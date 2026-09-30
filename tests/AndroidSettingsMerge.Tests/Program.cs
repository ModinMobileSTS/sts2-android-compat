using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using STS2Mobile.Android;
using STS2Mobile.Patches;

internal static class Program
{
    private static void Main()
    {
        string directory = Path.Combine(Path.GetTempPath(), "sts2-settings-roundtrip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            AndroidSettingsBridge.SettingsPath = Path.Combine(directory, "settings.save");
            File.WriteAllText(AndroidSettingsBridge.SettingsPath,
                "{\"volume_master\":0.75,\"android_compat_pack_enabled\":false}");
            string beforeSave = File.ReadAllText(AndroidSettingsBridge.SettingsPath);

            // A typed game settings save discards fields it does not own.
            GameSettings gameSettings = JsonSerializer.Deserialize<GameSettings>(beforeSave)!;
            gameSettings.VolumeMaster = 0.25;
            File.WriteAllText(AndroidSettingsBridge.SettingsPath, JsonSerializer.Serialize(gameSettings));
            AndroidSettingsMerge.MergeBackAndroidOnlyFields(beforeSave);

            JsonObject saved = JsonNode.Parse(File.ReadAllText(AndroidSettingsBridge.SettingsPath))!.AsObject();
            if (saved["android_compat_pack_enabled"]?.GetValue<bool>() != false)
                throw new InvalidOperationException("Saving in-game settings lost the launcher's disabled compatibility choice.");
            if (saved["volume_master"]?.GetValue<double>() != 0.25)
                throw new InvalidOperationException("Merging Android preferences rolled back the game's new volume setting.");
            Console.WriteLine("PASS settings serialization round-trip: " + saved.ToJsonString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class GameSettings
    {
        [JsonPropertyName("volume_master")]
        public double VolumeMaster { get; set; }
    }
}

namespace STS2Mobile.Android
{
    internal static class AndroidSettingsBridge
    {
        public static string SettingsPath = "";
        public static bool TryReadRaw(out string json)
        {
            json = File.ReadAllText(SettingsPath);
            return true;
        }
        public static bool TryWriteRaw(string json)
        {
            File.WriteAllText(SettingsPath, json);
            return true;
        }
    }
}

namespace STS2Mobile
{
    internal static class PatchHelper
    {
        public static void Log(string message) => Console.WriteLine(message);
    }
}
