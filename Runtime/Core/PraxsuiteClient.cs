using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Praxsuite
{
    /// <summary>
    /// The SDK's single instance: configuration, credential resolution, and the player session.
    ///
    /// You do not normally construct or configure this. It initialises itself from
    /// <c>Resources/PraxsuiteSettings</c> the first time any <c>Prax.*</c> call is made, so a
    /// game needs no boot script and no ordering rules between scripts. Call
    /// <see cref="Configure(PraxsuiteOptions)"/> only when configuration has to come from
    /// somewhere else at runtime, such as a launcher argument or a remote config service.
    /// </summary>
    public class PraxsuiteClient
    {
        private static PraxsuiteClient _instance;
        private static readonly object InitGate = new object();

        // -------------------------------------------------------------- lifecycle

        internal static PraxsuiteClient Instance
        {
            get
            {
                if (_instance != null) return _instance;

                lock (InitGate)
                {
                    if (_instance == null) _instance = CreateFromSettingsAsset();
                    return _instance;
                }
            }
        }

        /// <summary>True once configuration has been resolved (from the asset or explicitly).</summary>
        public static bool IsConfigured => _instance != null;

        /// <summary>
        /// Configures the SDK explicitly, replacing anything loaded from the settings asset.
        /// Any in-flight session is discarded.
        /// </summary>
        public static void Configure(PraxsuiteOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            options.Validate();

            lock (InitGate)
            {
                _instance = new PraxsuiteClient(options);
            }
            PraxLog.Info("Configured for workspace " + options.WorkspaceId + " at " + options.BaseUrl);
        }

        /// <summary>
        /// Resets the SDK: drops configuration, cached schema and the in-memory session.
        /// Intended for tests and for editor play-mode restarts. Does not delete a persisted
        /// session - use <c>Prax.Auth.SignOutAsync()</c> for that.
        /// </summary>
        public static void Reset()
        {
            lock (InitGate) _instance = null;
        }

        private static PraxsuiteClient CreateFromSettingsAsset()
        {
            var settings = Resources.Load<PraxsuiteSettings>(PraxsuiteSettings.ResourcePath);

            if (settings == null)
            {
                throw new PraxException("NOT_CONFIGURED",
                    "Praxsuite is not configured.\n\n" +
                    "Create the settings asset (menu: Praxsuite / Create Settings Asset, or " +
                    "Project Settings / Praxsuite) and paste your Workspace ID into it. It must " +
                    "live at Assets/Resources/PraxsuiteSettings.asset so it is included in builds.\n\n" +
                    "Alternatively call PraxsuiteClient.Configure(new PraxsuiteOptions { ... }) " +
                    "before your first Prax call.");
            }

            var problem = settings.Validate();
            if (problem != null)
                throw new PraxException("INVALID_CONFIG", "PraxsuiteSettings is not usable: " + problem);

            return new PraxsuiteClient(PraxsuiteOptions.FromSettings(settings));
        }

        // ---------------------------------------------------------------- instance

        internal readonly string WorkspaceId;
        internal readonly string BaseUrl;
        internal readonly int TimeoutSeconds;
        internal readonly int MaxRetries;
        internal readonly int RefreshLeadSeconds;
        internal readonly bool AutoFetchSchema;

        internal readonly IPraxTokenStore TokenStore;
        internal readonly PraxSchema Schema;

        // Feature modules, created once with this client. Exposed through the Prax facade.
        internal readonly PraxAuth AuthModule;
        internal readonly PraxData DataModule;
        internal readonly PraxEndpoints EndpointsModule;
        internal readonly PraxFiles FilesModule;
        internal readonly PraxPlayers PlayersModule;

        /// <summary>Raised after a successful sign-in or a session restored from disk.</summary>
        public event Action<PraxSession> SignedIn;

        /// <summary>Raised on sign-out, and when a session is dropped as unrecoverable.</summary>
        public event Action SignedOut;

        private string _publishableKey;
        private PraxSession _session;
        private bool _sessionLoaded;

        private Task<string> _keyDiscovery;
        private Task<bool> _refreshInFlight;
        private readonly object _sessionGate = new object();
        private readonly object _keyGate = new object();

        private PraxsuiteClient(PraxsuiteOptions options)
        {
            WorkspaceId = options.WorkspaceId.Trim();
            BaseUrl = PraxRoutes.NormalizeBaseUrl(options.BaseUrl);
            TimeoutSeconds = options.TimeoutSeconds;
            MaxRetries = options.MaxRetries;
            RefreshLeadSeconds = options.RefreshLeadSeconds;
            AutoFetchSchema = options.AutoFetchSchema;

            PraxLog.Minimum = options.VerboseLogging ? PraxLog.Level.Verbose : PraxLog.Level.Warning;

            if (!string.IsNullOrWhiteSpace(options.PublishableKey))
            {
                var key = options.PublishableKey.Trim();
                // Defence in depth: the settings validator and the build guard both reject a
                // secret key, but a key supplied at runtime has passed through neither.
                PraxKeyGuard.RequireClientSafe(key, "PraxsuiteOptions.PublishableKey");
                _publishableKey = key;
            }

            TokenStore = options.TokenStore ??
                         (options.PersistSession
                             ? (IPraxTokenStore)new PraxEncryptedFileTokenStore(WorkspaceId)
                             : new PraxMemoryTokenStore());

            Schema = new PraxSchema(this);
            AuthModule = new PraxAuth(this);
            DataModule = new PraxData(this);
            EndpointsModule = new PraxEndpoints(this);
            FilesModule = new PraxFiles(this);
            PlayersModule = new PraxPlayers(this);

            if (PraxRoutes.IsInsecureRemote(BaseUrl))
            {
                // Plaintext to a remote host would put the publishable key and every player's
                // session token on the wire in clear. Loopback is allowed for local backends.
                throw new PraxSecurityException(
                    "Refusing to use a plaintext http:// gateway URL for a remote host (" + BaseUrl + ").\n" +
                    "API keys and player session tokens would travel unencrypted. Use https://, " +
                    "or point at localhost for local development.");
            }
        }

        // ------------------------------------------------------------- credentials

        /// <summary>
        /// Picks the credential for a request: the player's access token when one is wanted
        /// and available, otherwise the workspace publishable key.
        /// </summary>
        internal async Task<string> ResolveCredentialAsync(PraxHttp.AuthMode mode, CancellationToken ct)
        {
            if (mode == PraxHttp.AuthMode.None) return null;

            if (mode == PraxHttp.AuthMode.PreferSession)
            {
                var session = CurrentSession;
                if (session != null && session.HasAccessToken)
                {
                    // Refresh proactively so the request does not spend a round trip on a 401.
                    if (session.IsAccessStale(RefreshLeadSeconds) && session.HasRefreshToken)
                    {
                        await TryRefreshSessionAsync(ct).ConfigureAwait(false);
                        session = CurrentSession;
                    }

                    if (session != null && session.HasAccessToken) return session.accessToken;
                }
            }

            return await GetPublishableKeyAsync(ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Returns the workspace publishable key, discovering it from the public
        /// <c>/auth/config</c> endpoint when the project did not supply one.
        ///
        /// This is what makes Workspace ID the only required setting. The endpoint is
        /// deliberately unauthenticated and returns only public information (the pk_live_
        /// key plus workspace branding), the same way a Stripe publishable key is public.
        /// Discovery runs once per process; concurrent callers share one request.
        /// </summary>
        internal Task<string> GetPublishableKeyAsync(CancellationToken ct)
        {
            if (!string.IsNullOrEmpty(_publishableKey)) return Task.FromResult(_publishableKey);

            // A dedicated lock, not the static InitGate: this is per-instance state, and taking
            // the construction lock here would let a discovery request block Instance resolution.
            lock (_keyGate)
            {
                if (!string.IsNullOrEmpty(_publishableKey)) return Task.FromResult(_publishableKey);
                if (_keyDiscovery != null && !_keyDiscovery.IsFaulted) return _keyDiscovery;
                _keyDiscovery = DiscoverPublishableKeyAsync(ct);
                return _keyDiscovery;
            }
        }

        private async Task<string> DiscoverPublishableKeyAsync(CancellationToken ct)
        {
            PraxLog.Info("No publishable key configured; fetching the workspace's public config.");

            Dictionary<string, object> body;
            try
            {
                body = await PraxHttp.SendJsonAsync("GET",
                    PraxRoutes.Auth(BaseUrl, WorkspaceId, "config"),
                    null, PraxHttp.AuthMode.None, ct).ConfigureAwait(false);
            }
            catch (PraxException ex) when (ex.StatusCode == 404)
            {
                throw new PraxException("WORKSPACE_NOT_FOUND",
                    "Workspace " + WorkspaceId + " was not found at " + BaseUrl + ".\n\n" +
                    "Two things cause this. Either the Workspace ID is wrong, or the workspace " +
                    "lives on a different Praxsuite tier - a workspace hosted on the Tesseract " +
                    "tier returns 404 on the Cloud host and vice versa.\n\n" +
                    "Check the Host setting in PraxsuiteSettings against the URL shown in your " +
                    "workspace's API Gateway settings page.", 404);
            }

            var key = PraxHttp.AsString(body, "publicKey");
            if (string.IsNullOrEmpty(key))
            {
                throw new PraxException("NO_PUBLISHABLE_KEY",
                    "Workspace " + WorkspaceId + " has no active publishable key.\n\n" +
                    "Create one in the portal under API Gateway / Credentials (a client key, " +
                    "pk_live_), grant it the table scopes your game needs, then either leave " +
                    "PraxsuiteSettings.publishableKey empty to pick it up automatically or paste " +
                    "it in explicitly.");
            }

            // The endpoint is public and returns the newest active client key. If a workspace
            // somehow serves a secret key here, refuse it rather than embed it in requests.
            PraxKeyGuard.RequireClientSafe(key, "the workspace public config endpoint");

            _publishableKey = key;
            PraxLog.Info("Resolved publishable key " + PraxKeyGuard.Redact(key) + ".");
            return key;
        }

        // ---------------------------------------------------------------- sessions

        /// <summary>The current player session, loading a persisted one on first access.</summary>
        internal PraxSession CurrentSession
        {
            get
            {
                lock (_sessionGate)
                {
                    if (!_sessionLoaded)
                    {
                        _sessionLoaded = true;
                        _session = TokenStore.Load();

                        if (_session != null && _session.IsRefreshExpired())
                        {
                            PraxLog.Info("The stored session's refresh token has expired; discarding it.");
                            _session = null;
                            TokenStore.Clear();
                        }
                        else if (_session != null)
                        {
                            PraxLog.Info("Restored a stored session for " + (_session.email ?? _session.endUserId) + ".");
                            RaiseSignedIn(_session);
                        }
                    }
                    return _session;
                }
            }
        }

        internal void SetSession(PraxSession session)
        {
            lock (_sessionGate)
            {
                _session = session;
                _sessionLoaded = true;
                TokenStore.Save(session);
            }

            if (session != null) RaiseSignedIn(session);
            else RaiseSignedOut();
        }

        internal void ClearSession()
        {
            lock (_sessionGate)
            {
                _session = null;
                _sessionLoaded = true;
                TokenStore.Clear();
            }
            RaiseSignedOut();
        }

        /// <summary>
        /// Exchanges the refresh token for a new pair.
        ///
        /// Refresh tokens rotate: the gateway invalidates the old one as it issues the new
        /// one. Two concurrent refreshes would therefore race, with the loser holding a
        /// token the server has already retired - so callers share a single in-flight
        /// refresh. Returns false when the session cannot be recovered, in which case it has
        /// been cleared and the player must sign in again.
        /// </summary>
        internal Task<bool> TryRefreshSessionAsync(CancellationToken ct)
        {
            lock (_sessionGate)
            {
                if (_refreshInFlight != null && !_refreshInFlight.IsCompleted) return _refreshInFlight;

                var session = _session;
                if (session == null || !session.HasRefreshToken) return Task.FromResult(false);

                _refreshInFlight = RefreshCoreAsync(session.refreshToken, ct);
                return _refreshInFlight;
            }
        }

        private async Task<bool> RefreshCoreAsync(string refreshToken, CancellationToken ct)
        {
            try
            {
                var body = await PraxHttp.SendJsonAsync("POST",
                    PraxRoutes.Auth(BaseUrl, WorkspaceId, "refresh"),
                    new Dictionary<string, object> { { "refreshToken", refreshToken } },
                    PraxHttp.AuthMode.ApiKey, ct).ConfigureAwait(false);

                var session = PraxAuthMapper.ToSession(body);
                if (session == null || !session.HasAccessToken)
                {
                    PraxLog.Warn("The refresh response carried no access token; signing the player out.");
                    ClearSession();
                    return false;
                }

                // Carry over profile fields the refresh response may omit.
                lock (_sessionGate)
                {
                    if (_session != null) PraxAuthMapper.CarryOverProfile(_session, session);
                }

                SetSession(session);
                PraxLog.Info("Session refreshed.");
                return true;
            }
            catch (PraxException ex)
            {
                if (ex.IsTransient)
                {
                    // Keep the session: the token may still be good once the network recovers.
                    PraxLog.Warn("Could not refresh the session right now (" + ex.Code + "). Keeping it.");
                    return false;
                }

                PraxLog.Info("The refresh token was rejected (" + ex.Code + "); signing the player out.");
                ClearSession();
                return false;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        private void RaiseSignedIn(PraxSession session)
        {
            var handler = SignedIn;
            if (handler == null) return;
            PraxDispatcher.Post(() =>
            {
                try { handler(session); }
                catch (Exception ex) { PraxLog.Error("A SignedIn handler threw.", ex); }
            });
        }

        private void RaiseSignedOut()
        {
            var handler = SignedOut;
            if (handler == null) return;
            PraxDispatcher.Post(() =>
            {
                try { handler(); }
                catch (Exception ex) { PraxLog.Error("A SignedOut handler threw.", ex); }
            });
        }
    }
}
