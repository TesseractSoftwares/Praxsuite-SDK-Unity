using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Praxsuite.Server
{
    /// <summary>
    /// Secret-key access, for a dedicated server build or an editor tool.
    ///
    /// Read this before using it.
    ///
    /// A secret key (sk_live_) carries the full scope of its credential. Anything holding one
    /// can read and write every table that credential reaches, with no row filter narrowing it
    /// to a single player. That is exactly what a game server needs and exactly what a game
    /// client must never have.
    ///
    /// Three separate mechanisms keep this out of player builds, because one is not enough:
    ///
    ///  1. Assembly platform scope. Praxsuite.Server.asmdef lists only Editor and standalone
    ///     desktop platforms, so this code does not compile at all for Android, iOS, WebGL or
    ///     console.
    ///  2. Define constraint. The same asmdef requires PRAXSUITE_SERVER. Without that define
    ///     the assembly is skipped even on a listed platform, so the default for every project
    ///     is that this type does not exist.
    ///  3. Build guard. Praxsuite.Editor/PraxBuildGuard fails the build when PRAXSUITE_SERVER
    ///     is set for a player-facing target, and when a secret key is found anywhere under
    ///     Assets. See docs/security.md.
    ///
    /// The key itself comes from the environment, never from an asset. A key in a
    /// ScriptableObject or a committed config file gets copied into builds and into version
    /// control, which is how these leak in practice - so this class will not read one from
    /// either.
    /// </summary>
    public static class PraxServer
    {
        /// <summary>Environment variable read by <see cref="InitializeFromEnvironment"/>.</summary>
        public const string SecretKeyVariable = "PRAXSUITE_SECRET_KEY";

        /// <summary>Environment variable holding the workspace GUID.</summary>
        public const string WorkspaceVariable = "PRAXSUITE_WORKSPACE_ID";

        /// <summary>Optional environment variable overriding the gateway base URL.</summary>
        public const string BaseUrlVariable = "PRAXSUITE_BASE_URL";

        private static string _secretKey;
        private static string _workspaceId;
        private static string _baseUrl = PraxRoutes.CloudHost;
        private static bool _initialized;

        /// <summary>True once a secret key has been supplied.</summary>
        public static bool IsInitialized => _initialized;

        /// <summary>
        /// Reads configuration from the process environment. The intended entry point: set the
        /// variables in your container, systemd unit, or launch script and call this once at
        /// startup.
        /// </summary>
        /// <param name="secretKeyFilePath">
        /// Optional path to a file containing the key - a Docker or Kubernetes secret mount, or
        /// a file outside the project tree. Read when the environment variable is absent.
        /// Anything under Assets/ is rejected: that path ends up inside builds.
        /// </param>
        public static void InitializeFromEnvironment(string secretKeyFilePath = null)
        {
            var key = Environment.GetEnvironmentVariable(SecretKeyVariable);

            if (string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(secretKeyFilePath))
                key = ReadKeyFile(secretKeyFilePath);

            if (string.IsNullOrWhiteSpace(key))
            {
                throw new PraxSecurityException(
                    "No secret key found.\n\n" +
                    "Set " + SecretKeyVariable + " in the server's environment, or pass a path to " +
                    "a secret file mounted outside the project.\n\n" +
                    "Do not put the key in a ScriptableObject, a Resources file, or anything else " +
                    "under Assets/ - all of those are copied into builds and committed to version " +
                    "control.");
            }

            var workspace = Environment.GetEnvironmentVariable(WorkspaceVariable);
            var baseUrl = Environment.GetEnvironmentVariable(BaseUrlVariable);

            // Fall back to the settings asset for the non-secret values: workspace id and host
            // are not secrets, and a dedicated server usually shares them with the client.
            if (string.IsNullOrWhiteSpace(workspace) || string.IsNullOrWhiteSpace(baseUrl))
            {
                var settings = Resources.Load<PraxsuiteSettings>(PraxsuiteSettings.ResourcePath);
                if (settings != null)
                {
                    if (string.IsNullOrWhiteSpace(workspace)) workspace = settings.workspaceId;
                    if (string.IsNullOrWhiteSpace(baseUrl)) baseUrl = settings.ResolvedBaseUrl;
                }
            }

            Initialize(workspace, key, baseUrl);
        }

        /// <summary>
        /// Configures explicitly. Prefer <see cref="InitializeFromEnvironment"/> - passing a key
        /// as a literal here puts it in your source, and therefore in your repository.
        /// </summary>
        public static void Initialize(string workspaceId, string secretKey, string baseUrl = null)
        {
            if (string.IsNullOrWhiteSpace(workspaceId))
                throw new ArgumentException("workspaceId is required.", nameof(workspaceId));
            if (string.IsNullOrWhiteSpace(secretKey))
                throw new ArgumentException("secretKey is required.", nameof(secretKey));

            var key = secretKey.Trim();
            var kind = PraxKeyGuard.Classify(key);

            if (kind != PraxKeyGuard.KeyKind.Secret)
            {
                // A publishable key here means the operator believes they have server access and
                // does not. Failing loudly beats writes silently narrowing to a row filter.
                throw new PraxSecurityException(
                    "PraxServer needs a secret key (" + PraxKeyGuard.SecretPrefix + "...), but got " +
                    (kind == PraxKeyGuard.KeyKind.Publishable
                        ? "a publishable key (pk_live_)."
                        : "something that is not a Praxsuite API key.") + "\n\n" +
                    "If you meant to use the publishable key, use the ordinary Prax API instead - " +
                    "it is designed for that. Create a server credential in the portal under " +
                    "API Gateway / Credentials.");
            }

            if (!Application.isEditor && !IsDedicatedServerBuild())
            {
                // Reached only if someone forced PRAXSUITE_SERVER onto a player build and
                // bypassed the build guard. Refuse at runtime as the last line of defence.
                throw new PraxSecurityException(
                    "PraxServer was initialised in a build that does not look like a dedicated " +
                    "server.\n\n" +
                    "A secret key in a build players can run gives every one of them full access " +
                    "to your workspace. Remove the PRAXSUITE_SERVER define from this build target, " +
                    "or build for the Dedicated Server platform.");
            }

            _workspaceId = workspaceId.Trim();
            _secretKey = key;
            _baseUrl = PraxRoutes.NormalizeBaseUrl(string.IsNullOrWhiteSpace(baseUrl)
                ? PraxRoutes.CloudHost
                : baseUrl);
            _initialized = true;

            PraxLog.Info("Server mode initialised for workspace " + _workspaceId +
                         " with credential " + PraxKeyGuard.Redact(_secretKey) + ".");
        }

        private static bool IsDedicatedServerBuild()
        {
#if UNITY_SERVER
            return true;
#else
            // A headless standalone launched with -batchmode is the pre-Unity-2021 way of
            // running a dedicated server, so accept it too.
            return Application.isBatchMode;
#endif
        }

        private static string ReadKeyFile(string path)
        {
            var full = Path.GetFullPath(path);

            var assets = Path.GetFullPath(Application.dataPath);
            if (full.StartsWith(assets, StringComparison.OrdinalIgnoreCase))
            {
                throw new PraxSecurityException(
                    "Refusing to read a secret key from inside the project data folder (" + full + ").\n\n" +
                    "Files there are copied into builds and usually committed. Mount the secret " +
                    "outside the project, or use the " + SecretKeyVariable + " environment variable.");
            }

            if (!File.Exists(full))
                throw new PraxSecurityException("No secret key file at " + full + ".");

            return File.ReadAllText(full).Trim();
        }

        // ------------------------------------------------------------------- data

        /// <summary>
        /// Runs a PraxQL request with the secret key - full credential scope, no row filter.
        ///
        /// Deliberately raw rather than a mirror of the client query builder. Server-side work
        /// is where cross-player queries and bulk writes happen, and having a distinct API
        /// surface makes those calls obvious in review instead of blending in with client code.
        /// Build requests with the same shape the gateway documents:
        ///
        /// <code>
        /// var result = await PraxServer.QueryAsync(new Dictionary&lt;string, object&gt;
        /// {
        ///     ["refs"] = new Dictionary&lt;string, object&gt; { ["t"] = tableId },
        ///     ["query"] = new Dictionary&lt;string, object&gt;
        ///     {
        ///         ["from"] = "t",
        ///         ["where"] = new List&lt;object&gt;
        ///         {
        ///             new Dictionary&lt;string, object&gt;
        ///                 { ["field"] = "Score", ["op"] = "gt", ["value"] = 1000 }
        ///         },
        ///         ["limit"] = 100
        ///     }
        /// });
        /// </code>
        /// </summary>
        public static async Task<Dictionary<string, object>> QueryAsync(
            Dictionary<string, object> request, CancellationToken ct = default)
        {
            RequireInitialized();
            if (request == null) throw new ArgumentNullException(nameof(request));

            return await SendAsync("POST", PraxRoutes.Query(_baseUrl, _workspaceId), request, ct)
                .ConfigureAwait(false);
        }

        /// <summary>Reads rows from a raw PraxQL response.</summary>
        public static IReadOnlyList<PraxRow> ReadRows(Dictionary<string, object> response)
        {
            return PraxRowReader.ReadRows(response);
        }

        /// <summary>Calls a gateway endpoint with the secret key.</summary>
        public static async Task<Dictionary<string, object>> CallEndpointAsync(string slug,
            object payload = null, CancellationToken ct = default)
        {
            RequireInitialized();
            if (string.IsNullOrWhiteSpace(slug))
                throw new ArgumentException("An endpoint slug is required.", nameof(slug));

            return await SendAsync("POST", PraxRoutes.Endpoint(_baseUrl, _workspaceId, slug.Trim()),
                payload, ct).ConfigureAwait(false);
        }

        /// <summary>Fetches the schema visible to the server credential.</summary>
        public static async Task<Dictionary<string, object>> GetSchemaAsync(CancellationToken ct = default)
        {
            RequireInitialized();
            return await SendAsync("GET", PraxRoutes.Schema(_baseUrl, _workspaceId), null, ct)
                .ConfigureAwait(false);
        }

        // --------------------------------------------------------------- transport

        /// <summary>
        /// Sends with the secret key attached.
        ///
        /// Server mode does not reuse PraxHttp: that path resolves credentials through the
        /// shared client, which is publishable-key-only by design. Keeping the two transports
        /// separate means there is no code path where a secret key could end up in a client
        /// request, or vice versa.
        /// </summary>
        private static async Task<Dictionary<string, object>> SendAsync(string method, string url,
            object body, CancellationToken ct)
        {
            using (var http = new System.Net.Http.HttpClient())
            {
                http.Timeout = TimeSpan.FromSeconds(60);
                http.DefaultRequestHeaders.Add("x-api-key", _secretKey);

                var request = new System.Net.Http.HttpRequestMessage(
                    new System.Net.Http.HttpMethod(method), url);

                if (body != null)
                {
                    request.Content = new System.Net.Http.StringContent(
                        PraxJson.Serialize(body), System.Text.Encoding.UTF8, "application/json");
                }

                System.Net.Http.HttpResponseMessage response;
                try
                {
                    response = await http.SendAsync(request, ct).ConfigureAwait(false);
                }
                catch (TaskCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new PraxException("TIMEOUT", "The request to " + url + " timed out.");
                }
                catch (System.Net.Http.HttpRequestException ex)
                {
                    throw new PraxException("NETWORK_ERROR",
                        "Could not reach the Praxsuite gateway: " + ex.Message + "\nURL: " + url);
                }

                var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    string code = null, message = null;
                    try
                    {
                        var parsed = PraxJson.ParseObject(text);
                        if (parsed.TryGetValue("error", out var errNode) &&
                            errNode is Dictionary<string, object> err)
                        {
                            code = err.TryGetValue("code", out var c) ? Convert.ToString(c) : null;
                            message = err.TryGetValue("message", out var m) ? Convert.ToString(m) : null;
                        }
                        else if (parsed.TryGetValue("message", out var envelope))
                        {
                            message = Convert.ToString(envelope);
                        }
                    }
                    catch (PraxJsonException)
                    {
                        message = text;
                    }

                    throw new PraxException(
                        code ?? "HTTP_" + (int)response.StatusCode,
                        message ?? ("The gateway returned HTTP " + (int)response.StatusCode + "."),
                        (int)response.StatusCode, null, text);
                }

                return string.IsNullOrEmpty(text)
                    ? new Dictionary<string, object>()
                    : PraxJson.ParseObject(text);
            }
        }

        private static void RequireInitialized()
        {
            if (_initialized) return;

            throw new PraxException("SERVER_NOT_INITIALIZED",
                "PraxServer has not been initialised. Call " +
                "PraxServer.InitializeFromEnvironment() once at server startup.");
        }
    }
}
