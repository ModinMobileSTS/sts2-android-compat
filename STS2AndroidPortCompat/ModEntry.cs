using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Godot;
using Godot.Bridge;
using Godot.NativeInterop;
using HarmonyLib;
using STS2Mobile.Patches;

namespace STS2Mobile;

// Entry point for the Android compatibility patcher.  The patch order mirrors
// ../s2/.cache/StS2-Launcher_Mod_Manager as closely as this Java-shell based
// port allows: early diagnostics first, BaseLib listener before ModManager can
// load user DLLs, then the same startup/platform/release/settings/layout/input/
// mod-loader groups.  Project-specific Android settings/shader/quick-restart
// patches are layered after the reference groups.
public static class ModEntry
{
    private static bool _applied;
    private static Harmony _harmony;

    [UnmanagedCallersOnly]
    public static int InitializeGodotSharp(IntPtr godotDllHandle, IntPtr outManagedCallbacks, IntPtr unmanagedCallbacks, int unmanagedCallbacksSize)
    {
        try
        {
            Console.Error.WriteLine($"[STS2Mobile] DIAG InitializeGodotSharp begin godotDllHandle=0x{godotDllHandle.ToInt64():x} outManagedCallbacks=0x{outManagedCallbacks.ToInt64():x} unmanagedCallbacks=0x{unmanagedCallbacks.ToInt64():x} unmanagedCallbacksSize={unmanagedCallbacksSize} assembly={SafeAssemblyLocation(typeof(ModEntry).Assembly)} baseDir={AppContext.BaseDirectory} cwd={SafeCurrentDirectory()} framework={RuntimeInformation.FrameworkDescription} os={RuntimeInformation.OSDescription} arch={RuntimeInformation.ProcessArchitecture}");
            DllImportResolver resolver = new GodotDllImportResolver(godotDllHandle).OnResolveDllImport;
            NativeLibrary.SetDllImportResolver(typeof(GodotObject).Assembly, resolver);
            Console.Error.WriteLine($"[STS2Mobile] DIAG InitializeGodotSharp resolver installed godotSharpAssembly={SafeAssemblyLocation(typeof(GodotObject).Assembly)}");
            NativeFuncs.Initialize(unmanagedCallbacks, unmanagedCallbacksSize);
            Console.Error.WriteLine("[STS2Mobile] DIAG InitializeGodotSharp NativeFuncs initialized");
            ManagedCallbacks.Create(outManagedCallbacks);
            Console.Error.WriteLine("[STS2Mobile] GodotSharp bootstrapped successfully");
            return 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[STS2Mobile] GodotSharp bootstrap failed: {exception}");
            return 0;
        }
    }

