using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using STS2Mobile.Android;

namespace STS2Mobile.Patches;

public static class LifecycleAndPerformancePatches
{
    private static bool _safePreloadStarted;

    public static void Apply(Harmony harmony)
    {
        // v0.106.1 beta safety: do not patch OneTimeInitialization/NGame startup
        // methods. Those early Harmony lookups/replacements can initialize
        // GodotSharp ResourceFormatLoader MethodName statics too early and abort
        // Godot 4.5 with StringName refcount errors (_recognize_path/_reset_state).
        // Start a visible, serialized preload later from NMainMenu._Ready instead.
        PatchHelper.Log("Android startup Harmony overrides disabled; safe deferred preload will run after scene startup.");
        PatchHelper.Patch(harmony, typeof(NMainMenu), "_Ready", postfix: PatchHelper.Method(typeof(LifecycleAndPerformancePatches), nameof(MainMenuReadyPostfix)));

        var muteHandlerType = typeof(NGame).Assembly.GetType("MegaCrit.Sts2.Core.Nodes.NMuteInBackgroundHandler");
        if (muteHandlerType != null)
        {
            // Do not patch inherited Godot lifecycle wrappers (_Ready/_Process) or _Notification
            // in the imported PC assembly. On Android/Godot 4.5 those Harmony lookups can force
            // GodotSharp MethodName static constructors for Resource/ResourceFormat* to run while
            // the native engine is still initializing.
            PatchHelper.Log("Background audio lifecycle patch disabled on imported PC assembly for Android startup safety.");
        }
    }

