using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
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
        PatchInitialize(harmony);
        PatchReadSteamMods(harmony);
        PatchHelper.Log("Mod loader compatibility patches enabled: Android local-mod scan + no Steam Workshop scan.");
    }

    private static void PatchInitialize(Harmony harmony)
    {
        try
        {
            var target = typeof(ModManager).GetMethod("Initialize", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (target == null)
            {
                PatchHelper.Log($"SKIPPED {typeof(ModManager).FullName}.Initialize: method not found");
                return;
            }

            var prefix = typeof(Task).IsAssignableFrom(target.ReturnType)
                ? PatchHelper.Method(typeof(ModLoaderPatches), nameof(InitializeAsyncPrefix))
                : PatchHelper.Method(typeof(ModLoaderPatches), nameof(InitializePrefix));
            harmony.Patch(target, prefix: new HarmonyMethod(prefix));
            PatchHelper.Log($"Patched {typeof(ModManager).FullName}.Initialize (return={target.ReturnType.Name})");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"FAILED {typeof(ModManager).FullName}.Initialize: {exception}");
        }
    }

    private static void PatchReadSteamMods(Harmony harmony)
    {
        try
        {
            var target = typeof(ModManager).GetMethod("ReadSteamMods", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (target == null)
            {
                PatchHelper.Log($"SKIPPED {typeof(ModManager).FullName}.ReadSteamMods: method not found");
                return;
            }

            var prefix = typeof(Task).IsAssignableFrom(target.ReturnType)
                ? PatchHelper.Method(typeof(ModLoaderPatches), nameof(ReadSteamModsAsyncPrefix))
                : PatchHelper.Method(typeof(ModLoaderPatches), nameof(ReadSteamModsPrefix));
            harmony.Patch(target, prefix: new HarmonyMethod(prefix));
            PatchHelper.Log($"Patched {typeof(ModManager).FullName}.ReadSteamMods (return={target.ReturnType.Name})");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"FAILED {typeof(ModManager).FullName}.ReadSteamMods: {exception}");
        }
    }

    public static bool InitializePrefix(IModManagerFileIo fileIo, ModSettings settings, object[] __args = null)
    {
        RunAndroidModInitializationSafely(fileIo, settings, __args);
        return false;
    }

    public static bool InitializeAsyncPrefix(IModManagerFileIo fileIo, ModSettings settings, ref Task __result, object[] __args = null)
    {
        RunAndroidModInitializationSafely(fileIo, settings, __args);
        __result = Task.CompletedTask;
        return false;
    }

    private static void RunAndroidModInitializationSafely(IModManagerFileIo fileIo, ModSettings settings, object[] args)
    {
        try
        {
            var gameVersion = args != null && args.Length > 2 ? args[2] : null;
            RunAndroidModInitialization(fileIo, settings, gameVersion);
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"[Mods] Android ModManager.Initialize replacement failed: {exception}");
        }
    }

    private static void RunAndroidModInitialization(IModManagerFileIo fileIo, ModSettings settings, object gameVersion)
    {
        SetField("_settings", settings);
        SetField("_fileIo", fileIo);
        SetField("_gameVersion", gameVersion);
        AppDomain.CurrentDomain.AssemblyResolve += InvokeAssemblyResolve;

        // Register VANILLA model placeholders before any mod is loaded.
        //
        // On Android/Mono, Harmony-patching a member (e.g. HextechRunes patches the
        // UnlockState.Relics getter) eagerly runs the declaring type's static
        // constructor. UnlockState..cctor walks ModelDb.AllEncounters -> Act<Overgrowth>
        // and other vanilla models, which throws KeyNotFound if _contentById is
        // empty. On PC/CoreCLR the cctor is not triggered this eagerly, so the mod
        // loads cleanly. Mod initializers also build static maps that reference
        // vanilla models (e.g. wuwancients.HiddenSeaRecord..cctor references several
        // vanilla relics). Pre-registering vanilla placeholders here makes those
        // early accesses resolve.
        //
        // We must NOT compute ids for MOD model types yet: mods such as
        // YuWanCard/BaseLib postfix ModelDb.GetEntry to add a namespace prefix and
        // permanently cache the first GetEntry result per type. Calling GetId for a
        // mod type before that mod's PatchAll runs would poison the cache with the
        // un-prefixed id. Vanilla types never get a prefix, so computing their ids
        // now is safe. Mod model placeholders are registered later, in ModelDb.Init
        // (phase 1), where all mod GetEntry patches are already applied (PC parity).
        ModelDbInitPatch.EnsureVanillaModelPlaceholdersPreRegistered();

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

        InvokeStaticIfExists("RemoveDisabledMods");
        InvokeStatic("SortModList", settings?.ModList ?? new List<SettingsSaveMod>());
        foreach (var mod in GetMods().ToArray())
        {
            using (ModelDbInitPatch.BeginModInitializationContainsShield())
            {
                InvokeStatic("TryLoadMod", mod);
            }
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

    private static object InvokeStaticIfExists(string name, params object[] args)
    {
        var method = typeof(ModManager).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
        return method == null ? null : method.Invoke(null, args);
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

    public static bool ReadSteamModsAsyncPrefix(ref Task __result)
    {
        __result = Task.CompletedTask;
        return false;
    }
}
