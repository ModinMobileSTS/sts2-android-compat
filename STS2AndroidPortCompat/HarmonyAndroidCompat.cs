using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HarmonyLib;

namespace STS2Mobile;

public static class HarmonyAndroidCompat
{
    private const string SelfTestHarmonyId = "sts2.android.harmony.selftest";
    private const string LibcShimLibraryName = "monomod_android_libc_shim";
    private const string EnableHarmonySelfTestFlag = "enable_harmony_selftest.flag";
    private const string EnableMonoModLogFlag = "enable_monomod_harmony_log.flag";
    private const string EnableOldHarmonyBootstrapFlag = "enable_old_harmony_compat_bootstrap.flag";

    private static bool _initialized;
    private static bool _selfTestInstalled;
    private static bool _nativeResolverInstalled;
    private static IntPtr _libcShimHandle;

    public static void Initialize()
    {
        if (_initialized)
        {
            PatchHelper.Log("[HarmonyAndroid] Initialize skipped because it already ran.");
            return;
        }
        _initialized = true;

        if (!IsAndroid())
            return;

        PatchHelper.Log($"[HarmonyAndroid] Preparing launcher-style minimal Harmony bootstrap. Harmony={typeof(Harmony).Assembly.FullName} DynamicCodeSupported={RuntimeFeature.IsDynamicCodeSupported} DynamicCodeCompiled={RuntimeFeature.IsDynamicCodeCompiled} ProcessArchitecture={RuntimeInformation.ProcessArchitecture}");
        LogMonoModAssemblySnapshot();

        // HarmonyOS can report enough POSIX/Linux details for MonoMod to miss the Android
        // specialization.  A missed Android detection reaches PlatformTriple.CreateCurrentSystem()
        // as OSKind.Posix and throws a bare NotImplementedException from UpdateWrapper().  Force
        // the real MonoMod.Utils fields before DetourFactory/PlatformTriple is first touched.
        TryForceMonoLikeRuntimeDetection();
        TryForceAndroidPlatformDetection();
        TryLogMonoModDetection("after_platform_override");
        TryLogResolvedMonoModBackend();
        HarmonyMethodReferenceImporterShim.Initialize();

        if (ShouldEnableMonoModFileLogging())
        {
            TryEnableMonoModFileLogging();
        }
        else
        {
            PatchHelper.Log($"[HarmonyAndroid] MonoMod file logging disabled by default; create launcher/{EnableMonoModLogFlag} to enable.");
        }

        if (ShouldEnableOldHarmonyBootstrap())
        {
            PatchHelper.Log($"[HarmonyAndroid] Enabling old native resolver / DMDType=cecil bootstrap because launcher/{EnableOldHarmonyBootstrapFlag} exists.");
            TryInstallNativeImportResolver();
            TryPreloadOptionalManagedDependencies();
            TryForcePortableDmdBackend();
            TryLogResolvedMonoModBackend();
        }
        else
        {
            PatchHelper.Log($"[HarmonyAndroid] Old native resolver / DMDType=cecil bootstrap skipped by default to match the working ../s2 path; create launcher/{EnableOldHarmonyBootstrapFlag} only for diagnostics.");
        }

        RunSelfTestIfRequested();
    }

