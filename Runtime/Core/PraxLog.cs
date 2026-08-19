using System;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Praxsuite
{
    /// <summary>
    /// SDK logging. Every message passes through <see cref="Scrub"/> so a credential or
    /// bearer token can never reach the Unity console, a crash reporter, or a log file
    /// that a player might upload with a bug report.
    /// </summary>
    public static class PraxLog
    {
        public enum Level { Off = 0, Error = 1, Warning = 2, Info = 3, Verbose = 4 }

        /// <summary>
        /// Defaults to Warning. Raised to Info by <c>PraxsuiteSettings.verboseLogging</c>.
        /// Verbose logs request and response bodies, so keep it off in shipped builds.
        /// </summary>
        public static Level Minimum = Level.Warning;

        private const string Tag = "[Praxsuite] ";

        // pk_live_/sk_live_ keys, plus anything that looks like a JWT.
        private static readonly Regex KeyPattern = new Regex(
            @"\b(pk|sk)_live_[A-Za-z0-9]+", RegexOptions.Compiled);

        private static readonly Regex JwtPattern = new Regex(
            @"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]+", RegexOptions.Compiled);

        // "refreshToken":"...", "accessToken":"...", "password":"...", "sessionToken":"..."
        private static readonly Regex SecretFieldPattern = new Regex(
            "\"(refreshToken|accessToken|password|newPassword|currentPassword|confirmPassword|sessionToken|publicKey)\"\\s*:\\s*\"[^\"]*\"",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Removes credentials from a string. Public because callers building their own
        /// diagnostics should run untrusted text through it too.
        /// </summary>
        public static string Scrub(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            text = KeyPattern.Replace(text, m => m.Groups[1].Value + "_live_<redacted>");
            text = JwtPattern.Replace(text, "<jwt redacted>");
            text = SecretFieldPattern.Replace(text, m =>
            {
                var colon = m.Value.IndexOf(':');
                return m.Value.Substring(0, colon + 1) + "\"<redacted>\"";
            });
            return text;
        }

        public static void Error(string message)
        {
            if (Minimum >= Level.Error) Debug.LogError(Tag + Scrub(message));
        }

        public static void Error(string message, Exception ex)
        {
            if (Minimum < Level.Error) return;
            Debug.LogError(Tag + Scrub(message) + "\n" + Scrub(ex == null ? "" : ex.ToString()));
        }

        public static void Warn(string message)
        {
            if (Minimum >= Level.Warning) Debug.LogWarning(Tag + Scrub(message));
        }

        public static void Info(string message)
        {
            if (Minimum >= Level.Info) Debug.Log(Tag + Scrub(message));
        }

        public static void Verbose(string message)
        {
            if (Minimum >= Level.Verbose) Debug.Log(Tag + Scrub(message));
        }
    }
}
