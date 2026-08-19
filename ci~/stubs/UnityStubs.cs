// Minimal UnityEngine surface, only enough to type-check the Praxsuite runtime assembly
// outside the editor. Not a Unity reimplementation - every member here exists purely so the
// compiler can resolve what the SDK actually calls.
using System;
using System.Collections;
using System.Collections.Generic;

namespace UnityEngine
{
    public class Object
    {
        public HideFlags hideFlags;
        public string name;
        public static void DontDestroyOnLoad(Object o) { }
        public static void Destroy(Object o) { }
    }

    [Flags]
    public enum HideFlags { None = 0, HideAndDontSave = 61 }

    public class ScriptableObject : Object
    {
        public static T CreateInstance<T>() where T : ScriptableObject => (T)System.Activator.CreateInstance(typeof(T));
    }

    public class Component : Object
    {
        public GameObject gameObject => null;
    }

    public class Behaviour : Component { }

    public class MonoBehaviour : Behaviour
    {
        public Coroutine StartCoroutine(IEnumerator routine) => null;
    }

    public class Coroutine { }

    public class GameObject : Object
    {
        public GameObject() { }
        public GameObject(string n) { name = n; }
        public T AddComponent<T>() where T : Component => default;
    }

    public class Texture : Object { }

    public class Texture2D : Texture
    {
        public Texture2D(int w, int h) { }
        public bool isReadable => true;
        public byte[] EncodeToPNG() => Array.Empty<byte>();
        public bool LoadImage(byte[] data) => true;
    }

    public static class Debug
    {
        public static void Log(object m) { }
        public static void LogWarning(object m) { }
        public static void LogError(object m) { }
    }

    public static class Application
    {
        public static string persistentDataPath => "";
        public static string dataPath => "";
        public static string identifier => "";
        public static string version => "";
        public static string unityVersion => "";
        public static bool isEditor => false;
        public static bool isBatchMode => false;
        public static RuntimePlatform platform => RuntimePlatform.WindowsPlayer;
        public static void OpenURL(string url) { }
    }

    public enum RuntimePlatform { WindowsPlayer, LinuxPlayer, OSXPlayer, Android, IPhonePlayer }

    public static class SystemInfo
    {
        public static string deviceUniqueIdentifier => "";
    }

    public static class Resources
    {
        public static T Load<T>(string path) where T : Object => default;
    }

    public class AddComponentMenu : Attribute
    {
        public AddComponentMenu(string menu) { }
    }

    public class HeaderAttribute : PropertyAttribute
    {
        public HeaderAttribute(string header) { }
    }

    public class TooltipAttribute : PropertyAttribute
    {
        public TooltipAttribute(string tooltip) { }
    }

    public class RangeAttribute : PropertyAttribute
    {
        public RangeAttribute(float min, float max) { }
    }

    public class PropertyAttribute : Attribute { }

    public class CreateAssetMenuAttribute : Attribute
    {
        public string fileName;
        public string menuName;
        public int order;
    }

    public enum RuntimeInitializeLoadType { BeforeSceneLoad, AfterSceneLoad }

    public class RuntimeInitializeOnLoadMethodAttribute : Attribute
    {
        public RuntimeInitializeOnLoadMethodAttribute() { }
        public RuntimeInitializeOnLoadMethodAttribute(RuntimeInitializeLoadType type) { }
    }

    public static class JsonUtility
    {
        public static string ToJson(object obj) => "";
        public static T FromJson<T>(string json) => default;
    }
}

namespace UnityEngine.Networking
{
    public class DownloadHandler : IDisposable
    {
        public byte[] data => Array.Empty<byte>();
        public string text => "";
        public void Dispose() { }
    }

    public class DownloadHandlerBuffer : DownloadHandler { }

    public class UploadHandler : IDisposable { public void Dispose() { } }

    public class UploadHandlerRaw : UploadHandler
    {
        public UploadHandlerRaw(byte[] data) { }
    }

    public interface IMultipartFormSection { }

    public class MultipartFormFileSection : IMultipartFormSection
    {
        public MultipartFormFileSection(string name, byte[] data, string fileName, string contentType) { }
    }

    public class UnityWebRequestAsyncOperation
    {
        public bool isDone => true;
    }

    public class UnityWebRequest : IDisposable
    {
        public enum Result { Success, ConnectionError, ProtocolError, DataProcessingError }

        public UnityWebRequest(string url, string method) { }

        public DownloadHandler downloadHandler { get; set; }
        public UploadHandler uploadHandler { get; set; }
        public int timeout { get; set; }
        public long responseCode => 200;
        public string error => "";
        public string url => "";
        public Result result => Result.Success;
        public bool isNetworkError => false;

        public void SetRequestHeader(string name, string value) { }
        public Dictionary<string, string> GetResponseHeaders() => null;
        public UnityWebRequestAsyncOperation SendWebRequest() => new UnityWebRequestAsyncOperation();
        public void Abort() { }
        public void Dispose() { }

        public static UnityWebRequest Post(string url, List<IMultipartFormSection> sections) => null;
    }
}