    [UnmanagedCallersOnly]
    public static void Apply()
    {
        Console.Error.WriteLine($"[STS2Mobile] DIAG Apply entry applied={_applied} assembly={SafeAssemblyLocation(typeof(ModEntry).Assembly)} baseDir={AppContext.BaseDirectory} cwd={SafeCurrentDirectory()} temp={SafeTempPath()} framework={RuntimeInformation.FrameworkDescription} os={RuntimeInformation.OSDescription} arch={RuntimeInformation.ProcessArchitecture}");
        LogRuntimeSnapshot("apply_entry");
        if (_applied)
            return;
        _applied = true;

        EnsureAndroidTempDirectory();
        LogRuntimeSnapshot("after_temp_directory");

        PatchHelper.Log("Initializing STS2Mobile Android port compatibility.");
        CompatBuildInfo.Log();
        HarmonyAndroidCompat.Initialize();
        LogRuntimeSnapshot("after_harmony_android_compat_init");
        _harmony = new Harmony("com.sts2mobile");
        PatchHelper.Log($"DIAG Harmony instance created id=com.sts2mobile assembly={typeof(Harmony).Assembly.FullName} location={SafeAssemblyLocation(typeof(Harmony).Assembly)}");

        ApplyPatchGroup("Critical platform patches", () => PlatformPatches.Apply(_harmony));
        ApplyPatchGroup("Critical save path patches", () => SavePathPatches.Apply(_harmony));

        // Keep the Java in-game panel bridge independent from the large feature
        // patch group below.  A version-specific UI/input patch can legitimately
        // fail to apply, but that must not leave the panel's inspector waiting
        // forever for a host which was skipped only because it was last in that
        // group.
        ApplyPatchGroup("Developer tools bridge", () => STS2Mobile.DevTools.DevToolsHost.Start());

        ApplyPatchGroup("Mod framework and model patches", () =>
        {
            BaseLibCompatPatches.Apply(_harmony);
            RitsuLibCompatPatches.Apply(_harmony);
            EarlyLocalizationFallbackPatches.Apply(_harmony);
            DeferredModPatchQueue.Apply(_harmony);
            ModelDbInitPatch.Apply(_harmony);
            UnlockStateCompatPatches.Apply(_harmony);
        });

        ApplyPatchGroup("Release/settings/display patches", () =>
        {
            ReleaseInfoPatches.Apply(_harmony);
            SavePathPatches.Apply(_harmony);
            SettingsPatches.Apply(_harmony);
            AndroidSettingsPatches.Apply(_harmony);
            DisplaySettingsPatches.Apply(_harmony);
            AndroidFontCoveragePatches.Apply(_harmony);
            UiScalePatches.Apply(_harmony);
        });

        ApplyPatchGroup("Android resource lifetime safety", () =>
        {
            AndroidAssetCacheLifecyclePatches.Apply(_harmony);
        });

        ApplyPatchGroup("Mobile layout/input patches", () =>
        {
            MobileLayoutPatches.Apply(_harmony);
            EventLayoutPatches.Apply(_harmony);
            MerchantLayoutPatches.Apply(_harmony);
            TouchInputPatches.Apply(_harmony);
            CardRewardPatches.Apply(_harmony);
            EarlyAccessDisclaimerPatches.Apply(_harmony);
            FeedbackScreenPatches.Apply(_harmony);
            CombatBackgroundPatches.Apply(_harmony);
        });

        ApplyPatchGroup("Android runtime feature patches", () =>
        {
            AndroidUiSafetyPatches.Apply(_harmony);
            ExternalSettingsPatches.Apply(_harmony);
            AndroidInGameSettingsPatches.Apply(_harmony);
            ShaderCompatibilityPatches.Apply(_harmony);
            DebugMenuPatches.Apply(_harmony);
            TransitionMaterialPatches.Apply(_harmony);
            MapDrawingSceneCachePatches.Apply(_harmony);
            AndroidInputCompatPatches.Apply(_harmony);
            MobileSelectionConfirmationPatches.Apply(_harmony);
            MerchantSelectionConfirmationPatches.Apply(_harmony);
            MobileTooltipPatches.Apply(_harmony);
            MobileTapPreviewPatches.Apply(_harmony);
            MobileHandLayoutPatches.Apply(_harmony);
            IntentAnimationPatches.Apply(_harmony);
            CombatAnimationWarmupPatches.Apply(_harmony);
            QuickRestartPatches.Apply(_harmony);
            LifecycleAndPerformancePatches.Apply(_harmony);
        });

        ApplyPatchGroup("LAN/mod-loader diagnostic patches", () =>
        {
            LanMultiplayerBootstrapPatches.Apply(_harmony);
            ModLoaderPatches.Apply(_harmony);
            SaveDiagnosticPatches.Apply(_harmony);
        });

        ApplyPatchGroup("Render diagnostic patches", () => RenderDiagnosticPatches.Apply(_harmony));
        PatchHelper.Log("Android port compatibility patch pass finished.");
        LogRuntimeSnapshot("apply_finished");
    }

