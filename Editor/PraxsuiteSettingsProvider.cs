using System.IO;
using UnityEditor;
using UnityEngine;

namespace Praxsuite.Editor
{
    /// <summary>
    /// The Praxsuite page in Project Settings, plus the menu item that creates the settings
    /// asset.
    ///
    /// The goal is that a developer who has never seen this SDK can go from installed to
    /// working by filling in one field, with the page telling them what is wrong when it is.
    /// </summary>
    public static class PraxsuiteSettingsProvider
    {
        private const string AssetFolder = "Assets/Resources";
        private const string AssetPath = AssetFolder + "/PraxsuiteSettings.asset";

        [MenuItem("Praxsuite/Create Settings Asset", priority = 1)]
        public static PraxsuiteSettings CreateSettingsAsset()
        {
            var existing = AssetDatabase.LoadAssetAtPath<PraxsuiteSettings>(AssetPath);
            if (existing != null)
            {
                Selection.activeObject = existing;
                EditorGUIUtility.PingObject(existing);
                return existing;
            }

            if (!Directory.Exists(AssetFolder)) Directory.CreateDirectory(AssetFolder);

            var settings = ScriptableObject.CreateInstance<PraxsuiteSettings>();
            AssetDatabase.CreateAsset(settings, AssetPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Selection.activeObject = settings;
            EditorGUIUtility.PingObject(settings);

            Debug.Log("[Praxsuite] Created " + AssetPath + ". Paste your Workspace ID into it to finish setup.");
            return settings;
        }

        [MenuItem("Praxsuite/Settings", priority = 2)]
        public static void OpenSettings()
        {
            SettingsService.OpenProjectSettings("Project/Praxsuite");
        }

        [MenuItem("Praxsuite/Documentation", priority = 20)]
        public static void OpenDocs()
        {
            Application.OpenURL("https://praxsuite.com/docs/sdk/unity");
        }

        [SettingsProvider]
        public static SettingsProvider CreateProvider()
        {
            return new SettingsProvider("Project/Praxsuite", SettingsScope.Project)
            {
                label = "Praxsuite",
                keywords = new[] { "praxsuite", "backend", "workspace", "api", "gateway" },
                guiHandler = _ => DrawSettings()
            };
        }

        private static void DrawSettings()
        {
            var settings = AssetDatabase.LoadAssetAtPath<PraxsuiteSettings>(AssetPath)
                           ?? Resources.Load<PraxsuiteSettings>(PraxsuiteSettings.ResourcePath);

            EditorGUILayout.Space(8);

            if (settings == null)
            {
                EditorGUILayout.HelpBox(
                    "Praxsuite is not set up yet.\n\n" +
                    "Create the settings asset, then paste your Workspace ID. That is the whole " +
                    "setup - the publishable key is fetched automatically.",
                    MessageType.Info);

                EditorGUILayout.Space(4);
                if (GUILayout.Button("Create Settings Asset", GUILayout.Height(28)))
                    CreateSettingsAsset();
                return;
            }

            var serialized = new SerializedObject(settings);
            serialized.Update();

            // Status first: the answer to "is this working?" should not require scrolling.
            var problem = settings.Validate();
            if (problem != null)
                EditorGUILayout.HelpBox(problem, MessageType.Error);
            else
                EditorGUILayout.HelpBox(
                    "Ready. Gateway: " + settings.ResolvedBaseUrl + "\n" +
                    (string.IsNullOrWhiteSpace(settings.publishableKey)
                        ? "Publishable key: fetched automatically at startup."
                        : "Publishable key: " + PraxKeyGuard.Redact(settings.publishableKey)),
                    MessageType.Info);

            EditorGUILayout.Space(8);

            var property = serialized.GetIterator();
            var enterChildren = true;
            while (property.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (property.name == "m_Script") continue;
                EditorGUILayout.PropertyField(property, true);
            }

            serialized.ApplyModifiedProperties();

            EditorGUILayout.Space(12);
            DrawSecurityNotice(settings);

            EditorGUILayout.Space(8);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Select Asset")) Selection.activeObject = settings;
                if (GUILayout.Button("Open Portal"))
                    Application.OpenURL("https://praxsuite.com/workspace/" + settings.workspaceId);
                if (GUILayout.Button("Security Guide")) OpenSecurityDoc();
            }
        }

        private static void DrawSecurityNotice(PraxsuiteSettings settings)
        {
            EditorGUILayout.LabelField("Security", EditorStyles.boldLabel);

            EditorGUILayout.HelpBox(
                "A game client is untrusted code running on someone else's machine. Build for that:\n\n" +
                "1. Ship only a publishable key (pk_live_). Never a secret key - the build guard " +
                "fails any build containing one.\n\n" +
                "2. Give each player their own identity with Prax.Auth. A __SELF__ row filter on " +
                "the player role then scopes every read and write to that player, server-side, " +
                "where a modified client cannot reach it.\n\n" +
                "3. Scope the player role read-only wherever you can, and route currency, " +
                "inventory grants and score submission through Prax.Endpoints so an automation " +
                "you control decides the outcome.",
                MessageType.None);

            if (settings.verboseLogging)
            {
                EditorGUILayout.HelpBox(
                    "Verbose logging is on. Credentials are redacted, but request and response " +
                    "bodies - including player data - go to the device log. Turn it off before " +
                    "shipping.",
                    MessageType.Warning);
            }
        }

        private static void OpenSecurityDoc()
        {
            const string packaged = "Packages/com.tesseractsoftwares.praxsuite/docs/security.md";
            var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(packaged);

            if (asset != null) AssetDatabase.OpenAsset(asset);
            else Application.OpenURL("https://praxsuite.com/docs/sdk/unity/security");
        }
    }
}
