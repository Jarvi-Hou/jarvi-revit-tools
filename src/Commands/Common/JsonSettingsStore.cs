using System;
using System.IO;
using Newtonsoft.Json;

namespace JarviTools.Commands.Common
{
    /// <summary>
    /// 在 %AppData%\JarviTools\ 下按名字读写 JSON 设置。
    /// 读失败/文件不存在时返回全新默认对象；写失败静默（设置丢失不影响功能）。
    /// </summary>
    internal static class JsonSettingsStore
    {
        private static string Dir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JarviTools");

        public static T Load<T>(string name) where T : class, new()
        {
            try
            {
                string path = Path.Combine(Dir, name + ".json");
                if (!File.Exists(path)) return new T();
                return JsonConvert.DeserializeObject<T>(File.ReadAllText(path)) ?? new T();
            }
            catch { return new T(); }
        }

        public static void Save<T>(string name, T settings) where T : class
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(Path.Combine(Dir, name + ".json"),
                    JsonConvert.SerializeObject(settings, Formatting.Indented));
            }
            catch { /* 忽略 */ }
        }
    }
}
