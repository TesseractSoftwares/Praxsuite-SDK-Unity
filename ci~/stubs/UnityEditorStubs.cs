// Minimal UnityEditor surface, only enough to type-check the Praxsuite editor assembly.
using System;
using UnityEngine;

namespace UnityEditor
{
    public enum BuildTarget
    {
        StandaloneWindows64, StandaloneWindows32, StandaloneLinux64, StandaloneOSX,
        Android, iOS, WebGL
    }

    public enum BuildTargetGroup { Unknown, Standalone, Android, iOS, WebGL }

    public enum StandaloneBuildSubtarget { Player, Server }

    public static class BuildPipeline
    {
        public static BuildTargetGroup GetBuildTargetGroup(BuildTarget t) => BuildTargetGroup.Standalone;
    }

    public static class EditorUserBuildSettings
    {
        public static StandaloneBuildSubtarget standaloneBuildSubtarget => StandaloneBuildSubtarget.Player;
    }

    public static class PlayerSettings
    {
        public static string GetScriptingDefineSymbols(UnityEditor.Build.NamedBuildTarget t) => "";
        public static string GetScriptingDefineSymbolsForGroup(BuildTargetGroup g) => "";
    }

    public static class AssetDatabase
    {
        public static T LoadAssetAtPath<T>(string path) where T : UnityEngine.Object => default;
        public static void CreateAsset(UnityEngine.Object o, string path) { }
        public static void SaveAssets() { }
        public static void Refresh() { }
        public static bool OpenAsset(UnityEngine.Object o) => true;
    }

    public class TextAsset : UnityEngine.Object { }

    public static class Selection
    {
        public static UnityEngine.Object activeObject { get; set; }
    }

    public static class EditorGUIUtility
    {
        public static void PingObject(UnityEngine.Object o) { }
    }

    public class SerializedProperty
    {
        public string name => "";
        public bool NextVisible(bool enterChildren) => false;
    }

    public class SerializedObject
    {
        public SerializedObject(UnityEngine.Object target) { }
        public void Update() { }
        public void ApplyModifiedProperties() { }
        public SerializedProperty GetIterator() => new SerializedProperty();
    }

    public static class EditorStyles
    {
        public static GUIStyle boldLabel => null;
    }

    public enum MessageType { None, Info, Warning, Error }

    public static class EditorGUILayout
    {
        public static void Space(float pixels) { }
        public static void HelpBox(string message, MessageType type) { }
        public static void LabelField(string label, GUIStyle style) { }
        public static void PropertyField(SerializedProperty p, bool includeChildren) { }

        public class HorizontalScope : IDisposable
        {
            public void Dispose() { }
        }
    }

    public class MenuItem : Attribute
    {
        public MenuItem(string path) { }
        public MenuItem(string path, bool isValidateFunction) { }
        public MenuItem(string path, bool isValidateFunction, int priority) { }
        public int priority;
    }

    public class SettingsProviderAttribute : Attribute { }

    public enum SettingsScope { User, Project }

    public class SettingsProvider
    {
        public SettingsProvider(string path, SettingsScope scope) { }
        public string label { get; set; }
        public string[] keywords { get; set; }
        public Action<string> guiHandler { get; set; }
    }

    public static class SettingsService
    {
        public static void OpenProjectSettings(string path) { }
    }
}

namespace UnityEditor.Build
{
    public struct NamedBuildTarget
    {
        public string TargetName => "Standalone";
        public static NamedBuildTarget FromBuildTargetGroup(BuildTargetGroup g) => new NamedBuildTarget();
        public static NamedBuildTarget Standalone => new NamedBuildTarget();
    }

    public class BuildFailedException : Exception
    {
        public BuildFailedException(string message) : base(message) { }
    }

    public interface IOrderedCallback
    {
        int callbackOrder { get; }
    }

    public interface IPreprocessBuildWithReport : IOrderedCallback
    {
        void OnPreprocessBuild(UnityEditor.Build.Reporting.BuildReport report);
    }
}

namespace UnityEditor.Build.Reporting
{
    public class BuildSummary
    {
        public BuildTarget platform => BuildTarget.StandaloneWindows64;
    }

    public class BuildReport
    {
        public BuildSummary summary => new BuildSummary();
    }
}

namespace UnityEngine
{
    public class GUIStyle { }

    public static class GUILayout
    {
        public static bool Button(string text) => false;
        public static bool Button(string text, params GUILayoutOption[] options) => false;
        public static GUILayoutOption Height(float h) => null;
    }

    public class GUILayoutOption { }
}
