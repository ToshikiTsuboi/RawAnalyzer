// Helpers/SettingsStore.cs
using System;
using System.IO;
using System.Text.Json;

namespace ImgProcessWpfApp.Helpers
{
    public class AppPreferences
    {
        public int RawWidth { get; set; } = 1920;
        public int RawHeight { get; set; } = 1080;
        public int HeaderBytes { get; set; } = 0;
    }

    public static class SettingsStore
    {
        private static readonly string Dir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "ImgProcessWpfApp");
        private static readonly string FilePath = Path.Combine(Dir, "prefs.json");

        public static AppPreferences Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var json = File.ReadAllText(FilePath);
                    var p = JsonSerializer.Deserialize<AppPreferences>(json);
                    if (p != null) return p;
                }
            }
            catch { }
            return new AppPreferences(); // 既定: 1920x1080 / 0
        }

        public static void Save(AppPreferences p)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var json = JsonSerializer.Serialize(p, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(FilePath, json);
            }
            catch { }
        }
    }
}
