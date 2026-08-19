using System.Threading;
using System.Threading.Tasks;

namespace Praxsuite
{
    /// <summary>
    /// The Praxsuite SDK.
    ///
    /// Setup is one field. Create the settings asset (menu: Praxsuite / Create Settings Asset),
    /// paste your Workspace ID, and start calling - the SDK initialises itself on first use,
    /// so there is no boot script to add and no ordering to get right between scripts.
    ///
    /// <code>
    /// // Sign a player in
    /// var result = await Prax.Auth.LoginAsync(email, password);
    ///
    /// // Read their own save (the row filter scopes this to them, server-side)
    /// var save = await Prax.Data.From("PlayerSaves").FirstAsync();
    ///
    /// // Write something only the server may decide
    /// var reward = await Prax.Endpoints.CallAsync("claim-daily-reward");
    /// </code>
    ///
    /// Everything returns a <c>Task</c>, so it composes with async/await, coroutines
    /// (<c>yield return task.AsCoroutine()</c>) and UniTask alike. Failures throw
    /// <see cref="PraxException"/>, which carries a stable <c>Code</c> and predicates such as
    /// <c>IsRateLimited</c> and <c>IsQuotaExceeded</c> so you never have to match on message text.
    ///
    /// On security: the client is untrusted, and this SDK is built on that assumption rather
    /// than around it. Ship only a publishable key, give players their own tokens, and keep
    /// anything valuable behind <see cref="Endpoints"/>. See docs/security.md.
    /// </summary>
    public static class Prax
    {
        /// <summary>Player accounts: register, sign in, sessions, password flows, OIDC.</summary>
        public static PraxAuth Auth => PraxsuiteClient.Instance.AuthModule;

        /// <summary>Table reads and writes.</summary>
        public static PraxData Data => PraxsuiteClient.Instance.DataModule;

        /// <summary>Gateway endpoints - the server-authoritative path.</summary>
        public static PraxEndpoints Endpoints => PraxsuiteClient.Instance.EndpointsModule;

        /// <summary>File upload and download.</summary>
        public static PraxFiles Files => PraxsuiteClient.Instance.FilesModule;

        /// <summary>Platform identity links, for analytics and account linking.</summary>
        public static PraxPlayers Players => PraxsuiteClient.Instance.PlayersModule;

        /// <summary>Table name to id mapping.</summary>
        public static PraxSchema Schema => PraxsuiteClient.Instance.Schema;

        /// <summary>True once the SDK has resolved its configuration.</summary>
        public static bool IsConfigured => PraxsuiteClient.IsConfigured;

        /// <summary>The configured workspace id.</summary>
        public static string WorkspaceId => PraxsuiteClient.Instance.WorkspaceId;

        /// <summary>The gateway base URL in use.</summary>
        public static string BaseUrl => PraxsuiteClient.Instance.BaseUrl;

        /// <summary>
        /// Configures the SDK explicitly instead of reading the settings asset. Only needed
        /// when configuration arrives at runtime - a launcher argument, remote config, a test.
        /// </summary>
        public static void Configure(PraxsuiteOptions options) => PraxsuiteClient.Configure(options);

        /// <summary>
        /// Verifies the SDK can reach the workspace, and warms the publishable key and schema
        /// so the first real call is not paying for both.
        ///
        /// Optional, but worth awaiting behind a loading screen: a misconfigured workspace id
        /// or host then fails there, with a message that says which, rather than in the middle
        /// of a sign-in screen.
        /// </summary>
        public static async Task<PraxWorkspaceConfig> InitializeAsync(CancellationToken ct = default)
        {
            var client = PraxsuiteClient.Instance;

            var config = await PraxAuthStatic.GetWorkspaceConfigAsync(ct).ConfigureAwait(false);

            if (client.AutoFetchSchema)
            {
                try
                {
                    await client.Schema.FetchAsync(false, ct).ConfigureAwait(false);
                }
                catch (PraxException ex)
                {
                    // A missing schema is recoverable: names can still be registered by hand,
                    // and GUIDs always work. Do not fail startup over it.
                    PraxLog.Warn("Could not load the schema during initialisation (" + ex.Code +
                                 "). Address tables by GUID, or register names with " +
                                 "Prax.Schema.Register().");
                }
            }

            PraxLog.Info("Ready. Workspace '" + (config.WorkspaceName ?? client.WorkspaceId) +
                         "' at " + client.BaseUrl + ".");
            return config;
        }

        /// <summary>
        /// Drops configuration, the cached schema and the in-memory session. For tests and
        /// editor play-mode restarts. Does not delete a persisted session - use
        /// <c>Prax.Auth.SignOutAsync()</c> for that.
        /// </summary>
        public static void Reset() => PraxsuiteClient.Reset();
    }
}
