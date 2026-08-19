using UnityEngine;

namespace Praxsuite
{
    /// <summary>
    /// Project-wide Praxsuite configuration, stored as a single asset the SDK finds on its own.
    ///
    /// Setup is one field: paste your Workspace ID. Everything else has a working default,
    /// and the publishable key is fetched from the workspace's public config endpoint at
    /// first use - so there is nothing secret in this asset and nothing to rotate in the
    /// project when a key changes.
    ///
    /// Create it from the menu: Praxsuite / Create Settings Asset, or edit it under
    /// Project Settings / Praxsuite. It must live at Resources/PraxsuiteSettings so it is
    /// included in the build and loadable at runtime.
    /// </summary>
    [CreateAssetMenu(fileName = "PraxsuiteSettings", menuName = "Praxsuite/Settings Asset", order = 1)]
    public class PraxsuiteSettings : ScriptableObject
    {
        /// <summary>Path inside a Resources folder, without extension.</summary>
        public const string ResourcePath = "PraxsuiteSettings";

        public enum HostPreset
        {
            /// <summary>gateway.praxsuite.com</summary>
            Cloud = 0,
            /// <summary>gateway.praxsuite.tesseractsoftwares.com</summary>
            Tesseract = 1,
            /// <summary>Use <see cref="customBaseUrl"/> - for a dedicated tier or a local backend.</summary>
            Custom = 2
        }

        // ------------------------------------------------------------- required

        [Header("Workspace")]
        [Tooltip("Your Praxsuite workspace GUID. The only value you must supply.\n" +
                 "Find it in the portal URL: /workspace/<this-guid>")]
        public string workspaceId = "";

        [Tooltip("Which Praxsuite tier hosts this workspace. A workspace exists on exactly " +
                 "one tier - the wrong host returns 404, not a helpful error.")]
        public HostPreset host = HostPreset.Cloud;

        [Tooltip("Gateway base URL. Used only when Host is set to Custom.")]
        public string customBaseUrl = "";

        // ------------------------------------------------------------- optional

        [Header("Publishable Key (optional)")]
        [Tooltip("Leave empty and the SDK fetches it from /auth/config at startup - one less " +
                 "thing to keep in sync.\n\n" +
                 "Set it explicitly when the workspace has several publishable keys and you " +
                 "need a specific one (auto-discovery returns the most recently created key).\n\n" +
                 "Only pk_live_ keys are accepted here. A secret key in this field fails the build.")]
        public string publishableKey = "";

        // ------------------------------------------------------------- transport

        [Header("Transport")]
        [Tooltip("Per-request timeout in seconds.")]
        [Range(1, 120)]
        public int timeoutSeconds = 30;

        [Tooltip("Retry attempts after the first try, for network errors, 5xx and 429. " +
                 "Quota exhaustion is never retried - retrying cannot fix it.")]
        [Range(0, 8)]
        public int maxRetries = 3;

        [Tooltip("Fetch the table schema on first use so you can address tables by name " +
                 "instead of pasting GUIDs. Turn off and call Prax.Schema.Register() to save " +
                 "one request at startup.")]
        public bool autoFetchSchema = true;

        // ------------------------------------------------------------- sessions

        [Header("Player Sessions")]
        [Tooltip("Keep a player signed in across app restarts.\n\n" +
                 "The refresh token is stored AES-encrypted under persistentDataPath with a " +
                 "device-derived key. That stops casual inspection and other apps on the " +
                 "device; it is not protection against the device's own owner, who can run a " +
                 "debugger against your process. Treat every client session as spoofable and " +
                 "keep authority on the server. See docs/security.md.")]
        public bool persistSession = true;

        [Tooltip("Refresh the access token this many seconds before it expires.")]
        [Range(10, 600)]
        public int refreshLeadSeconds = 60;

        // ------------------------------------------------------------- diagnostics

        [Header("Diagnostics")]
        [Tooltip("Log request and response bodies. Credentials and tokens are redacted, but " +
                 "player data is not - leave this off in shipped builds.")]
        public bool verboseLogging = false;

        // ------------------------------------------------------------- derived

        /// <summary>The gateway base URL implied by <see cref="host"/>.</summary>
        public string ResolvedBaseUrl
        {
            get
            {
                switch (host)
                {
                    case HostPreset.Tesseract: return PraxRoutes.TesseractHost;
                    case HostPreset.Custom: return PraxRoutes.NormalizeBaseUrl(customBaseUrl);
                    default: return PraxRoutes.CloudHost;
                }
            }
        }

        /// <summary>
        /// Human-readable reason this asset is not usable, or null when it is.
        /// Shown in the settings UI and checked again by the build guard.
        /// </summary>
        public string Validate()
        {
            if (string.IsNullOrWhiteSpace(workspaceId))
                return "Workspace ID is empty. Copy the GUID from your portal URL: /workspace/<guid>";

            if (!System.Guid.TryParse(workspaceId.Trim(), out _))
                return "Workspace ID is not a valid GUID: " + workspaceId;

            if (host == HostPreset.Custom && string.IsNullOrWhiteSpace(customBaseUrl))
                return "Host is Custom but Custom Base URL is empty.";

            if (!string.IsNullOrWhiteSpace(publishableKey))
            {
                var kind = PraxKeyGuard.Classify(publishableKey.Trim());
                if (kind == PraxKeyGuard.KeyKind.Secret)
                    return "This is a SECRET key (sk_live_). It would ship inside your build and " +
                           "hand every player full access to your workspace. Use a publishable " +
                           "key (pk_live_) instead.";
                if (kind != PraxKeyGuard.KeyKind.Publishable)
                    return "Publishable Key does not look like a pk_live_ key.";
            }

            return null;
        }
    }
}
