using System;
using System.IO;
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
            DllImportResolver resolver = new GodotDllImportResolver(godotDllHandle).OnResolveDllImport;
            NativeLibrary.SetDllImportResolver(typeof(GodotObject).Assembly, resolver);
            NativeFuncs.Initialize(unmanagedCallbacks, unmanagedCallbacksSize);
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
        if (_applied)
            return;
        _applied = true;

        EnsureAndroidTempDirectory();

        PatchHelper.Log("Initializing STS2Mobile Android port compatibility.");
        CompatBuildInfo.Log();
        _harmony = new Harmony("com.sts2mobile");

        // Reference launcher applies this outside the game-type try/catch so it
        // also runs in bootstrap/launcher-only mode.
        RenderDiagnosticPatches.Apply(_harmony);

        try
        {
            // Keep mod framework shims first.  They only register AssemblyLoad
            // listeners, and must be present before ModManager loads user DLLs.
            BaseLibCompatPatches.Apply(_harmony);
            RitsuLibCompatPatches.Apply(_harmony);
            ModelDbInitPatch.Apply(_harmony);
            UnlockStateCompatPatches.Apply(_harmony);
            PlatformPatches.Apply(_harmony);
            ReleaseInfoPatches.Apply(_harmony);
            SavePathPatches.Apply(_harmony);

            // Reference launcher's mobile/default settings + UI scale patches,
            // plus this port's Java companion settings bridge.
            SettingsPatches.Apply(_harmony);
            AndroidSettingsPatches.Apply(_harmony);
            DisplaySettingsPatches.Apply(_harmony);
            AndroidFontCoveragePatches.Apply(_harmony);
            UiScalePatches.Apply(_harmony);

            // Reference mobile layout/input fixes.
            MobileLayoutPatches.Apply(_harmony);
            EventLayoutPatches.Apply(_harmony);
            MerchantLayoutPatches.Apply(_harmony);
            TouchInputPatches.Apply(_harmony);
            CardRewardPatches.Apply(_harmony);
            EarlyAccessDisclaimerPatches.Apply(_harmony);
            FeedbackScreenPatches.Apply(_harmony);
            CombatBackgroundPatches.Apply(_harmony);

            // This port's extra Android UI/input/shader/lifecycle additions.
            AndroidUiSafetyPatches.Apply(_harmony);
            ExternalSettingsPatches.Apply(_harmony);
            AndroidInGameSettingsPatches.Apply(_harmony);
            ShaderCompatibilityPatches.Apply(_harmony);
            AndroidInputCompatPatches.Apply(_harmony);
            MobileSelectionConfirmationPatches.Apply(_harmony);
            MerchantSelectionConfirmationPatches.Apply(_harmony);
            MobileTapPreviewPatches.Apply(_harmony);
            MobileHandLayoutPatches.Apply(_harmony);
            IntentAnimationPatches.Apply(_harmony);
            QuickRestartPatches.Apply(_harmony);
            LifecycleAndPerformancePatches.Apply(_harmony);

            // Reference launcher applies LAN before mod-loader; keep the same
            // relative order while deferring the heavy LAN patch set to menu time.
            LanMultiplayerBootstrapPatches.Apply(_harmony);
            ModLoaderPatches.Apply(_harmony);
            SaveDiagnosticPatches.Apply(_harmony);

            PatchHelper.Log("All Android port compatibility patches applied.");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Patch application failed: {exception}");
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