    private static bool IsAndroid()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Create("ANDROID")))
            return true;

        try
        {
            var bytes = File.ReadAllBytes("/proc/self/cmdline");
            var raw = System.Text.Encoding.UTF8.GetString(bytes);
            var processName = raw.Split('\0')[0].Trim();
            if (processName.Contains("com.megacrit.sts2re", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        catch
        {
        }

        try
        {
            return Directory.Exists("/apex/com.android.runtime")
                || Directory.Exists("/data/user/0/com.megacrit.sts2re")
                || Directory.Exists("/data/data/com.megacrit.sts2re")
                || File.Exists("/system/build.prop");
        }
        catch
        {
            return false;
        }
    }

    private static bool ShouldEnableMonoModFileLogging()
    {
        return LauncherFlagExists(EnableMonoModLogFlag)
            || IsEnvironmentEnabled("STS2_ANDROID_ENABLE_MONOMOD_LOG");
    }

    private static bool ShouldEnableOldHarmonyBootstrap()
    {
        return LauncherFlagExists(EnableOldHarmonyBootstrapFlag)
            || IsEnvironmentEnabled("STS2_ANDROID_ENABLE_OLD_HARMONY_BOOTSTRAP");
    }

    private static bool LauncherFlagExists(string fileName)
    {
        try
        {
            return File.Exists(Path.Combine(ResolveAndroidFilesDir(), "launcher", fileName));
        }
        catch
        {
            return false;
        }
    }

    private static bool IsEnvironmentEnabled(string name)
    {
        try
        {
            var value = System.Environment.GetEnvironmentVariable(name);
            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void TryEnableMonoModFileLogging()
    {
        try
        {
            var logsDir = Path.Combine(ResolveAndroidFilesDir(), "logs");
            Directory.CreateDirectory(logsDir);
            var path = Path.Combine(logsDir, "monomod-harmony.log");
            Harmony.SetSwitch("LogToFile", path);
            Harmony.SetSwitch("LogSpam", true);
            Harmony.SetSwitch("LogToFileFilter", string.Empty);
            PatchHelper.Log($"[HarmonyAndroid] Enabled MonoMod file logging: {path}");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"[HarmonyAndroid] Failed to enable MonoMod file logging: {exception.Message}");
        }
    }

    private static string ResolveAndroidFilesDir()
    {
        var packageName = "com.megacrit.sts2re";
        try
        {
            var bytes = File.ReadAllBytes("/proc/self/cmdline");
            var raw = System.Text.Encoding.UTF8.GetString(bytes);
            var processName = raw.Split('\0')[0].Trim();
            if (!string.IsNullOrWhiteSpace(processName))
            {
                var colon = processName.IndexOf(':');
                packageName = colon > 0 ? processName.Substring(0, colon) : processName;
            }
        }
        catch
        {
        }

        var userPath = $"/data/user/0/{packageName}/files";
        if (Directory.Exists(userPath))
            return userPath;
        var dataPath = $"/data/data/{packageName}/files";
        if (Directory.Exists(dataPath))
            return dataPath;
        return userPath;
    }

    private static void TryInstallNativeImportResolver()
    {
        if (_nativeResolverInstalled)
            return;

        var harmonyAssembly = typeof(Harmony).Assembly;
        try
        {
            NativeLibrary.SetDllImportResolver(harmonyAssembly, ResolveHarmonyNativeImport);
            _nativeResolverInstalled = true;
            PatchHelper.Log("[HarmonyAndroid] Installed legacy native import resolver for Harmony assembly.");
            var handle = ResolveHarmonyNativeImport("libc", harmonyAssembly, null);
            PatchHelper.Log(handle != IntPtr.Zero
                ? $"[HarmonyAndroid] Loaded Android libc shim: {LibcShimLibraryName}"
                : "[HarmonyAndroid] Android libc shim not preloaded by legacy resolver.");
        }
        catch (InvalidOperationException exception)
        {
            _nativeResolverInstalled = true;
            PatchHelper.Log($"[HarmonyAndroid] Native import resolver already installed for Harmony assembly: {exception.Message}");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"[HarmonyAndroid] Failed to install Harmony native import resolver: {exception}");
        }
    }

    private static IntPtr ResolveHarmonyNativeImport(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!IsAndroid())
            return IntPtr.Zero;
        if (!string.Equals(libraryName, "libc", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(libraryName, "libc.so", StringComparison.OrdinalIgnoreCase))
        {
            return IntPtr.Zero;
        }

        if (_libcShimHandle != IntPtr.Zero)
            return _libcShimHandle;

        foreach (var candidate in new[] { LibcShimLibraryName, "lib" + LibcShimLibraryName + ".so" })
        {
            try
            {
                _libcShimHandle = NativeLibrary.Load(candidate, assembly, searchPath);
                if (_libcShimHandle != IntPtr.Zero)
                    return _libcShimHandle;
            }
            catch
            {
                _libcShimHandle = IntPtr.Zero;
            }
        }
        return IntPtr.Zero;
    }

    private static void TryPreloadOptionalManagedDependencies()
    {
        TryPreloadManagedAssembly("Iced");
        TryPreloadManagedAssembly("MonoMod.Iced");
    }

    private static void TryPreloadManagedAssembly(string simpleName)
    {
        try
        {
            var loaded = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(assembly => string.Equals(assembly.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase));
            if (loaded != null)
            {
                PatchHelper.Log($"[HarmonyAndroid] Optional managed dependency already loaded: {loaded.FullName}");
                return;
            }

            var assembly = Assembly.Load(new AssemblyName(simpleName));
            PatchHelper.Log($"[HarmonyAndroid] Preloaded optional managed dependency: {assembly.FullName} location={SafeAssemblyLocation(assembly)}");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"[HarmonyAndroid] Failed to preload optional managed dependency {simpleName}: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static void TryForcePortableDmdBackend()
    {
        try
        {
            Harmony.SetSwitch("DMDType", "cecil");
            PatchHelper.Log("[HarmonyAndroid] Forced MonoMod DMDType=cecil for Android diagnostics.");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"[HarmonyAndroid] Failed to force DMDType=cecil: {exception.Message}");
        }
    }

    private static void TryForceMonoLikeRuntimeDetection()
    {
        try
        {
            var platformDetection = ResolveMonoModUtilsType("MonoMod.Utils.PlatformDetection");
            var runtimeKind = ResolveMonoModUtilsType("MonoMod.Utils.RuntimeKind");
            var corelibKind = ResolveMonoModUtilsType("MonoMod.Utils.CorelibKind");
            if (platformDetection == null || runtimeKind == null || corelibKind == null)
            {
                PatchHelper.Log($"[HarmonyAndroid] Could not locate MonoMod runtime detection types. platformDetection={platformDetection != null} runtimeKind={runtimeKind != null} corelibKind={corelibKind != null}");
                return;
            }

            var runtimeInitState = platformDetection.GetField("runtimeInitState", BindingFlags.NonPublic | BindingFlags.Static);
            var runtime = platformDetection.GetField("runtime", BindingFlags.NonPublic | BindingFlags.Static);
            var corelib = platformDetection.GetField("corelib", BindingFlags.NonPublic | BindingFlags.Static);
            var runtimeVersion = platformDetection.GetField("runtimeVersion", BindingFlags.NonPublic | BindingFlags.Static);
            if (runtimeInitState == null || runtime == null || corelib == null || runtimeVersion == null)
            {
                PatchHelper.Log("[HarmonyAndroid] Could not locate MonoMod runtime detection backing fields.");
                return;
            }

            var oldState = runtimeInitState.GetValue(null);
            var oldRuntime = runtime.GetValue(null);
            var oldCorelib = corelib.GetValue(null);
            var oldVersion = runtimeVersion.GetValue(null);
            var version = System.Environment.Version;
            runtime.SetValue(null, Enum.Parse(runtimeKind, "Mono"));
            corelib.SetValue(null, Enum.Parse(corelibKind, "Core"));
            runtimeVersion.SetValue(null, version);
            runtimeInitState.SetValue(null, 1);
            ResetMonoModCaches();
            PatchHelper.Log($"[HarmonyAndroid] Forced MonoMod runtime detection: state {oldState} -> 1, Runtime {oldRuntime} -> Mono, Corelib {oldCorelib} -> Core, Version {oldVersion ?? "<null>"} -> {version}, Mono.Runtime={Type.GetType("Mono.Runtime", throwOnError: false) != null}, Mono.RuntimeStructs={Type.GetType("Mono.RuntimeStructs", throwOnError: false) != null}, FrameworkDescription={RuntimeInformation.FrameworkDescription}");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"[HarmonyAndroid] Failed to force Mono-like runtime detection: {exception}");
        }
    }

    private static void TryForceAndroidPlatformDetection()
    {
        try
        {
            var platformDetection = ResolveMonoModUtilsType("MonoMod.Utils.PlatformDetection");
            var osKind = ResolveMonoModUtilsType("MonoMod.Utils.OSKind");
            var architectureKind = ResolveMonoModUtilsType("MonoMod.Utils.ArchitectureKind");
            if (platformDetection == null || osKind == null || architectureKind == null)
            {
                PatchHelper.Log($"[HarmonyAndroid] Could not locate MonoMod platform detection types. platformDetection={platformDetection != null} osKind={osKind != null} architectureKind={architectureKind != null}");
                return;
            }

            var platformInitState = platformDetection.GetField("platInitState", BindingFlags.NonPublic | BindingFlags.Static);
            var os = platformDetection.GetField("os", BindingFlags.NonPublic | BindingFlags.Static);
            var architecture = platformDetection.GetField("arch", BindingFlags.NonPublic | BindingFlags.Static);
            if (platformInitState == null || os == null || architecture == null)
            {
                PatchHelper.Log("[HarmonyAndroid] Could not locate MonoMod PlatformDetection backing fields.");
                return;
            }

            var architectureName = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => "Arm64",
                Architecture.X64 => "x86_64",
                Architecture.X86 => "x86",
                Architecture.Arm => "Arm",
                _ => string.Empty,
            };
            if (string.IsNullOrWhiteSpace(architectureName))
            {
                PatchHelper.Log($"[HarmonyAndroid] Unsupported Android process architecture for MonoMod override: {RuntimeInformation.ProcessArchitecture}");
                return;
            }

            var oldState = platformInitState.GetValue(null);
            var oldOs = os.GetValue(null);
            var oldArchitecture = architecture.GetValue(null);
            os.SetValue(null, Enum.Parse(osKind, "Android"));
            architecture.SetValue(null, Enum.Parse(architectureKind, architectureName));
            platformInitState.SetValue(null, 1);
            ResetMonoModCaches();
            PatchHelper.Log($"[HarmonyAndroid] Forced MonoMod platform detection: state {oldState} -> 1, OS {oldOs} -> Android, Arch {oldArchitecture} -> {architectureName}");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"[HarmonyAndroid] Failed to force Android-like MonoMod platform detection: {exception}");
        }
    }

    private static void TryLogMonoModDetection(string phase)
    {
        try
        {
            var platformDetection = ResolveMonoModUtilsType("MonoMod.Utils.PlatformDetection");
            if (platformDetection == null)
            {
                PatchHelper.Log($"[HarmonyAndroid] MonoMod PlatformDetection type missing at {phase}.");
                return;
            }

            var os = ReadStaticProperty(platformDetection, "OS");
            var architecture = ReadStaticProperty(platformDetection, "Architecture");
            var runtime = ReadStaticProperty(platformDetection, "Runtime");
            var corelib = ReadStaticProperty(platformDetection, "Corelib");
            var runtimeVersion = ReadStaticProperty(platformDetection, "RuntimeVersion");
            PatchHelper.Log($"[HarmonyAndroid] MonoMod detection {phase}: OS={os} Architecture={architecture} Runtime={runtime} Corelib={corelib} RuntimeVersion={runtimeVersion}");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"[HarmonyAndroid] Failed to log MonoMod detection at {phase}: {exception}");
        }
    }

    private static object ReadStaticProperty(Type type, string name)
    {
        try
        {
            return type.GetProperty(name, BindingFlags.Public | BindingFlags.Static)?.GetValue(null) ?? "<null>";
        }
        catch (Exception exception)
        {
            return "<failed:" + exception.GetType().Name + ":" + exception.Message + ">";
        }
    }

    private static void TryLogResolvedMonoModBackend()
    {
        try
        {
            var coreAssembly = ResolveMonoModCoreAssembly();
            var platformTriple = coreAssembly?.GetType("MonoMod.Core.Platforms.PlatformTriple", throwOnError: false);
            var detourFactory = coreAssembly?.GetType("MonoMod.Core.DetourFactory", throwOnError: false);
            var currentTriple = platformTriple?.GetProperty("Current", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            var hostTriple = currentTriple?.GetType().GetProperty("HostTriple", BindingFlags.Public | BindingFlags.Instance)?.GetValue(currentTriple);
            var system = currentTriple?.GetType().GetProperty("System", BindingFlags.Public | BindingFlags.Instance)?.GetValue(currentTriple);
            var architecture = currentTriple?.GetType().GetProperty("Architecture", BindingFlags.Public | BindingFlags.Instance)?.GetValue(currentTriple);
            var runtime = currentTriple?.GetType().GetProperty("Runtime", BindingFlags.Public | BindingFlags.Instance)?.GetValue(currentTriple);
            var currentFactory = detourFactory?.GetProperty("Current", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            var defaultFactory = detourFactory?.GetProperty("Default", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            PatchHelper.Log($"[HarmonyAndroid] Resolved MonoMod backend: CoreAssembly={SafeAssemblyLocation(coreAssembly)} HostTriple={hostTriple} System={system?.GetType().FullName ?? "<null>"} Architecture={architecture?.GetType().FullName ?? "<null>"} Runtime={runtime?.GetType().FullName ?? "<null>"} DetourFactory.Current={currentFactory?.GetType().FullName ?? "<null>"} DetourFactory.Default={defaultFactory?.GetType().FullName ?? "<null>"}");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"[HarmonyAndroid] Failed to log resolved MonoMod backend: {exception}");
        }
    }

    private static void ResetMonoModCaches()
    {
        var coreAssembly = ResolveMonoModCoreAssembly();
        SetStaticField(coreAssembly?.GetType("MonoMod.Core.Platforms.PlatformTriple", throwOnError: false), "lazyCurrent", null);
        SetStaticField(coreAssembly?.GetType("MonoMod.Core.DetourFactory", throwOnError: false), "lazyCurrent", null);
        SetStaticField(coreAssembly?.GetType("MonoMod.Core.DetourFactory", throwOnError: false), "lazyDefault", null);
    }

    private static void SetStaticField(Type type, string fieldName, object value)
    {
        var field = type?.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static);
        field?.SetValue(null, value);
    }

    private static Type ResolveMonoModUtilsType(string typeName)
    {
        return ResolveMonoModType("MonoMod.Utils", typeName);
    }

    private static Type ResolveMonoModType(string assemblyName, string typeName)
    {
        var assembly = ResolveAssembly(assemblyName);
        var type = assembly?.GetType(typeName, throwOnError: false);
        if (type != null)
            return type;

        type = Type.GetType(typeName + ", " + assemblyName, throwOnError: false);
        if (type != null)
            return type;

        return typeof(Harmony).Assembly.GetType(typeName, throwOnError: false);
    }

    private static Assembly ResolveMonoModCoreAssembly()
    {
        return ResolveAssembly("MonoMod.Core") ?? typeof(Harmony).Assembly;
    }

    private static Assembly ResolveAssembly(string simpleName)
    {
        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(assembly => string.Equals(assembly.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase));
        if (loaded != null)
            return loaded;

        try
        {
            return Assembly.Load(new AssemblyName(simpleName));
        }
        catch
        {
        }

        try
        {
            var assemblyDir = Path.GetDirectoryName(typeof(HarmonyAndroidCompat).Assembly.Location);
            if (!string.IsNullOrWhiteSpace(assemblyDir))
            {
                var candidate = Path.Combine(assemblyDir, simpleName + ".dll");
                if (File.Exists(candidate))
                    return Assembly.LoadFrom(candidate);
            }
        }
        catch
        {
        }

        return null;
    }

    private static void LogMonoModAssemblySnapshot()
    {
        try
        {
            PatchHelper.Log($"[HarmonyAndroid] MonoMod assemblies: Harmony={SafeAssemblyLocation(typeof(Harmony).Assembly)} Utils={SafeAssemblyLocation(ResolveAssembly("MonoMod.Utils"))} Core={SafeAssemblyLocation(ResolveMonoModCoreAssembly())} RuntimeDetour={SafeAssemblyLocation(ResolveAssembly("MonoMod.RuntimeDetour"))} Iced={SafeAssemblyLocation(ResolveAssembly("MonoMod.Iced"))}");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"[HarmonyAndroid] Failed to log MonoMod assembly snapshot: {exception.Message}");
        }
    }

    private static string SafeAssemblyLocation(Assembly assembly)
    {
        if (assembly == null)
            return "<null>";
        try
        {
            return assembly.Location ?? "<null>";
        }
        catch (Exception exception)
        {
            return "<failed:" + exception.Message + ">";
        }
    }

    private static void RunSelfTestIfRequested()
    {
        if (_selfTestInstalled)
            return;
        if (!LauncherFlagExists(EnableHarmonySelfTestFlag))
        {
            PatchHelper.Log($"[HarmonyAndroid] Harmony self-test skipped; create launcher/{EnableHarmonySelfTestFlag} to enable diagnostics.");
            return;
        }
        _selfTestInstalled = true;

        try
        {
            var harmony = new Harmony(SelfTestHarmonyId);
            harmony.CreateClassProcessor(typeof(StaticTestPatch)).Patch();
            harmony.CreateClassProcessor(typeof(VirtualTestPatch)).Patch();
            var staticValue = StaticTestTarget.GetValue();
            var virtualValue = new VirtualTestTarget().GetValue();
            PatchHelper.Log($"[HarmonyAndroid] Self-test results: static={staticValue} virtual={virtualValue} static_ok={staticValue == 42} virtual_ok={virtualValue == 42}");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"[HarmonyAndroid] Harmony self-test failed: {exception}");
        }
    }

    private static class StaticTestTarget
    {
        public static int GetValue() => 1;
    }

    [HarmonyPatch(typeof(StaticTestTarget), nameof(StaticTestTarget.GetValue))]
    private static class StaticTestPatch
    {
        [HarmonyPostfix]
        private static void Postfix(ref int __result)
        {
            __result += 41;
        }
    }

    private class VirtualTestTarget
    {
        public virtual int GetValue() => 1;
    }

    [HarmonyPatch(typeof(VirtualTestTarget), nameof(VirtualTestTarget.GetValue))]
    private static class VirtualTestPatch
    {
        [HarmonyPostfix]
        private static void Postfix(ref int __result)
        {
            __result += 41;
        }
    }
}
