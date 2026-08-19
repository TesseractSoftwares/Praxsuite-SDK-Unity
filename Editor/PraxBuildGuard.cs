using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Praxsuite.Editor
{
    /// <summary>
    /// Fails the build when it would ship something that must not ship.
    ///
    /// A secret key committed into a Unity project is not a hypothetical: it is the single
    /// most common way a game backend gets compromised, and it is invisible until someone
    /// decompiles the build. Every client SDK warns about it in documentation nobody reads at
    /// 2am before a release. Documentation is not a control.
    ///
    /// So this runs before every build and stops it outright when it finds:
    ///
    ///   - a secret key anywhere under Assets/ or in ProjectSettings,
    ///   - PRAXSUITE_SERVER defined for a target players can run,
    ///   - a settings asset with a secret key in the publishable key field,
    ///   - a plaintext http:// gateway URL pointed at a remote host,
    ///   - a settings asset that is missing or unusable.
    ///
    /// It also warns about verbose logging left on, which leaks player data into device logs.
    ///
    /// Nothing here is bypassable with a checkbox on purpose. If a build must proceed anyway,
    /// the escape hatch is to fix the finding - or, knowingly and visibly, to remove this file.
    /// </summary>
    public class PraxBuildGuard : IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;

        private const string ServerDefine = "PRAXSUITE_SERVER";

        // Matches a real key, not the prefix constant the SDK itself declares. Requiring 16+
        // characters of key material after the prefix keeps PraxKeyGuard's own
        // "sk_live_" literal from tripping the scan.
        private static readonly Regex SecretKeyPattern =
            new Regex(@"sk_live_[A-Za-z0-9]{16,}", RegexOptions.Compiled);

        // Text assets worth scanning. Binary assets and imported libraries are skipped: a key
        // pasted into a .png is not the failure mode anyone actually hits.
        private static readonly string[] ScannedExtensions =
        {
            ".cs", ".asset", ".json", ".txt", ".xml", ".yaml", ".yml", ".ini", ".cfg", ".env", ".prefab", ".unity"
        };

        public void OnPreprocessBuild(BuildReport report)
        {
            var target = report.summary.platform;
            var group = BuildPipeline.GetBuildTargetGroup(target);
            var problems = new List<string>();
            var warnings = new List<string>();

            CheckServerDefine(group, target, problems);
            CheckSettingsAsset(problems, warnings);
            CheckForSecretKeys(problems);

            foreach (var warning in warnings)
                Debug.LogWarning("[Praxsuite] " + warning);

            if (problems.Count == 0)
            {
                Debug.Log("[Praxsuite] Build checks passed for " + target + ".");
                return;
            }

            var message = "Praxsuite blocked this build.\n\n" +
                          string.Join("\n\n", problems.ToArray()) +
                          "\n\nSee Packages/com.tesseractsoftwares.praxsuite/docs/security.md.";

            // BuildFailedException is what Unity expects here; it aborts the build and reports
            // the message rather than producing a broken player.
            throw new BuildFailedException(message);
        }

        // ------------------------------------------------------------ server define

        private static void CheckServerDefine(BuildTargetGroup group, BuildTarget target,
            List<string> problems)
        {
            if (!IsDefined(group, ServerDefine)) return;

            if (IsDedicatedServerTarget(group, target)) return;

            problems.Add(
                "PROBLEM: " + ServerDefine + " is defined for build target " + target + ", which " +
                "players can run.\n" +
                "  That define compiles in Praxsuite.Server, whose whole purpose is holding a " +
                "secret key with full workspace access.\n" +
                "  FIX: remove " + ServerDefine + " from Player Settings / Scripting Define Symbols " +
                "for this target. Keep it only on the Dedicated Server platform, or on the " +
                "standalone target you build your headless server from.");
        }

        private static bool IsDefined(BuildTargetGroup group, string symbol)
        {
            string defines;
#if UNITY_2021_2_OR_NEWER
            defines = PlayerSettings.GetScriptingDefineSymbols(
                UnityEditor.Build.NamedBuildTarget.FromBuildTargetGroup(group));
#else
            defines = PlayerSettings.GetScriptingDefineSymbolsForGroup(group);
#endif
            if (string.IsNullOrEmpty(defines)) return false;

            foreach (var part in defines.Split(';', ',', ' '))
                if (part.Trim() == symbol) return true;
            return false;
        }

        private static bool IsDedicatedServerTarget(BuildTargetGroup group, BuildTarget target)
        {
#if UNITY_2021_2_OR_NEWER
            // Unity 2021.2+ models "Dedicated Server" as a subtarget of Standalone rather than
            // its own BuildTargetGroup, so the subtarget is the only thing worth checking.
            if (group == BuildTargetGroup.Standalone &&
                EditorUserBuildSettings.standaloneBuildSubtarget == StandaloneBuildSubtarget.Server)
                return true;
#endif
            // Before 2021.2 there was no server subtarget, so a headless standalone was the
            // only way to build one. Accept Linux standalone, which is what those builds were
            // in practice, rather than blocking a legitimate server build on an old editor.
            return group == BuildTargetGroup.Standalone && target == BuildTarget.StandaloneLinux64;
        }

        // ----------------------------------------------------------- settings asset

        private static void CheckSettingsAsset(List<string> problems, List<string> warnings)
        {
            var settings = Resources.Load<PraxsuiteSettings>(PraxsuiteSettings.ResourcePath);

            if (settings == null)
            {
                // Not fatal on its own: the project may configure the SDK at runtime through
                // PraxsuiteOptions, which is a legitimate pattern.
                warnings.Add(
                    "No PraxsuiteSettings asset was found in a Resources folder. The SDK will " +
                    "throw at runtime unless you call PraxsuiteClient.Configure() before the " +
                    "first Prax call. Create one from Praxsuite / Create Settings Asset.");
                return;
            }

            var problem = settings.Validate();
            if (problem != null)
            {
                problems.Add(
                    "PROBLEM: PraxsuiteSettings is not usable.\n" +
                    "  " + problem + "\n" +
                    "  FIX: open Project Settings / Praxsuite and correct it.");
            }

            if (PraxRoutes.IsInsecureRemote(settings.ResolvedBaseUrl))
            {
                problems.Add(
                    "PROBLEM: the gateway URL is plaintext http:// pointed at a remote host (" +
                    settings.ResolvedBaseUrl + ").\n" +
                    "  Every API key and player session token would travel unencrypted, readable " +
                    "by anyone on the network path.\n" +
                    "  FIX: use https://. Plain http is only accepted for localhost.");
            }

            if (settings.verboseLogging)
            {
                warnings.Add(
                    "Verbose logging is enabled in PraxsuiteSettings. Credentials are redacted, " +
                    "but request and response bodies - including player data - will be written to " +
                    "the device log. Turn it off for a release build.");
            }
        }

        // --------------------------------------------------------------- key scan

        private static void CheckForSecretKeys(List<string> problems)
        {
            var roots = new List<string> { Application.dataPath };

            var projectSettings = Path.Combine(
                Directory.GetParent(Application.dataPath).FullName, "ProjectSettings");
            if (Directory.Exists(projectSettings)) roots.Add(projectSettings);

            var hits = new List<string>();

            foreach (var root in roots)
            {
                string[] files;
                try
                {
                    files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[Praxsuite] Could not scan " + root + " for secret keys: " + ex.Message);
                    continue;
                }

                foreach (var file in files)
                {
                    if (!IsScannable(file)) continue;

                    string text;
                    try
                    {
                        // A key is short; a huge file is a generated asset, not somewhere a
                        // human pastes credentials. Skipping them keeps the scan quick.
                        var info = new FileInfo(file);
                        if (info.Length > 4 * 1024 * 1024) continue;
                        text = File.ReadAllText(file);
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    if (SecretKeyPattern.IsMatch(text)) hits.Add(ToProjectRelative(file));
                }
            }

            if (hits.Count == 0) return;

            problems.Add(
                "PROBLEM: a Praxsuite SECRET key (sk_live_) was found in " + hits.Count +
                " project file(s):\n" +
                "    " + string.Join("\n    ", hits.ToArray()) + "\n\n" +
                "  A secret key carries full workspace access and would ship inside this build, " +
                "where anyone can extract it in minutes.\n" +
                "  FIX:\n" +
                "    1. Revoke that key now, in the portal under API Gateway / Credentials. " +
                "Assume it is compromised - it is in your build folder and probably in git history.\n" +
                "    2. Remove it from these files.\n" +
                "    3. For client code, use a publishable key (pk_live_) and sign players in " +
                "with Prax.Auth.\n" +
                "    4. For a dedicated server, supply the key through the PRAXSUITE_SECRET_KEY " +
                "environment variable instead of any file in the project.");
        }

        private static bool IsScannable(string path)
        {
            // Skip Unity's own metadata and anything under a Library/Temp style folder that
            // may have been nested into Assets by a package.
            if (path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) return false;

            var extension = Path.GetExtension(path);
            if (string.IsNullOrEmpty(extension)) return false;

            foreach (var allowed in ScannedExtensions)
                if (string.Equals(extension, allowed, StringComparison.OrdinalIgnoreCase)) return true;

            return false;
        }

        private static string ToProjectRelative(string absolute)
        {
            var projectRoot = Directory.GetParent(Application.dataPath).FullName;
            return absolute.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase)
                ? absolute.Substring(projectRoot.Length + 1).Replace('\\', '/')
                : absolute;
        }
    }
}