    public static void ExecuteVeryEarlyPostfix()
    {
        try
        {
            PreloadManager.Enabled = AndroidSettingsBridge.GetBool("preload_enabled", PreloadManager.Enabled);
            PatchHelper.Log($"Preload enabled from Android companion settings: {PreloadManager.Enabled}");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"ExecuteVeryEarlyPostfix failed: {exception.Message}");
        }
    }

    public static void MainMenuReadyPostfix(NMainMenu __instance)
    {
        StartSafeDeferredPreload("MainMenuReady", __instance);
    }

    public static void StartSafeDeferredPreload(string reason, Node owner = null)
    {
        if (_safePreloadStarted)
            return;
        if (!OS.GetName().Equals("Android", StringComparison.OrdinalIgnoreCase))
            return;
        PreloadManager.Enabled = AndroidSettingsBridge.GetBool("preload_enabled", PreloadManager.Enabled);
        if (!PreloadManager.Enabled)
        {
            PatchHelper.Log($"Android safe preload not started ({reason}): preload_enabled=false.");
            return;
        }
        _safePreloadStarted = true;
        PatchHelper.Log($"Android safe preload scheduled ({reason}).");
        // Godot Callable cannot marshal Task return values to Variant. Fire the
        // async preload from a void callable instead of passing an async lambda.
        Callable.From(() => _ = RunSafeDeferredPreloadAsync(reason, owner)).CallDeferred();
    }

    private static async Task RunSafeDeferredPreloadAsync(string reason, Node owner)
    {
        AndroidStartupLoadingScreen loadingScreen = null;
        try
        {
            await Task.Yield();
            await WaitForFramesAsync(2);
            loadingScreen = await ShowDeferredPreloadScreenAsync(owner);
            await RunSerializedAndroidPreloadAsync(loadingScreen);
            if (loadingScreen != null)
            {
                loadingScreen.SetStatus("Startup optimization complete", "Ready", 1f);
                await WaitForFramesAsync(2);
            }
            LogResourceStats($"android safe preload complete ({reason})");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Android safe preload failed ({reason}): {exception}");
            LogResourceStats($"android safe preload failed ({reason})");
        }
        finally
        {
            if (loadingScreen != null && GodotObject.IsInstanceValid(loadingScreen))
                loadingScreen.QueueFree();
        }
    }

    private static async Task<AndroidStartupLoadingScreen> ShowDeferredPreloadScreenAsync(Node owner)
    {
        try
        {
            Node parent = owner ?? (Engine.GetMainLoop() as SceneTree)?.Root;
            if (parent == null || !GodotObject.IsInstanceValid(parent))
                return null;
            var loadingScreen = new AndroidStartupLoadingScreen { Name = "AndroidDeferredPreloadScreen" };
            parent.AddChild(loadingScreen);
            loadingScreen.SetStatus("Optimizing startup...", "Preparing resource warmup", 0f);
            await loadingScreen.PresentAsync();
            return loadingScreen;
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Android safe preload UI unavailable: {exception.Message}");
            return null;
        }
    }

    private static async Task WaitForFramesAsync(int frames)
    {
        var tree = Engine.GetMainLoop() as SceneTree;
        if (tree == null)
        {
            await Task.Yield();
            return;
        }
        for (int i = 0; i < frames; i++)
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
    }

    public static bool LaunchMainMenuPrefix(NGame __instance, bool skipLogo, ref Task __result)
    {
        if (!OS.GetName().Equals("Android", StringComparison.OrdinalIgnoreCase))
            return true;
        __result = LaunchMainMenuAndroidAsync(__instance, skipLogo);
        return false;
    }

    public static bool LoadCommonAndMainMenuAssetsPrefix(ref Task __result)
    {
        if (!OS.GetName().Equals("Android", StringComparison.OrdinalIgnoreCase))
            return true;
        __result = LoadCommonAndMainMenuAssetsAndroidAsync();
        return false;
    }

    private static async Task LaunchMainMenuAndroidAsync(NGame game, bool skipLogo)
    {
        PatchHelper.Log($"Android startup preload flow begin (skipLogo={skipLogo}, preload={PreloadManager.Enabled}).");
        Node logoAnimation = null;
        if (skipLogo)
        {
            await PreloadManager.LoadMainMenuEssentials();
        }
        else
        {
            await PreloadManager.LoadLogoAnimation();
            logoAnimation = CreateSceneNode("MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NLogoAnimation");
            if (logoAnimation != null)
                SetCurrentRootScene(game, logoAnimation);
            await PreloadManager.LoadMainMenuEssentials();
        }

        if (logoAnimation != null)
        {
            await TryPlayLogoAsync(game, logoAnimation);
        }

        AndroidStartupLoadingScreen startupLoadingScreen = await ShowStartupWarmupScreenAndLoadAssetsAsync(game, keepVisibleAfterWarmup: !PreloadManager.Enabled);

        if (startupLoadingScreen != null)
        {
            startupLoadingScreen.SetStatus("Loading main menu...", "Creating main menu scene", 0.95f);
            await game.ToSignal(game.GetTree(), SceneTree.SignalName.ProcessFrame);
        }

        await CallPrivateTask(game, "LoadMainMenu");
        await game.ToSignal(game.GetTree(), SceneTree.SignalName.ProcessFrame);
        await WarmMainMenuHotspotsAsync(game, startupLoadingScreen);

        if (startupLoadingScreen != null)
        {
            startupLoadingScreen.SetStatus("Startup complete", "Launching main menu", 1f);
            await game.ToSignal(game.GetTree(), SceneTree.SignalName.ProcessFrame);
            await DismissStartupLoadingScreenAsync(game, startupLoadingScreen);
        }

        PatchHelper.Log($"Android startup preload flow complete at {Time.GetTicksMsec():N0}ms.");
        LogResourceStats(PreloadManager.Enabled ? "main menu loaded (startup warmup complete)" : "main menu loaded (essential)");
        _ = TaskHelper.RunSafely(LoadCommonAndMainMenuAssetsAndroidAsync());
        TryCheckCommandLineJoin(game);
    }

    private static async Task LoadCommonAndMainMenuAssetsAndroidAsync()
    {
        if (!PreloadManager.Enabled)
        {
            await Task.Yield();
            PatchHelper.Log("Android common/main-menu preload skipped because preload_enabled is off.");
            return;
        }

        try
        {
            await RunSerializedAndroidPreloadAsync();
            LogResourceStats("android common/main-menu preload complete");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Android common/main-menu preload failed; continuing without full cache: {exception}");
            LogResourceStats("android common/main-menu preload failed");
        }
    }

    private static Task RunSerializedAndroidPreloadAsync()
    {
        return RunSerializedAndroidPreloadAsync(null);
    }

    private static async Task RunSerializedAndroidPreloadAsync(AndroidStartupLoadingScreen loadingScreen)
    {
        PreloadManager.Cache.UnloadMissedCacheAssets();
        var assetPaths = CollectCommonAndMainMenuAssetPaths();
        PatchHelper.Log($"Android common/main-menu preload begin: {assetPaths.Count:N0} resources.");
        loadingScreen?.SetStatus("Optimizing startup...", $"Preparing {assetPaths.Count:N0} resources", 0.05f);
        int loaded = 0;
        foreach (string path in assetPaths)
        {
            try
            {
                if (!PreloadManager.Cache.ContainsKey(path))
                {
                    Resource resource = ResourceLoader.Load<Resource>(path, null, ResourceLoader.CacheMode.Reuse);
                    if (resource != null)
                        PreloadManager.Cache.SetAsset(path, resource);
                    else
                        PatchHelper.Log($"Android preload returned null: {path}");
                }
            }
            catch (Exception exception)
            {
                PatchHelper.Log($"Android preload skipped {path}: {exception.Message}");
            }
            loaded++;
            if ((loaded % 8) == 0)
            {
                float progress = assetPaths.Count == 0 ? 1f : 0.05f + (0.9f * loaded / assetPaths.Count);
                loadingScreen?.SetStatus("Optimizing startup...", $"Preloading {loaded:N0}/{assetPaths.Count:N0}: {GetResourceDisplayName(path)}", progress);
                await WaitForFramesAsync(1);
            }
        }
        loadingScreen?.SetStatus("Optimizing startup...", $"Preloaded {loaded:N0}/{assetPaths.Count:N0} resources", 0.98f);
        PatchHelper.Log($"Android common/main-menu preload complete: {loaded:N0}/{assetPaths.Count:N0} resources visited.");
    }

    private static IReadOnlyList<string> CollectCommonAndMainMenuAssetPaths()
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        AddAssetSet(result, "CommonAssets", () => AssetSets.CommonAssets);
        AddAssetSet(result, "MainMenuSet", () => AssetSets.MainMenuSet);
        return result.Where(path => !string.IsNullOrWhiteSpace(path)).OrderBy(path => path, StringComparer.Ordinal).ToArray();
    }

    private static void AddAssetSet(HashSet<string> target, string label, Func<IEnumerable<string>> provider)
    {
        try
        {
            foreach (string path in provider())
            {
                if (!string.IsNullOrWhiteSpace(path))
                    target.Add(path);
            }
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Android preload asset collection failed for {label}: {exception.Message}");
        }
    }

    private static async Task<AndroidStartupLoadingScreen> ShowStartupWarmupScreenAndLoadAssetsAsync(NGame game, bool keepVisibleAfterWarmup)
    {
        bool transitionVisible = false;
        if (game.Transition != null)
        {
            transitionVisible = game.Transition.Visible;
            game.Transition.Visible = false;
        }

        var startupLoadingScreen = new AndroidStartupLoadingScreen { Name = "AndroidStartupLoadingScreen" };
        game.AddChild(startupLoadingScreen);
        await startupLoadingScreen.PresentAsync();

        if (PreloadManager.Enabled)
        {
            AssetLoadingSession session = await StartAndroidStartupWarmupAsync();
            await startupLoadingScreen.RunWarmup(session);
            LogResourceStats("startup warmup complete");
        }
        else
        {
            startupLoadingScreen.SetStatus("Loading main menu...", "Preload disabled", 0.25f);
            await game.ToSignal(game.GetTree(), SceneTree.SignalName.ProcessFrame);
        }

        if (!keepVisibleAfterWarmup)
        {
            startupLoadingScreen.QueueFree();
            if (game.Transition != null)
                game.Transition.Visible = transitionVisible;
            await game.ToSignal(game.GetTree(), SceneTree.SignalName.ProcessFrame);
            return null;
        }

        startupLoadingScreen.SetMeta("restore_transition_visible", transitionVisible);
        return startupLoadingScreen;
    }

    private static async Task DismissStartupLoadingScreenAsync(NGame game, AndroidStartupLoadingScreen startupLoadingScreen)
    {
        bool transitionVisible = startupLoadingScreen.HasMeta("restore_transition_visible") && (bool)startupLoadingScreen.GetMeta("restore_transition_visible");
        startupLoadingScreen.QueueFree();
        if (game.Transition != null)
            game.Transition.Visible = transitionVisible;
        await game.ToSignal(game.GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private static Task<AssetLoadingSession> StartAndroidStartupWarmupAsync()
    {
        PatchHelper.Log("Startup warmup: skipping bulk asset preload; scene-only VFX warmup will run on the loading screen.");
        return Task.FromResult(AssetLoadingSession.Empty());
    }

    private static async Task WarmMainMenuHotspotsAsync(NGame game, AndroidStartupLoadingScreen startupLoadingScreen)
    {
        try
        {
            object mainMenu = game.GetType().GetProperty("MainMenu", BindingFlags.Public | BindingFlags.Instance)?.GetValue(game);
            if (mainMenu == null)
                return;
            object submenuStack = mainMenu.GetType().GetProperty("SubmenuStack", BindingFlags.Public | BindingFlags.Instance)?.GetValue(mainMenu);
            if (submenuStack == null)
                return;
            MethodInfo getSubmenuType = submenuStack.GetType().GetMethod("GetSubmenuType", new[] { typeof(Type) });
            if (getSubmenuType == null)
                return;

            string[] hotTypeNames =
            {
                "MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NSingleplayerSubmenu",
                "MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NMultiplayerSubmenu",
            };

            for (int i = 0; i < hotTypeNames.Length; i++)
            {
                Type submenuType = game.GetType().Assembly.GetType(hotTypeNames[i]);
                if (submenuType == null)
                    continue;
                startupLoadingScreen?.SetStatus("Loading main menu...", $"Preparing menu {i + 1}/{hotTypeNames.Length}", 0.975f + (0.01f * i));
                getSubmenuType.Invoke(submenuStack, new object[] { submenuType });
                await game.ToSignal(game.GetTree(), SceneTree.SignalName.ProcessFrame);
            }

            PatchHelper.Log("Main menu hotspot preload complete: singleplayer/multiplayer submenus instantiated.");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Main menu hotspot preload skipped: {exception.Message}");
        }
    }

    private static string GetResourceDisplayName(string path)
    {
        int index = path.LastIndexOf('/');
        string text = index >= 0 ? path.Substring(index + 1) : path;
        int extensionIndex = text.LastIndexOf('.');
        if (extensionIndex > 0)
            text = text.Substring(0, extensionIndex);
        return text.Replace('_', ' ');
    }

    private static void LogResourceStats(string context)
    {
        try
        {
            ulong staticMemoryUsage = OS.GetStaticMemoryUsage();
            ulong videoMemoryUsage = RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.VideoMemUsed);
            int objectCount = (int)Performance.GetMonitor(Performance.Monitor.ObjectCount);
            int resourceCount = (int)Performance.GetMonitor(Performance.Monitor.ObjectResourceCount);
            int nodeCount = (int)Performance.GetMonitor(Performance.Monitor.ObjectNodeCount);
            int cachedAssets = PreloadManager.Cache.GetCacheKeys().Count();
            PatchHelper.Log($"[Startup] Resource stats ({context}): StaticMem={FormatBytes(staticMemoryUsage)}, VRAM={FormatBytes(videoMemoryUsage)}, Objects={objectCount:N0}, Resources={resourceCount:N0}, Nodes={nodeCount:N0}, CachedAssets={cachedAssets:N0}");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Resource stats failed ({context}): {exception.Message}");
        }
    }

    private static string FormatBytes(ulong bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        int index = 0;
        double value = bytes;
        while (value >= 1024.0 && index < units.Length - 1)
        {
            value /= 1024.0;
            index++;
        }
        return $"{value:0.#}{units[index]}";
    }

    private static Node CreateSceneNode(string typeName)
    {
        var type = typeof(NGame).Assembly.GetType(typeName);
        var method = type?.GetMethod("Create", BindingFlags.Public | BindingFlags.Static);
        return method?.Invoke(null, null) as Node;
    }

    private static void SetCurrentRootScene(NGame game, Node scene)
    {
        var container = game.RootSceneContainer;
        if (container == null || scene is not Control control)
            return;
        container.SetCurrentScene(control);
    }

    private static async Task TryPlayLogoAsync(NGame game, Node logoAnimation)
    {
        try
        {
            var transition = game.Transition;
            var fadeIn = transition?.GetType().GetMethod("FadeIn", new[] { typeof(float), typeof(string), typeof(System.Threading.CancellationToken?) });
            if (fadeIn != null)
                await (Task)fadeIn.Invoke(transition, new object[] { 0.8f, "res://materials/transitions/fade_transition_mat.tres", null });
            var playAnimation = logoAnimation.GetType().GetMethod("PlayAnimation", new[] { typeof(System.Threading.CancellationToken) });
            if (playAnimation != null)
                await (Task)playAnimation.Invoke(logoAnimation, new object[] { System.Threading.CancellationToken.None });
            var fadeOut = transition?.GetType().GetMethod("FadeOut", new[] { typeof(float), typeof(string), typeof(System.Threading.CancellationToken?) });
            if (fadeOut != null)
                await (Task)fadeOut.Invoke(transition, new object[] { 0.8f, "res://materials/transitions/fade_transition_mat.tres", null });
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Android logo flow failed; continuing to main menu: {exception.Message}");
        }
    }

    private static Task CallPrivateTask(object target, string methodName)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(bool) }, null)
            ?? target.GetType().GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
        if (method == null)
        {
            PatchHelper.Log($"Android startup reflection failed: {target.GetType().Name}.{methodName} not found.");
            return Task.CompletedTask;
        }
        var parameters = method.GetParameters().Length == 1 ? new object[] { false } : null;
        return method.Invoke(target, parameters) as Task ?? Task.CompletedTask;
    }

    private static void TryCheckCommandLineJoin(NGame game)
    {
        try
        {
            var field = typeof(NGame).GetField("_joinCallbackHandler", BindingFlags.NonPublic | BindingFlags.Instance);
            var handler = field?.GetValue(game);
            handler?.GetType().GetMethod("CheckForCommandLineJoin", BindingFlags.Public | BindingFlags.Instance)?.Invoke(handler, null);
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Android command-line join check failed: {exception.Message}");
        }
    }

    public static void MuteReadyPostfix(object __instance)
    {
        try
        {
            if (!IsAudioCompatibilityMode())
            {
                var node = (Godot.Node)__instance;
                node.ProcessMode = Godot.Node.ProcessModeEnum.Always;
                node.SetProcess(true);
            }
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"MuteReadyPostfix failed: {exception.Message}");
        }
    }

    public static bool MuteProcessPrefix() => IsAudioCompatibilityMode();

    public static bool MuteNotificationPrefix(object __instance, int what)
    {
        if (IsAudioCompatibilityMode())
            return true;
        try
        {
            switch (what)
            {
                case (int)Godot.Node.NotificationWMWindowFocusOut:
                case (int)Godot.Node.NotificationApplicationPaused:
                case (int)Godot.Node.NotificationApplicationFocusOut:
                    CallPrivate(__instance, "Mute");
                    return false;
                case (int)Godot.Node.NotificationWMWindowFocusIn:
                case (int)Godot.Node.NotificationApplicationResumed:
                case (int)Godot.Node.NotificationApplicationFocusIn:
                    CallPrivate(__instance, "Unmute");
                    return false;
            }
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"MuteNotificationPrefix failed: {exception.Message}");
        }
        return true;
    }

    private static bool IsAudioCompatibilityMode() => AndroidSettingsBridge.GetBool("audio_compatibility_mode", false);

    private static void CallPrivate(object target, string methodName)
    {
        target.GetType().GetMethod(methodName, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.Invoke(target, null);
    }
}