    private static void ApplyPatchGroup(string label, Action apply)
    {
        try
        {
            apply();
            PatchHelper.Log($"{label} applied.");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"{label} failed: {exception}");
        }
    }

    private static void LogRuntimeSnapshot(string phase)
    {
        try
        {
            var assemblyDir = Path.GetDirectoryName(SafeAssemblyLocation(typeof(ModEntry).Assembly));
            var filesDir = TryResolveFilesDirFromPublishDir(assemblyDir);
            PatchHelper.Log($"DIAG runtime_snapshot phase={phase}; assembly_dir={assemblyDir}; files_dir={filesDir}; base_dir={AppContext.BaseDirectory}; cwd={SafeCurrentDirectory()}; TMPDIR={System.Environment.GetEnvironmentVariable("TMPDIR") ?? "<null>"}; TMP={System.Environment.GetEnvironmentVariable("TMP") ?? "<null>"}; TEMP={System.Environment.GetEnvironmentVariable("TEMP") ?? "<null>"}; Path.GetTempPath={SafeTempPath()}; loaded_assemblies={DescribeLoadedAssemblies()}");
            LogFileSnapshot(phase, assemblyDir, "STS2Mobile.dll");
            LogFileSnapshot(phase, assemblyDir, "0Harmony.dll");
            LogFileSnapshot(phase, assemblyDir, "MonoMod.Core.dll");
            LogFileSnapshot(phase, assemblyDir, "MonoMod.Utils.dll");
            LogFileSnapshot(phase, assemblyDir, "MonoMod.Iced.dll");
            LogFileSnapshot(phase, assemblyDir, "GodotSharp.dll");
            LogFileSnapshot(phase, assemblyDir, "sts2.dll");
            LogFileSnapshot(phase, assemblyDir, "sts2.deps.json");
            LogFileSnapshot(phase, assemblyDir, "sts2.runtimeconfig.json");
            if (!string.IsNullOrWhiteSpace(filesDir))
            {
                LogFileSnapshot(phase, filesDir, "port_compat.pck");
                LogFileSnapshot(phase, Path.Combine(filesDir, "launcher"), "selected_instance.json");
                LogFileSnapshot(phase, Path.Combine(filesDir, "launcher"), "selected_compat_pack.json");
            }
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"DIAG runtime_snapshot failed phase={phase}: {exception}");
        }
    }

    private static void LogFileSnapshot(string phase, string dir, string name)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir))
            {
                PatchHelper.Log($"DIAG file_snapshot phase={phase}; name={name}; dir=<empty>");
                return;
            }
            var path = Path.Combine(dir, name);
            var file = new FileInfo(path);
            PatchHelper.Log($"DIAG file_snapshot phase={phase}; path={path}; exists={file.Exists}; bytes={(file.Exists ? file.Length : -1L)}; mtime_utc={(file.Exists ? file.LastWriteTimeUtc.ToString("O") : "<missing>")}");
            if (file.Exists && file.Length <= 8192 && (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)))
            {
                var text = File.ReadAllText(path).Replace('\n', ' ').Replace('\r', ' ');
                if (text.Length > 2000)
                    text = text.Substring(0, 2000) + "...<truncated>";
                PatchHelper.Log($"DIAG file_text phase={phase}; path={path}; text={text}");
            }
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"DIAG file_snapshot failed phase={phase}; dir={dir}; name={name}; error={exception.Message}");
        }
    }

    private static string DescribeLoadedAssemblies()
    {
        try
        {
            var names = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetName().Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Where(name => name.Equals("STS2Mobile", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("sts2", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("0Harmony", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("MonoMod", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("GodotSharp", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase);
            return string.Join(",", names);
        }
        catch (Exception exception)
        {
            return "<failed:" + exception.Message + ">";
        }
    }

    private static string SafeAssemblyLocation(System.Reflection.Assembly assembly)
    {
        try
        {
            return assembly?.Location ?? "<null>";
        }
        catch (Exception exception)
        {
            return "<failed:" + exception.Message + ">";
        }
    }

    private static string SafeCurrentDirectory()
    {
        try
        {
            return Directory.GetCurrentDirectory();
        }
        catch (Exception exception)
        {
            return "<failed:" + exception.Message + ">";
        }
    }

    private static string SafeTempPath()
    {
        try
        {
            return Path.GetTempPath();
        }
        catch (Exception exception)
        {
            return "<failed:" + exception.Message + ">";
        }
    }

    private static void EnsureAndroidTempDirectory()
    {
        try
        {
            // Android does not guarantee a process-wide /tmp directory. MonoMod/Harmony's
            // shared-state bootstrap writes a temporary DMD assembly via Path.GetTempPath();
            // if the runtime falls back to /tmp on devices where it is absent,
            // HarmonySharedState's type initializer is permanently poisoned and every patch
            // appears to fail even though STS2Mobile.dll was loaded successfully.
            //
            // This entry point runs very early in GodotSharp startup. Avoid Godot APIs here:
            // on some vendor ROMs, calling Engine/OS before the script bridge has finished
            // initializing can trip native StringName lifetime assertions and terminate the
            // process. Derive a writable private directory from this managed assembly instead.
            var tempDir = ResolveWritableTempDirectory();
            if (string.IsNullOrWhiteSpace(tempDir))
            {
                PatchHelper.Log("Failed to configure Android temp directory; no writable candidate found. Harmony may fall back to /tmp.");
                return;
            }

            System.Environment.SetEnvironmentVariable("TMPDIR", tempDir);
            System.Environment.SetEnvironmentVariable("TMP", tempDir);
            System.Environment.SetEnvironmentVariable("TEMP", tempDir);

            PatchHelper.Log($"Android temp directory configured for Harmony/MonoMod: {tempDir}");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Failed to configure Android temp directory; Harmony may fall back to /tmp: {exception}");
        }
    }

    private static string ResolveWritableTempDirectory()
    {
        var assemblyDir = Path.GetDirectoryName(typeof(ModEntry).Assembly.Location);
        var filesDir = TryResolveFilesDirFromPublishDir(assemblyDir);

        string[] candidates =
        {
            TryBuildEnvironmentCandidate("TMPDIR", null),
            TryBuildEnvironmentCandidate("TMP", null),
            TryBuildEnvironmentCandidate("TEMP", null),
            TryGetRuntimeTempPath(),
            string.IsNullOrWhiteSpace(filesDir) ? null : Path.Combine(filesDir, "tmp"),
            string.IsNullOrWhiteSpace(assemblyDir) ? null : Path.Combine(assemblyDir, "tmp"),
            TryBuildEnvironmentCandidate("HOME", "tmp"),
            TryBuildEnvironmentCandidate("ANDROID_DATA", "local/tmp/sts2mobile"),
        };

        foreach (var candidate in candidates)
        {
            if (TryPrepareTempDirectory(candidate, out var prepared))
                return prepared;
        }
        return null;
    }

    private static string TryResolveFilesDirFromPublishDir(string assemblyDir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(assemblyDir))
                return null;

            var dir = new DirectoryInfo(assemblyDir);
            // Expected: <files>/.godot/mono/publish/arm64/STS2Mobile.dll
            if (dir.Name == "arm64"
                && dir.Parent?.Name == "publish"
                && dir.Parent.Parent?.Name == "mono"
                && dir.Parent.Parent.Parent?.Name == ".godot"
                && dir.Parent.Parent.Parent.Parent != null)
            {
                return dir.Parent.Parent.Parent.Parent.FullName;
            }
        }
        catch
        {
            // Try the next candidate.
        }
        return null;
    }

    private static string TryBuildEnvironmentCandidate(string variable, string relativePath)
    {
        try
        {
            var root = System.Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrWhiteSpace(root))
                return null;
            return string.IsNullOrWhiteSpace(relativePath) ? root : Path.Combine(root, relativePath);
        }
        catch
        {
            return null;
        }
    }

    private static string TryGetRuntimeTempPath()
    {
        try
        {
            return Path.GetTempPath();
        }
        catch
        {
            return null;
        }
    }

    private static bool TryPrepareTempDirectory(string candidate, out string prepared)
    {
        prepared = null;
        try
        {
            if (string.IsNullOrWhiteSpace(candidate) || candidate == "/tmp")
                return false;

            var fullPath = Path.GetFullPath(candidate);
            Directory.CreateDirectory(fullPath);

            // Validate writability now so Harmony/MonoMod does not discover a bad temp path
            // inside HarmonySharedState's static initializer, which cannot recover cleanly.
            var probe = Path.Combine(fullPath, ".sts2mobile_tmp_probe");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);

            prepared = fullPath;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
