using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.Modding;
using STS2Mobile.Android;

namespace STS2Mobile.Patches;

// Mirrors the reference launcher startup path while avoiding Harmony transpiler
// emission on ModManager.Initialize. The old ldstr/Path.Combine transpiler worked
// on older builds but can trip MonoMod/Cecil/Godot StringName lifetime bugs on
// Godot 4.5 Android. A prefix replacement calls the game's own private scanner,
// dependency sorter and loader through reflection instead.
public static class ModLoaderPatches
{
    public static void Apply(Harmony harmony)
    {
        PatchHelper.Patch(harmony, typeof(ModManager), "Initialize", prefix: PatchHelper.Method(typeof(ModLoaderPatches), nameof(InitializePrefix)));
        PatchHelper.Patch(harmony, typeof(ModManager), "ReadSteamMods", prefix: PatchHelper.Method(typeof(ModLoaderPatches), nameof(ReadSteamModsPrefix)));
        PatchHelper.Log("Mod loader compatibility patches enabled: Android local-mod scan + no Steam Workshop scan.");
    }

    public static bool InitializePrefix(IModManagerFileIo fileIo, ModSettings settings)
    {
        try
        {
            RunAndroidModInitialization(fileIo, settings);
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"[Mods] Android ModManager.Initialize replacement failed: {exception}");
        }
        return false;
    }

    private static void RunAndroidModInitialization(IModManagerFileIo fileIo, ModSettings settings)
    {
        SetField("_settings", settings);
        SetField("_fileIo", fileIo);
        AppDomain.CurrentDomain.AssemblyResolve += InvokeAssemblyResolve;

        string path = GetAndroidModsDir();
        if (fileIo.DirectoryExists(path))
        {
            InvokeStatic("ReadModsInDirRecursive", path, ModSource.ModsDirectory, null);
        }

        var mods = GetMods();
        if (mods.Count == 0)
        {
            PatchHelper.Log("[Mods] No Android mods detected.");
            return;
        }

        InvokeStatic("SortModList", settings?.ModList ?? new List<SettingsSaveMod>());
        foreach (var mod in GetMods().ToArray())
        {
            InvokeStatic("TryLoadMod", mod);
        }

        if (ModManager.IsRunningModded())
        {
            PatchHelper.Log($"[Mods] Android mod initialization loaded {ModManager.GetLoadedMods().Count()} mods ({GetMods().Count} total).");
        }
        SetField("_initialized", true);

        if (settings != null)
        {
            var list = new List<SettingsSaveMod>();
            foreach (var mod in GetMods())
            {
                var settingsSaveMod = new SettingsSaveMod(mod);
                bool isEnabled = settings.ModList.FirstOrDefault(m => m.Id == mod.manifest?.id)?.IsEnabled ?? true;
                settingsSaveMod.IsEnabled = isEnabled;
                list.Add(settingsSaveMod);
            }
            settings.ModList = list;
        }
    }

    private static List<Mod> GetMods()
    {
        return (List<Mod>)GetField("_mods");
    }

    private static object GetField(string name)
    {
        return typeof(ModManager).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
    }

    private static void SetField(string name, object value)
    {
        var field = typeof(ModManager).GetField(name, BindingFlags.NonPublic | BindingFlags.Static);
        field?.SetValue(null, value);
    }

    private static object InvokeStatic(string name, params object[] args)
    {
        var method = typeof(ModManager).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
        if (method == null)
            throw new MissingMethodException(typeof(ModManager).FullName, name);
        return method.Invoke(null, args);
    }

    private static Assembly InvokeAssemblyResolve(object sender, ResolveEventArgs args)
    {
        try
        {
            return InvokeStatic("HandleAssemblyResolveFailure", sender, args) as Assembly;
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"[Mods] Assembly resolve fallback failed for {args?.Name}: {exception.Message}");
            return null;
        }
    }

    public static string GetAndroidModsDir()
    {
        var path = AppPaths.ModsDir;
        try
        {
            Directory.CreateDirectory(path);
            NormalizeManifestAliases(path);
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"[Mods] Failed to prepare Android mods directory '{path}': {exception.Message}");
        }
        return path;
    }

    private static void NormalizeManifestAliases(string modsRoot)
    {
        if (!Directory.Exists(modsRoot))
            return;
        foreach (var manifestPath in Directory.EnumerateFiles(modsRoot, "mod_manifest.json", SearchOption.AllDirectories))
        {
            TryCopyManifestAlias(manifestPath);
        }
    }

    private static void TryCopyManifestAlias(string manifestPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(manifestPath);
            if (string.IsNullOrEmpty(dir))
                return;
            var modId = ReadManifestId(manifestPath);
            if (string.IsNullOrWhiteSpace(modId))
                modId = Path.GetFileName(dir);
            if (string.IsNullOrWhiteSpace(modId))
                return;
            var alias = Path.Combine(dir, modId + ".json");
            if (Path.GetFullPath(alias).Equals(Path.GetFullPath(manifestPath), StringComparison.OrdinalIgnoreCase))
                return;
            if (!File.Exists(alias))
            {
                WriteManifestAlias(manifestPath, alias);
                PatchHelper.Log($"[Mods] Added runtime manifest alias {Path.GetFileName(alias)} for reference scanner.");
            }
            if (File.Exists(alias) && string.Equals(ReadManifestId(alias), modId, StringComparison.Ordinal) && IsGeneratedManifestAlias(alias))
            {
                File.Delete(manifestPath);
                PatchHelper.Log($"[Mods] Removed duplicate mod_manifest.json after aliasing {Path.GetFileName(alias)}.");
            }
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"[Mods] Manifest alias normalization failed for {manifestPath}: {exception.Message}");
        }
    }

    private static void WriteManifestAlias(string sourcePath, string aliasPath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(sourcePath));
            using var stream = File.Create(aliasPath);
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject())
                property.WriteTo(writer);
            writer.WriteBoolean("android_generated_manifest_alias", true);
            writer.WriteEndObject();
        }
        catch
        {
            File.Copy(sourcePath, aliasPath, overwrite: true);
        }
    }

    private static bool IsGeneratedManifestAlias(string manifestPath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            return document.RootElement.TryGetProperty("android_generated_manifest_alias", out var generated)
                && generated.ValueKind == JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }

    private static string ReadManifestId(string manifestPath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var root = document.RootElement;
            if (root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                return id.GetString();
            if (root.TryGetProperty("mod_id", out var modId) && modId.ValueKind == JsonValueKind.String)
                return modId.GetString();
            if (root.TryGetProperty("modId", out var camelModId) && camelModId.ValueKind == JsonValueKind.String)
                return camelModId.GetString();
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"[Mods] Failed to read manifest id from {manifestPath}: {exception.Message}");
        }
        return null;
    }

    public static bool ReadSteamModsPrefix() => false;
}
