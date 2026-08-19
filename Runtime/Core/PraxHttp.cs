using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace Praxsuite
{
    /// <summary>
    /// The SDK's transport. One place that knows how to talk to the gateway, so retry
    /// policy, credential handling, redaction and error shaping are decided once.
    /// </summary>
    internal static class PraxHttp
    {
        private const string ApiKeyHeader = "x-api-key";
        private const int MaxBackoffSeconds = 30;

        // Backoff jitter is computed after an await, so it may run on a worker thread.
        // UnityEngine.Random is main-thread-only and would throw there; System.Random is not
        // thread-safe either, so it is guarded.
        private static readonly System.Random Jitter = new System.Random();
        private static readonly object JitterGate = new object();

        private static double NextJitter()
        {
            lock (JitterGate) return Jitter.NextDouble();
        }

        internal class Response
        {
            public long Status;
            public string Body;
            public Dictionary<string, string> Headers;
            public bool Ok => Status >= 200 && Status < 300;
        }

        internal enum AuthMode
        {
            /// <summary>Send the workspace publishable key. Used by /auth/* and unauthenticated reads.</summary>
            ApiKey,
            /// <summary>Send the signed-in player's access token, falling back to the api key.</summary>
            PreferSession,
            /// <summary>Send no credential at all. Only /auth/config and /auth/logo allow this.</summary>
            None
        }

        // ------------------------------------------------------------------ send

        /// <summary>
        /// Sends a JSON request and returns the parsed body.
        ///
        /// Handles, in order: main-thread marshalling, credential selection, one silent
        /// token refresh on 401, retry with exponential backoff and jitter for transient
        /// failures, and mapping any non-2xx into a <see cref="PraxException"/> whose
        /// <c>Code</c> matches what the gateway sent.
        /// </summary>
        internal static async Task<Dictionary<string, object>> SendJsonAsync(
            string method,
            string url,
            object body,
            AuthMode authMode,
            CancellationToken ct = default)
        {
            var response = await SendAsync(method, url, body, authMode, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(response.Body)) return new Dictionary<string, object>();

            try
            {
                return PraxJson.ParseObject(response.Body);
            }
            catch (PraxJsonException ex)
            {
                throw new PraxException("MALFORMED_RESPONSE",
                    "The gateway returned a body that is not valid JSON. " + ex.Message,
                    (int)response.Status, null, response.Body);
            }
        }

        internal static async Task<Response> SendAsync(
            string method,
            string url,
            object body,
            AuthMode authMode,
            CancellationToken ct = default)
        {
            var client = PraxsuiteClient.Instance;
            var payload = body == null ? null : Encoding.UTF8.GetBytes(PraxJson.Serialize(body));

            var attempts = client.MaxRetries + 1;
            var refreshAttempted = false;

            for (var attempt = 1; ; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                var credential = await client.ResolveCredentialAsync(authMode, ct).ConfigureAwait(false);

                if (PraxLog.Minimum >= PraxLog.Level.Verbose)
                {
                    PraxLog.Verbose(method + " " + url +
                                    " auth=" + PraxKeyGuard.Redact(credential) +
                                    (payload == null ? "" : " body=" + Encoding.UTF8.GetString(payload)));
                }

                var response = await SendOnceAsync(method, url, payload, "application/json",
                    credential, client.TimeoutSeconds, ct).ConfigureAwait(false);

                if (response.Ok)
                {
                    PraxLog.Verbose("HTTP " + response.Status + " <- " + url +
                                    " (" + (response.Body == null ? 0 : response.Body.Length) + " bytes)");
                    return response;
                }

                var error = BuildError(response);

                // A 401 on a session-backed call usually means the access token aged out
                // between our expiry check and the server's. Refresh once and replay; if the
                // refresh itself fails the session is genuinely gone.
                if (response.Status == 401 && authMode == AuthMode.PreferSession && !refreshAttempted)
                {
                    refreshAttempted = true;
                    if (await client.TryRefreshSessionAsync(ct).ConfigureAwait(false))
                    {
                        PraxLog.Info("Access token was rejected; refreshed the session and retrying.");
                        continue;
                    }
                }

                if (!error.IsTransient || attempt >= attempts) throw error;

                var delay = ResolveBackoff(response, attempt);
                PraxLog.Warn("Attempt " + attempt + "/" + attempts + " failed (" + error.Code +
                             "). Retrying in " + delay.ToString("0.##", CultureInfo.InvariantCulture) + "s.");
                await DelayAsync(delay, ct).ConfigureAwait(false);
            }
        }

        /// <summary>Uploads raw bytes as multipart/form-data under the field name "file".</summary>
        internal static async Task<Dictionary<string, object>> SendMultipartAsync(
            string url,
            byte[] fileBytes,
            string fileName,
            string contentType,
            AuthMode authMode,
            CancellationToken ct = default)
        {
            var client = PraxsuiteClient.Instance;
            var credential = await client.ResolveCredentialAsync(authMode, ct).ConfigureAwait(false);

            var form = new List<IMultipartFormSection>
            {
                new MultipartFormFileSection("file", fileBytes, fileName, contentType)
            };

            var response = await RunOnMainThreadAsync(() =>
            {
                var request = UnityWebRequest.Post(url, form);
                ApplyCredential(request, credential);
                request.timeout = client.TimeoutSeconds;
                return request;
            }, ct).ConfigureAwait(false);

            if (!response.Ok) throw BuildError(response);
            return string.IsNullOrEmpty(response.Body)
                ? new Dictionary<string, object>()
                : PraxJson.ParseObject(response.Body);
        }

        /// <summary>Downloads raw bytes (file content). Bypasses JSON parsing.</summary>
        internal static async Task<byte[]> GetBytesAsync(string url, AuthMode authMode,
            CancellationToken ct = default)
        {
            var client = PraxsuiteClient.Instance;
            var credential = await client.ResolveCredentialAsync(authMode, ct).ConfigureAwait(false);

            byte[] data = null;
            var response = await RunOnMainThreadAsync(() =>
            {
                var request = new UnityWebRequest(url, "GET")
                {
                    downloadHandler = new DownloadHandlerBuffer(),
                    timeout = client.TimeoutSeconds
                };
                ApplyCredential(request, credential);
                return request;
            }, ct, request => data = request.downloadHandler.data).ConfigureAwait(false);

            if (!response.Ok) throw BuildError(response);
            return data ?? Array.Empty<byte>();
        }

        // ------------------------------------------------------------- single send

        private static Task<Response> SendOnceAsync(string method, string url, byte[] payload,
            string contentType, string credential, int timeoutSeconds, CancellationToken ct)
        {
            return RunOnMainThreadAsync(() =>
            {
                var request = new UnityWebRequest(url, method)
                {
                    downloadHandler = new DownloadHandlerBuffer(),
                    timeout = timeoutSeconds
                };

                if (payload != null)
                {
                    request.uploadHandler = new UploadHandlerRaw(payload);
                    request.SetRequestHeader("Content-Type", contentType);
                }

                request.SetRequestHeader("Accept", "application/json");
                ApplyCredential(request, credential);
                return request;
            }, ct);
        }

        private static void ApplyCredential(UnityWebRequest request, string credential)
        {
            if (string.IsNullOrEmpty(credential)) return;

            // The gateway accepts either header. x-api-key is used for keys and Authorization
            // for session tokens, matching how the backend middleware documents them - the
            // distinction keeps gateway access logs readable.
            if (PraxKeyGuard.Classify(credential) == PraxKeyGuard.KeyKind.EndUserJwt)
                request.SetRequestHeader("Authorization", "Bearer " + credential);
            else
                request.SetRequestHeader(ApiKeyHeader, credential);
        }

        /// <summary>
        /// Creates the request on the main thread, awaits it, and hands the finished
        /// UnityWebRequest to an optional reader before disposing it.
        /// </summary>
        private static Task<Response> RunOnMainThreadAsync(Func<UnityWebRequest> factory,
            CancellationToken ct, Action<UnityWebRequest> reader = null)
        {
            var tcs = new TaskCompletionSource<Response>(TaskCreationOptions.RunContinuationsAsynchronously);

            PraxDispatcher.Run(() =>
            {
                UnityWebRequest request;
                try
                {
                    request = factory();
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                    return;
                }
                PraxDispatcher.StartRoutine(Drive(request, tcs, ct, reader));
            });

            return tcs.Task;
        }

        private static IEnumerator Drive(UnityWebRequest request, TaskCompletionSource<Response> tcs,
            CancellationToken ct, Action<UnityWebRequest> reader)
        {
            using (request)
            {
                var operation = request.SendWebRequest();

                while (!operation.isDone)
                {
                    if (ct.IsCancellationRequested)
                    {
                        request.Abort();
                        tcs.TrySetCanceled(ct);
                        yield break;
                    }
                    yield return null;
                }

                // A protocol error still carries a status and body worth reading, so only
                // connection and data-processing errors become transport failures here.
                var isTransport =
#if UNITY_2020_2_OR_NEWER
                    request.result == UnityWebRequest.Result.ConnectionError ||
                    request.result == UnityWebRequest.Result.DataProcessingError;
#else
                    request.isNetworkError;
#endif

                if (isTransport)
                {
                    var timedOut = !string.IsNullOrEmpty(request.error) &&
                                   request.error.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0;
                    tcs.TrySetException(new PraxException(
                        timedOut ? "TIMEOUT" : "NETWORK_ERROR",
                        "Could not reach the Praxsuite gateway: " + request.error +
                        "\nURL: " + request.url,
                        0));
                    yield break;
                }

                try
                {
                    reader?.Invoke(request);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                    yield break;
                }

                tcs.TrySetResult(new Response
                {
                    Status = request.responseCode,
                    Body = request.downloadHandler != null ? request.downloadHandler.text : null,
                    Headers = request.GetResponseHeaders()
                });
            }
        }

        // ------------------------------------------------------------------ errors

        /// <summary>
        /// Maps a non-2xx response to a typed error.
        ///
        /// The gateway is not uniform: /query returns a bare {error:{code,message,details}},
        /// while /auth/* and management routes return the platform envelope
        /// {isSuccess,message,errors,data}. Both shapes are handled so callers see one
        /// consistent exception either way.
        /// </summary>
        private static PraxException BuildError(Response response)
        {
            var status = (int)response.Status;
            string code = null;
            string message = null;
            List<string> details = null;

            if (!string.IsNullOrEmpty(response.Body))
            {
                try
                {
                    var root = PraxJson.ParseObject(response.Body);

                    // Shape 1: PraxQL - {"error":{"code":..,"message":..,"details":[..]}}
                    if (root.TryGetValue("error", out var errNode) &&
                        errNode is Dictionary<string, object> err)
                    {
                        code = AsString(err, "code");
                        message = AsString(err, "message");
                        details = AsStringList(err, "details");
                    }
                    // Shape 2: platform envelope - {"isSuccess":false,"message":..,"errors":[..]}
                    else
                    {
                        message = AsString(root, "message");
                        details = AsStringList(root, "errors");
                    }

                    // Shape 3: files controller - {"error":"..."}
                    if (message == null && errNode is string plain) message = plain;
                }
                catch (PraxJsonException)
                {
                    // Non-JSON body (an HTML error page from an edge proxy, most likely).
                    message = Truncate(response.Body, 400);
                }
            }

            if (string.IsNullOrEmpty(code)) code = "HTTP_" + status;
            if (string.IsNullOrEmpty(message)) message = DescribeStatus(status);

            return new PraxException(code, message, status, details, response.Body);
        }

        private static string DescribeStatus(int status)
        {
            switch (status)
            {
                case 400: return "The gateway rejected the request as malformed.";
                case 401: return "Not authenticated. The API key or session token is missing, " +
                                 "expired, or does not belong to this workspace.";
                case 403: return "Authenticated, but not permitted. Check the credential's or " +
                                 "role's table scopes in API Gateway settings.";
                case 404: return "Not found. Verify the workspace ID and that you are pointed at " +
                                 "the tier that actually hosts it - a workspace on another tier " +
                                 "returns 404 here.";
                case 413: return "The payload is larger than the workspace plan allows.";
                case 429: return "Rate limited or out of plan allowance.";
                case 500: return "The gateway hit an internal error.";
                case 502:
                case 503:
                case 504: return "The gateway is unavailable or timed out upstream.";
                default: return "The gateway returned HTTP " + status + ".";
            }
        }

        // ----------------------------------------------------------------- backoff

        /// <summary>
        /// Exponential backoff with +/-25% jitter, capped. Honours Retry-After when the
        /// gateway sends one, since it knows better than we do.
        /// </summary>
        private static double ResolveBackoff(Response response, int attempt)
        {
            if (response.Headers != null &&
                response.Headers.TryGetValue("Retry-After", out var retryAfter) &&
                double.TryParse(retryAfter, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) &&
                seconds > 0)
            {
                return Math.Min(seconds, MaxBackoffSeconds);
            }

            var backoff = Math.Pow(2, attempt - 1);
            var jitter = backoff * 0.25 * (NextJitter() * 2 - 1);
            return Math.Min(Math.Max(backoff + jitter, 0.1), MaxBackoffSeconds);
        }

        private static Task DelayAsync(double seconds, CancellationToken ct)
        {
            return Task.Delay(TimeSpan.FromSeconds(seconds), ct);
        }

        // ------------------------------------------------------------------ helpers

        internal static string AsString(Dictionary<string, object> map, string key)
        {
            if (map == null || !map.TryGetValue(key, out var value) || value == null) return null;
            return value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static List<string> AsStringList(Dictionary<string, object> map, string key)
        {
            if (map == null || !map.TryGetValue(key, out var value)) return null;
            if (!(value is List<object> list) || list.Count == 0) return null;

            var result = new List<string>(list.Count);
            foreach (var item in list)
                if (item != null) result.Add(Convert.ToString(item, CultureInfo.InvariantCulture));
            return result.Count > 0 ? result : null;
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
            return s.Substring(0, max) + "...";
        }
    }
}
