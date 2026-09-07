using System;
using System.IO;
using System.Text.Json;
using STS2Mobile.Android;
using STS2Mobile.Patches;

internal static class Program
{
    private static void Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "mod-alias-safety-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string mods = Path.Combine(root, "mods");
            AppPaths.ModsDir = mods;
            Directory.CreateDirectory(mods);
            string manifest = Path.Combine(mods, "mod_manifest.json");
            foreach (string invalid in new[] { "../escaped", "..\\escaped", Path.Combine(root, "absolute"), "C:escaped", ".", ".." })
            {
                WriteManifest(manifest, invalid);
                ModLoaderPatches.GetAndroidModsDir();
                Require(File.Exists(manifest), "Unsafe manifest must not be removed");
                Require(Directory.GetFiles(mods).Length == 1, "Unsafe id created a local alias");
                Require(!File.Exists(Path.Combine(root, "escaped.json")) && !File.Exists(Path.Combine(root, "absolute.json")), "Alias escaped MOD root");
            }
            string id = "模组.Test-1";
            WriteManifest(manifest, id);
            ModLoaderPatches.GetAndroidModsDir();
            string alias = Path.Combine(mods, id + ".json");
            using (var json = JsonDocument.Parse(File.ReadAllText(alias)))
            {
                Require(json.RootElement.GetProperty("id").GetString() == id, "Valid id was changed");
                Require(json.RootElement.GetProperty("android_generated_manifest_alias").GetBoolean(), "Missing generated alias marker");
            }
            Require(!File.Exists(manifest), "Generated duplicate manifest was not removed");
            File.Delete(alias);

            string outside = Path.Combine(root, "outside.json");
            File.WriteAllText(outside, "private-data");
            WriteManifest(manifest, "linked");
            string link = Path.Combine(mods, "linked.json");
            File.CreateSymbolicLink(link, outside);
            ModLoaderPatches.GetAndroidModsDir();
            Require(File.ReadAllText(outside) == "private-data" && File.Exists(manifest), "Symlink alias changed external data or removed source");
            File.Delete(link);
            File.Delete(manifest);

            string externalDir = Path.Combine(root, "external");
            Directory.CreateDirectory(externalDir);
            WriteManifest(Path.Combine(externalDir, "mod_manifest.json"), "External");
            string dirLink = Path.Combine(mods, "external-link");
            Directory.CreateSymbolicLink(dirLink, externalDir);
            ModLoaderPatches.GetAndroidModsDir();
            Require(!File.Exists(Path.Combine(externalDir, "External.json")), "Scanner followed a symlink directory");
            Directory.Delete(dirLink);
            Console.WriteLine("PASS: traversal, rooted paths, Unicode alias, symlink file and symlink directory boundaries");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void WriteManifest(string path, string id) => File.WriteAllText(path, JsonSerializer.Serialize(new { id }));
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
