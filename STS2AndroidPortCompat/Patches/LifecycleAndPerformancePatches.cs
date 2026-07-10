using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using STS2Mobile.Android;

namespace STS2Mobile.Patches;

public static class LifecycleAndPerformancePatches
{
    private static bool _safePreloadStarted;
    private static bool _startupResourcePreloadStarted;
    private static bool _startupPreloadCompleted;
    private static string _preloadPhase = "startup";
    private static readonly object PostPreloadMissLock = new object();
    private static readonly Dictionary<string, int> PostPreloadMissCategoryCounts = new Dictionary<string, int>(StringComparer.Ordinal);
    private static readonly HashSet<string> PostPreloadMissLoggedPaths = new HashSet<string>(StringComparer.Ordinal);
    private static readonly HashSet<string> LearnedWarmAssetPaths = new HashSet<string>(StringComparer.Ordinal);
    private static readonly object ProtectedWarmCacheLock = new object();
    private static readonly HashSet<string> ProtectedWarmAssetPaths = new HashSet<string>(StringComparer.Ordinal);
    private static readonly string[] AndroidRuntimePinnedAssetPaths =
    {
        "res://images/packed/combat_ui/end_turn_button.png",
        "res://images/packed/combat_ui/end_turn_button_glow.png",
        "res://scenes/cards/holders/hand_card_holder.tscn",
        "res://scenes/cards/holders/selected_hand_card_holder.tscn",
        "res://scenes/cards/card_portrait_blur_material.tres",
        "res://scenes/cards/card_canvas_group_blur_material.tres",
        "res://scenes/cards/card_canvas_group_mask_blur_material.tres",
        "res://scenes/cards/card_canvas_group_mask_material.tres",
        "res://materials/cards/banners/card_banner_common_mat.tres",
        "res://materials/cards/banners/card_banner_uncommon_mat.tres",
        "res://materials/cards/banners/card_banner_rare_mat.tres",
        "res://materials/cards/banners/card_banner_curse_mat.tres",
        "res://materials/cards/banners/card_banner_status_mat.tres",
        "res://materials/cards/banners/card_banner_event_mat.tres",
        "res://materials/cards/banners/card_banner_quest_mat.tres",
        "res://materials/cards/banners/card_banner_ancient_mat.tres",
        "res://materials/cards/frames/card_frame_red_mat.tres",
        "res://materials/cards/frames/card_frame_green_mat.tres",
        "res://materials/cards/frames/card_frame_blue_mat.tres",
        "res://materials/cards/frames/card_frame_pink_mat.tres",
        "res://materials/cards/frames/card_frame_orange_mat.tres",
        "res://materials/cards/frames/card_frame_colorless_mat.tres",
        "res://materials/cards/frames/card_frame_curse_mat.tres",
        "res://materials/cards/frames/card_frame_quest_mat.tres",
        "res://scenes/ui/multiplayer_vote_icon.tscn",
        "res://scenes/vfx/vfx_block.tscn",
        "res://scenes/vfx/hit_spark_vfx.tscn",
        "res://scenes/vfx/vfx_damage_num.tscn",
        "res://scenes/vfx/vfx_attack_slash.tscn",
        "res://scenes/vfx/vfx_attack_blunt.tscn",
    };
    private static int _postPreloadMissCount;
    private static bool _learnedWarmAssetsLoaded;
    private static bool _learnedWarmAssetSaveScheduled;
    private static bool _learnedWarmAssetSaveDirty;
    private const int MaxLearnedWarmAssetPaths = 512;

    public static void Apply(Harmony harmony)
    {
        // v0.106.1 beta safety: do not patch OneTimeInitialization/NGame startup
        // methods. Those early Harmony lookups/replacements can initialize
        // GodotSharp ResourceFormatLoader MethodName statics too early and abort
        // Godot 4.5 with StringName refcount errors (_recognize_path/_reset_state).
        // Start a visible, serialized preload later from NMainMenu._Ready instead.
        PatchHelper.Log("Android startup Harmony overrides disabled; safe deferred preload will run after scene startup.");
        PatchHelper.Patch(harmony, typeof(NMainMenu), "_Ready", postfix: PatchHelper.Method(typeof(LifecycleAndPerformancePatches), nameof(MainMenuReadyPostfix)));
        PatchHelper.Patch(harmony, typeof(PreloadManager), "LoadCommonAndMainMenuAssets", prefix: PatchHelper.Method(typeof(LifecycleAndPerformancePatches), nameof(LoadCommonAndMainMenuAssetsPrefix)));
        PatchHelper.Patch(harmony, typeof(PreloadManager), "LoadAssetSets", prefix: PatchHelper.Method(typeof(LifecycleAndPerformancePatches), nameof(LoadAssetSetsPrefix)));
        var cacheLoadAsset = AccessTools.Method(typeof(AssetCache), "LoadAsset", new[] { typeof(string) });
        if (cacheLoadAsset != null)
        {
            harmony.Patch(cacheLoadAsset, prefix: new HarmonyMethod(PatchHelper.Method(typeof(LifecycleAndPerformancePatches), nameof(AssetCacheLoadAssetPrefix))));
            PatchHelper.Log("Patched AssetCache.LoadAsset for Android preload diagnostics.");
        }
        var cacheUnloadAssets = AccessTools.Method(typeof(AssetCache), "UnloadAssets", new[] { typeof(IEnumerable<string>) });
        if (cacheUnloadAssets != null)
        {
            harmony.Patch(cacheUnloadAssets, prefix: new HarmonyMethod(PatchHelper.Method(typeof(LifecycleAndPerformancePatches), nameof(AssetCacheUnloadAssetsPrefix))));
            PatchHelper.Log("Patched AssetCache.UnloadAssets for Android protected warm cache.");
        }
        var cacheUnloadMissedAssets = AccessTools.Method(typeof(AssetCache), "UnloadMissedCacheAssets");
        if (cacheUnloadMissedAssets != null)
        {
            harmony.Patch(cacheUnloadMissedAssets, prefix: new HarmonyMethod(PatchHelper.Method(typeof(LifecycleAndPerformancePatches), nameof(AssetCacheUnloadMissedCacheAssetsPrefix))));
            PatchHelper.Log("Patched AssetCache.UnloadMissedCacheAssets for Android protected warm cache.");
        }

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
            PreloadManager.Enabled = IsMasterPreloadEnabled();
            PatchHelper.Log($"Preload enabled from Android companion settings: {PreloadManager.Enabled}");
            LogPreloadSettings("ExecuteVeryEarly");
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
        PreloadManager.Enabled = IsMasterPreloadEnabled();
        LogPreloadSettings($"StartSafeDeferredPreload:{reason}");
        if (!ShouldRunAnyStartupPreload())
        {
            PatchHelper.Log($"Android safe preload not started ({reason}): startup preload settings disabled.");
            return;
        }
        _safePreloadStarted = true;
        PatchHelper.Log($"Android safe preload scheduled ({reason}).");
        // Godot Callable cannot marshal Task return values to Variant. Fire the
        // async preload from a statement-bodied void callable instead of passing
        // an async or expression-bodied Task-returning lambda.
        Callable.From(() =>
        {
            _ = RunSafeDeferredPreloadAsync(reason, owner);
        }).CallDeferred();
    }

    private static async Task RunSafeDeferredPreloadAsync(string reason, Node owner)
    {
        AndroidStartupLoadingScreen loadingScreen = null;
        try
        {
            await Task.Yield();
            await WaitForFramesAsync(2);
            loadingScreen = await ShowDeferredPreloadScreenAsync(owner);
            if (ShouldRunCommonMainMenuResourcePreload() && TryMarkStartupResourcePreloadStarted())
                await RunSerializedAndroidPreloadAsync(loadingScreen);
            if (IsCombatCodeWarmupEnabled())
            {
                loadingScreen?.SetStatus("Optimizing combat code...", "Preparing first attack hot paths", 0.9f);
                using var phase = EnterPreloadPhase("combat-code-warmup");
                AndroidStartupLoadingScreen.PrewarmCombatHotPaths();
                await WaitForFramesAsync(1);
            }
            var vfxMode = GetVfxWarmupMode();
            if (!string.Equals(vfxMode, "off", StringComparison.OrdinalIgnoreCase))
                await AndroidStartupLoadingScreen.WarmupCriticalScenesAsync(loadingScreen, vfxMode, IsVfxTreeWarmupEnabled(), GetVfxTreeWarmupFrames(), IsVfxWarmupRetainCacheEnabled(), GetVfxTreeWarmupScope());
            if (IsGameplayAssetPreloadEnabled() || IsLearnedWarmListEnabled())
                await RunGameplayWarmCachePreloadAsync(loadingScreen);
            if (IsMenuHotspotPreloadEnabled())
                await WarmMainMenuHotspotsAsync(FindGameNode(owner), loadingScreen);
            ProtectWarmCacheIfEnabled();
            if (loadingScreen != null)
            {
                loadingScreen.SetStatus("Startup optimization complete", "Ready", 1f);
                await WaitForFramesAsync(2);
            }
            MarkStartupPreloadCompleted(reason);
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
        if (!OS.GetName().Equals("Android", StringComparison.OrdinalIgnoreCase) || ShouldUseOriginalCommonMainMenuPreload())
        {
            if (IsPreloadDebugEnabled())
                PatchHelper.Log($"[PreloadDiag] LoadCommonAndMainMenuAssets using original flow; settings={GetPreloadSettingsSummary()}");
            return true;
        }
        __result = LoadCommonAndMainMenuAssetsAndroidAsync();
        return false;
    }

    public static bool LoadAssetSetsPrefix(ref Task<AssetLoadingSession> __result, string name)
    {
        bool allow = ShouldAllowAssetSetPreload(name);
        if (IsPreloadDebugEnabled())
            PatchHelper.Log($"[PreloadDiag] LoadAssetSets name={name ?? "<null>"} allow={allow} phase={_preloadPhase} settings={GetPreloadSettingsSummary()}");
        if (!OS.GetName().Equals("Android", StringComparison.OrdinalIgnoreCase) || allow)
            return true;
        __result = Task.FromResult(AssetLoadingSession.Empty());
        PatchHelper.Log($"Android preload skipped by preload settings: {name}");
        return false;
    }

    public static void AssetCacheLoadAssetPrefix(string path)
    {
        bool debugEnabled = IsPreloadDebugEnabled();
        bool learningEnabled = IsLearnedWarmListEnabled();
        if (!debugEnabled && !learningEnabled)
            return;
        try
        {
            bool postStartupPreload = _startupPreloadCompleted;
            string category = CategorizeAssetPath(path);
            int postMissCount = 0;
            if (postStartupPreload && debugEnabled)
                postMissCount = RegisterPostPreloadCacheMiss(path, category);
            if (postStartupPreload && learningEnabled)
                RecordLearnedWarmAsset(path);
            if (debugEnabled)
                PatchHelper.Log($"[PreloadDiag] cache miss path={path} category={category} phase={_preloadPhase} preloadEnabled={PreloadManager.Enabled} postStartupPreload={postStartupPreload} postMisses={postMissCount:N0} cachedAssets={PreloadManager.Cache.GetCacheKeys().Count():N0}");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"[PreloadDiag] cache miss log failed for {path}: {exception.Message}");
        }
    }

    public static void AssetCacheUnloadAssetsPrefix(ref IEnumerable<string> __0)
    {
        if (__0 == null)
            return;
        try
        {
            var filtered = FilterAndroidProtectedAssets(__0, out int skipped);
            if (skipped > 0)
            {
                __0 = filtered;
                if (IsPreloadDebugEnabled())
                    PatchHelper.Log($"[PreloadDiag] protected Android runtime cache skipped {skipped:N0} unload candidate(s).");
            }
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Protected Android runtime cache unload filter failed: {exception.Message}");
        }
    }

    public static void AssetCacheUnloadMissedCacheAssetsPrefix(AssetCache __instance)
    {
        if (__instance == null)
            return;
        try
        {
            string[] protectedPaths = GetAndroidProtectedAssetPaths();
            if (protectedPaths.Length == 0)
                return;
            var field = AccessTools.Field(typeof(AssetCache), "_missedCacheAssets");
            if (field?.GetValue(__instance) is not HashSet<string> missedAssets || missedAssets.Count == 0)
                return;
            int removed = 0;
            foreach (string path in protectedPaths)
            {
                if (missedAssets.Remove(path))
                    removed++;
            }
            if (removed > 0 && IsPreloadDebugEnabled())
                PatchHelper.Log($"[PreloadDiag] protected Android runtime cache kept {removed:N0} missed cache asset(s).");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Protected Android runtime missed-cache filter failed: {exception.Message}");
        }
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
        if (!ShouldRunAnyStartupPreload())
        {
            await Task.Yield();
            PatchHelper.Log("Android common/main-menu preload skipped by preload settings.");
            return;
        }

        try
        {
            if (!TryMarkStartupResourcePreloadStarted())
            {
                PatchHelper.Log("Android common/main-menu preload already handled by another startup path.");
                return;
            }
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
        using var phase = EnterPreloadPhase("startup-common-main-menu");
        PreloadManager.Cache.UnloadMissedCacheAssets();
        var assetPaths = CollectCommonAndMainMenuAssetPaths();
        PatchHelper.Log($"Android common/main-menu preload begin: {assetPaths.Count:N0} resources.");
        if (IsPreloadDebugEnabled())
            LogAssetPathSummary("common/main-menu collection", assetPaths);
        loadingScreen?.SetStatus("Optimizing startup...", $"Preparing {assetPaths.Count:N0} resources", 0.05f);
        int loaded = 0;
        int cachedBefore = 0;
        int loadedNow = 0;
        int nullLoads = 0;
        int failed = 0;
        ulong started = Time.GetTicksMsec();
        foreach (string path in assetPaths)
        {
            try
            {
                if (PreloadManager.Cache.ContainsKey(path))
                {
                    cachedBefore++;
                }
                else
                {
                    Resource resource = ResourceLoader.Load<Resource>(path, null, ResourceLoader.CacheMode.Reuse);
                    if (resource != null)
                    {
                        PreloadManager.Cache.SetAsset(path, resource);
                        loadedNow++;
                    }
                    else
                    {
                        nullLoads++;
                        PatchHelper.Log($"Android preload returned null: {path}");
                    }
                }
            }
            catch (Exception exception)
            {
                failed++;
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
        PatchHelper.Log($"Android common/main-menu preload complete: visited={loaded:N0}/{assetPaths.Count:N0} loaded_now={loadedNow:N0} cached_before={cachedBefore:N0} null={nullLoads:N0} failed={failed:N0} elapsed={Time.GetTicksMsec() - started:N0}ms.");
        if (IsPreloadDebugEnabled())
            LogResourceStats("common/main-menu serialized preload detailed");
    }

    private static IReadOnlyList<string> CollectCommonAndMainMenuAssetPaths()
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (IsStartupCommonPreloadEnabled())
            AddAssetSet(result, "CommonAssets", () => AssetSets.CommonAssets);
        if (IsStartupMainMenuPreloadEnabled())
            AddAssetSet(result, "MainMenuSet", () => AssetSets.MainMenuSet);
        if (IsShaderResourcePreloadEnabled())
            AddAssetSet(result, "ShaderResources", GetShaderWarmupPaths);
        return result.Where(path => !string.IsNullOrWhiteSpace(path)).OrderBy(path => path, StringComparer.Ordinal).ToArray();
    }

    private static async Task RunGameplayWarmCachePreloadAsync(AndroidStartupLoadingScreen loadingScreen)
    {
        using var phase = EnterPreloadPhase("gameplay-warm-cache");
        var assetPaths = CollectGameplayWarmAssetPaths();
        if (assetPaths.Count == 0)
        {
            PatchHelper.Log("Android gameplay warm cache skipped: no assets collected.");
            return;
        }

        PatchHelper.Log($"Android gameplay warm cache preload begin: {assetPaths.Count:N0} resources.");
        if (IsPreloadDebugEnabled())
            LogAssetPathSummary("gameplay warm cache collection", assetPaths);
        await LoadAndCacheAssetsAsync(assetPaths, loadingScreen, "Preloading gameplay resources...", 0.82f, 0.985f);
        if (IsProtectWarmCacheEnabled())
            PinWarmAssets(assetPaths, "gameplay warm cache");
    }

    private static IReadOnlyList<string> CollectGameplayWarmAssetPaths()
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (IsGameplayAssetPreloadEnabled())
        {
            AddAssetSet(result, "GameplayStaticHotspots", GetStaticGameplayWarmPaths);
            AddAssetSet(result, "GameplayModelAssets", GetModelWarmAssetPaths);
            AddAssetSet(result, "GameplayVfxCache", GetRetainedVfxWarmPaths);
        }
        if (IsLearnedWarmListEnabled())
        {
            AddAssetSet(result, "LearnedWarmAssets", GetLearnedWarmAssetPaths);
        }
        return result.Where(IsLoadableWarmPath).OrderBy(path => path, StringComparer.Ordinal).ToArray();
    }

    private static async Task LoadAndCacheAssetsAsync(IReadOnlyList<string> assetPaths, AndroidStartupLoadingScreen loadingScreen, string title, float progressStart, float progressEnd)
    {
        int loaded = 0;
        int cachedBefore = 0;
        int loadedNow = 0;
        int nullLoads = 0;
        int failed = 0;
        ulong started = Time.GetTicksMsec();
        for (int i = 0; i < assetPaths.Count; i++)
        {
            string path = assetPaths[i];
            try
            {
                if (PreloadManager.Cache.ContainsKey(path))
                {
                    cachedBefore++;
                }
                else
                {
                    Resource resource = ResourceLoader.Load<Resource>(path, null, ResourceLoader.CacheMode.Reuse);
                    if (resource != null)
                    {
                        PreloadManager.Cache.SetAsset(path, resource);
                        loadedNow++;
                    }
                    else
                    {
                        nullLoads++;
                        if (nullLoads <= 20)
                            PatchHelper.Log($"Android warm cache returned null: {path}");
                    }
                }
            }
            catch (Exception exception)
            {
                failed++;
                if (failed <= 30)
                    PatchHelper.Log($"Android warm cache skipped {path}: {exception.Message}");
            }

            loaded++;
            if ((loaded % 8) == 0)
            {
                float progress = assetPaths.Count == 0 ? progressEnd : progressStart + ((progressEnd - progressStart) * loaded / assetPaths.Count);
                loadingScreen?.SetStatus(title, $"{loaded:N0}/{assetPaths.Count:N0}: {GetResourceDisplayName(path)}", progress);
                await WaitForFramesAsync(1);
            }
        }

        loadingScreen?.SetStatus(title, $"Preloaded {loaded:N0}/{assetPaths.Count:N0} gameplay resources", progressEnd);
        PatchHelper.Log($"Android gameplay warm cache preload complete: visited={loaded:N0}/{assetPaths.Count:N0} loaded_now={loadedNow:N0} cached_before={cachedBefore:N0} null={nullLoads:N0} failed={failed:N0} elapsed={Time.GetTicksMsec() - started:N0}ms.");
    }

    private static IEnumerable<string> GetStaticGameplayWarmPaths()
    {
        return new[]
        {
            "res://images/packed/sprite_fonts/ironclad_energy_icon.png",
            "res://images/packed/sprite_fonts/silent_energy_icon.png",
            "res://images/packed/sprite_fonts/regent_energy_icon.png",
            "res://images/packed/sprite_fonts/necrobinder_energy_icon.png",
            "res://images/packed/sprite_fonts/defect_energy_icon.png",
            "res://images/packed/sprite_fonts/colorless_energy_icon.png",
            "res://images/packed/map/drawing_clear.png",
            "res://images/packed/map/drawing_clear_glow.png",
            "res://images/atlases/intent_atlas.sprites/intent_attack.tres",
            "res://images/atlases/intent_atlas.sprites/intent_attack_buff.tres",
            "res://images/atlases/intent_atlas.sprites/intent_attack_debuff.tres",
            "res://images/atlases/intent_atlas.sprites/intent_attack_defend.tres",
            "res://images/atlases/intent_atlas.sprites/intent_buff.tres",
            "res://images/atlases/intent_atlas.sprites/intent_debuff.tres",
            "res://images/atlases/intent_atlas.sprites/intent_defend.tres",
            "res://images/atlases/intent_atlas.sprites/intent_escape.tres",
            "res://images/atlases/intent_atlas.sprites/intent_sleep.tres",
            "res://images/atlases/intent_atlas.sprites/intent_stun.tres",
            "res://images/atlases/intent_atlas.sprites/intent_unknown.tres",
            "res://materials/vfx/potion/potion_liquid_overlay.tres",
            "res://scenes/ui/card_hover_tip.tscn",
            "res://scenes/ui/abandon_run_confirm_popup.tscn",
        };
    }

    private static IEnumerable<string> GetModelWarmAssetPaths()
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        TryAddModelAssets(result, "AllCards.RunAssetPaths", () => ModelDb.AllCards.SelectMany(card => card.RunAssetPaths));
        TryAddModelAssets(result, "AllCharacters.AssetPaths", () => ModelDb.AllCharacters.SelectMany(character => character.AssetPaths));
        TryAddModelAssets(result, "AllCharacters.CharacterSelect", () => ModelDb.AllCharacters.SelectMany(character => character.AssetPathsCharacterSelect));
        TryAddModelAssets(result, "AllActs.AssetPaths", () => ModelDb.Acts.SelectMany(act => act.AssetPaths));
        TryAddModelAssets(result, "AllPowers.IconPaths", () => ModelDb.AllPowers.SelectMany(power => GetReflectedStringPaths(power, "IconPath", "PackedIconPath", "ResolvedBigIconPath")));
        TryAddModelAssets(result, "AllRelics.IconPaths", () => ModelDb.AllRelics.SelectMany(relic => GetReflectedStringPaths(relic, "IconPath", "PackedIconPath", "ResolvedBigIconPath")));
        TryAddModelAssets(result, "AllPotions.ImagePaths", () => ModelDb.AllPotions.SelectMany(potion => GetReflectedStringPaths(potion, "ImagePath", "OutlinePath")));
        TryAddModelAssets(result, "AllOrbs.AssetPaths", () => ModelDb.Orbs.SelectMany(orb => orb.AssetPaths));
        TryAddModelAssets(result, "AllEvents.BasicAssetPaths", () => ModelDb.AllEvents.SelectMany(GetEventWarmAssetPaths));
        TryAddModelAssets(result, "AllEncounters.StaticAssets", () => ModelDb.AllEncounters.SelectMany(GetEncounterWarmAssetPaths));
        return result;
    }

    private static IEnumerable<string> GetRetainedVfxWarmPaths()
    {
        if (!IsVfxWarmupRetainCacheEnabled())
            return Array.Empty<string>();
        try
        {
            return AndroidStartupLoadingScreen.GetWarmupScenePathsForMode(GetVfxWarmupMode());
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Failed to collect retained VFX warm paths: {exception.Message}");
            return Array.Empty<string>();
        }
    }

    private static IEnumerable<string> GetEventWarmAssetPaths(EventModel eventModel)
    {
        if (eventModel == null)
            return Array.Empty<string>();
        var result = new HashSet<string>(StringComparer.Ordinal);
        result.UnionWith(GetReflectedStringPaths(eventModel, "LayoutScenePath", "InitialPortraitPath", "BackgroundScenePath", "VfxPath"));
        return result;
    }

    private static IEnumerable<string> GetEncounterWarmAssetPaths(EncounterModel encounter)
    {
        if (encounter == null)
            return Array.Empty<string>();
        var result = new HashSet<string>(StringComparer.Ordinal);
        result.UnionWith(GetReflectedStringPaths(encounter, "ScenePath", "BossNodePath"));
        TryAddModelAssets(result, "Encounter.ExtraAssetPaths", () => encounter.ExtraAssetPaths);
        TryAddModelAssets(result, "Encounter.MapNodeAssetPaths", () => encounter.MapNodeAssetPaths);
        TryAddModelAssets(result, "Encounter.AllPossibleMonsters", () => encounter.AllPossibleMonsters.SelectMany(monster => monster.AssetPaths));
        return result;
    }

    private static void TryAddModelAssets(HashSet<string> target, string label, Func<IEnumerable<string>> provider)
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
            PatchHelper.Log($"Android gameplay warm asset collection failed for {label}: {exception.Message}");
        }
    }

    private static IEnumerable<string> GetReflectedStringPaths(object source, params string[] propertyNames)
    {
        if (source == null)
            yield break;
        Type type = source.GetType();
        foreach (string propertyName in propertyNames)
        {
            string value = null;
            try
            {
                var property = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (property != null && property.PropertyType == typeof(string))
                    value = property.GetValue(source) as string;
            }
            catch
            {
                value = null;
            }
            if (!string.IsNullOrWhiteSpace(value))
                yield return value;
        }
    }

    private static bool IsLoadableWarmPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith("res://", StringComparison.Ordinal))
            return false;
        if (path.Contains("..", StringComparison.Ordinal))
            return false;
        return path.EndsWith(".tscn", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".tres", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".res", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".gdshader", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase);
    }

    private static void ProtectWarmCacheIfEnabled()
    {
        if (!IsProtectWarmCacheEnabled())
            return;
        try
        {
            var loaded = PreloadManager.Cache.GetLoadedCacheAssets();
            PinWarmAssets(loaded, "loaded startup cache");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Android warm cache protection failed: {exception.Message}");
        }
    }

    private static void PinWarmAssets(IEnumerable<string> paths, string reason)
    {
        try
        {
            var pathList = paths.Where(IsLoadableWarmPath).Distinct(StringComparer.Ordinal).ToArray();
            if (pathList.Length == 0)
                return;
            lock (ProtectedWarmCacheLock)
            {
                foreach (string path in pathList)
                    ProtectedWarmAssetPaths.Add(path);
            }
            TryCallNativePinAssets(pathList);
            PatchHelper.Log($"Android warm cache protected {pathList.Length:N0} asset(s): {reason}.");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Android warm cache pin failed ({reason}): {exception.Message}");
        }
    }

    private static IEnumerable<string> FilterAndroidProtectedAssets(IEnumerable<string> paths, out int skipped)
    {
        string[] protectedPaths = GetAndroidProtectedAssetPaths();
        if (protectedPaths.Length == 0)
        {
            skipped = 0;
            return paths;
        }
        var protectedSet = new HashSet<string>(protectedPaths, StringComparer.Ordinal);
        var result = new List<string>();
        skipped = 0;
        foreach (string path in paths)
        {
            if (protectedSet.Contains(path))
            {
                skipped++;
                continue;
            }
            result.Add(path);
        }
        return result;
    }

    private static string[] GetAndroidProtectedAssetPaths()
    {
        if (IsProtectWarmCacheEnabled())
        {
            lock (ProtectedWarmCacheLock)
                return AndroidRuntimePinnedAssetPaths.Concat(ProtectedWarmAssetPaths).Distinct(StringComparer.Ordinal).ToArray();
        }
        return AndroidRuntimePinnedAssetPaths;
    }

    private static void TryCallNativePinAssets(string[] pathList)
    {
        try
        {
            var method = typeof(AssetCache).GetMethod("PinAssets", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(IEnumerable<string>) }, null);
            method?.Invoke(PreloadManager.Cache, new object[] { pathList });
        }
        catch (Exception exception)
        {
            if (IsPreloadDebugEnabled())
                PatchHelper.Log($"Native AssetCache.PinAssets unavailable: {exception.Message}");
        }
    }

    private static void LoadLearnedWarmAssets()
    {
        if (_learnedWarmAssetsLoaded)
            return;
        _learnedWarmAssetsLoaded = true;
        try
        {
            string path = GetLearnedWarmAssetsPath();
            if (!File.Exists(path))
                return;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return;
            foreach (JsonElement element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String)
                    continue;
                string assetPath = element.GetString();
                if (IsLoadableWarmPath(assetPath))
                    LearnedWarmAssetPaths.Add(assetPath);
            }
            PatchHelper.Log($"Loaded {LearnedWarmAssetPaths.Count:N0} learned warm asset path(s).");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Failed to load learned warm assets: {exception.Message}");
        }
    }

    private static IEnumerable<string> GetLearnedWarmAssetPaths()
    {
        LoadLearnedWarmAssets();
        return LearnedWarmAssetPaths;
    }

    private static void RecordLearnedWarmAsset(string path)
    {
        if (!IsLoadableWarmPath(path))
            return;
        try
        {
            LoadLearnedWarmAssets();
            lock (PostPreloadMissLock)
            {
                if (!LearnedWarmAssetPaths.Add(path))
                    return;
                if (LearnedWarmAssetPaths.Count > MaxLearnedWarmAssetPaths)
                {
                    var trimmed = LearnedWarmAssetPaths.OrderBy(value => value, StringComparer.Ordinal).Take(MaxLearnedWarmAssetPaths).ToArray();
                    LearnedWarmAssetPaths.Clear();
                    foreach (string item in trimmed)
                        LearnedWarmAssetPaths.Add(item);
                }
                ScheduleLearnedWarmAssetsSaveLocked();
            }
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Failed to record learned warm asset {path}: {exception.Message}");
        }
    }

    private static void ScheduleLearnedWarmAssetsSaveLocked()
    {
        _learnedWarmAssetSaveDirty = true;
        if (_learnedWarmAssetSaveScheduled)
            return;
        _learnedWarmAssetSaveScheduled = true;
        _ = Task.Run(SaveLearnedWarmAssetsEventuallyAsync);
    }

    private static async Task SaveLearnedWarmAssetsEventuallyAsync()
    {
        while (true)
        {
            try
            {
                await Task.Delay(750);
                string[] snapshot;
                lock (PostPreloadMissLock)
                {
                    if (!_learnedWarmAssetSaveDirty)
                    {
                        _learnedWarmAssetSaveScheduled = false;
                        return;
                    }
                    _learnedWarmAssetSaveDirty = false;
                    snapshot = LearnedWarmAssetPaths.OrderBy(value => value, StringComparer.Ordinal).ToArray();
                }
                SaveLearnedWarmAssetsSnapshot(snapshot);
            }
            catch (Exception exception)
            {
                lock (PostPreloadMissLock)
                {
                    _learnedWarmAssetSaveScheduled = false;
                    _learnedWarmAssetSaveDirty = false;
                }
                PatchHelper.Log($"Failed to save learned warm assets: {exception.Message}");
                return;
            }
        }
    }

    private static void SaveLearnedWarmAssetsSnapshot(string[] paths)
    {
        try
        {
            string path = GetLearnedWarmAssetsPath();
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllText(path, JsonSerializer.Serialize(paths, new JsonSerializerOptions { WriteIndented = true }));
            if (IsPreloadDebugEnabled())
                PatchHelper.Log($"Saved {paths.Length:N0} learned warm asset path(s).");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Failed to save learned warm assets: {exception.Message}");
        }
    }

    private static string GetLearnedWarmAssetsPath()
    {
        return Path.Combine(AppPaths.DataDir, "launcher", "preload-learned-assets.json");
    }

    private static void AddAssetSet(HashSet<string> target, string label, Func<IEnumerable<string>> provider)
    {
        try
        {
            int before = target.Count;
            int provided = 0;
            foreach (string path in provider())
            {
                provided++;
                if (!string.IsNullOrWhiteSpace(path))
                    target.Add(path);
            }
            if (IsPreloadDebugEnabled())
                PatchHelper.Log($"[PreloadDiag] asset set {label}: provided={provided:N0} unique_added={target.Count - before:N0} total_unique={target.Count:N0}");
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

        if (ShouldRunStartupWarmupScreen())
        {
            AssetLoadingSession session = await StartAndroidStartupWarmupAsync();
            await startupLoadingScreen.RunWarmup(session, IsCombatCodeWarmupEnabled(), GetVfxWarmupMode());
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
        PatchHelper.Log($"Startup warmup: skipping bulk asset preload; scene-only VFX warmup will run on the loading screen. settings={GetPreloadSettingsSummary()}");
        return Task.FromResult(AssetLoadingSession.Empty());
    }

    private static async Task WarmMainMenuHotspotsAsync(NGame game, AndroidStartupLoadingScreen startupLoadingScreen)
    {
        if (game == null || !IsMenuHotspotPreloadEnabled())
            return;
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
                using var phase = EnterPreloadPhase($"menu-hotspot:{submenuType.Name}");
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

    private static void MarkStartupPreloadCompleted(string reason)
    {
        _startupPreloadCompleted = true;
        if (IsPreloadDebugEnabled())
            PatchHelper.Log($"[PreloadDiag] startup preload completed reason={reason}; subsequent cache misses are post-startup-preload misses.");
    }

    private static int RegisterPostPreloadCacheMiss(string path, string category)
    {
        lock (PostPreloadMissLock)
        {
            _postPreloadMissCount++;
            if (!PostPreloadMissCategoryCounts.ContainsKey(category))
                PostPreloadMissCategoryCounts[category] = 0;
            PostPreloadMissCategoryCounts[category]++;

            bool firstPathLog = PostPreloadMissLoggedPaths.Add(path ?? "<null>");
            if (firstPathLog || _postPreloadMissCount % 25 == 0)
                PatchHelper.Log($"[PreloadDiag] post-preload cache miss summary total={_postPreloadMissCount:N0} categories={FormatPostPreloadMissCategories()} latest={path}");
            return _postPreloadMissCount;
        }
    }

    private static string FormatPostPreloadMissCategories()
    {
        return string.Join(", ", PostPreloadMissCategoryCounts
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}:{pair.Value:N0}"));
    }

    private static string CategorizeAssetPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "unknown";
        if (path.Contains("/vfx/", StringComparison.OrdinalIgnoreCase))
            return path.EndsWith(".tscn", StringComparison.OrdinalIgnoreCase) ? "scene/vfx" : "resource/vfx";
        if (path.Contains("/cards/", StringComparison.OrdinalIgnoreCase))
            return PathCategoryByExtension(path, "cards");
        if (path.Contains("/monsters/", StringComparison.OrdinalIgnoreCase))
            return PathCategoryByExtension(path, "monsters");
        if (path.Contains("/rooms/", StringComparison.OrdinalIgnoreCase) || path.Contains("/events/", StringComparison.OrdinalIgnoreCase))
            return PathCategoryByExtension(path, "rooms-events");
        if (path.Contains("/characters/", StringComparison.OrdinalIgnoreCase))
            return PathCategoryByExtension(path, "characters");
        if (path.Contains("/ui/", StringComparison.OrdinalIgnoreCase) || path.Contains("/screens/", StringComparison.OrdinalIgnoreCase))
            return PathCategoryByExtension(path, "ui");
        return PathCategoryByExtension(path, "other");
    }

    private static string PathCategoryByExtension(string path, string prefix)
    {
        if (path.EndsWith(".tscn", StringComparison.OrdinalIgnoreCase))
            return $"scene/{prefix}";
        if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            return $"texture/{prefix}";
        if (path.EndsWith(".tres", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".res", StringComparison.OrdinalIgnoreCase))
            return $"material/{prefix}";
        if (path.EndsWith(".gdshader", StringComparison.OrdinalIgnoreCase))
            return $"shader/{prefix}";
        if (path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase))
            return $"audio/{prefix}";
        return $"resource/{prefix}";
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

    private static bool IsMasterPreloadEnabled() => AndroidSettingsBridge.GetBool("preload_enabled", true);

    private static bool IsStartupCommonPreloadEnabled() => IsMasterPreloadEnabled() && AndroidSettingsBridge.GetBool("preload_startup_common_enabled", true);

    private static bool IsStartupMainMenuPreloadEnabled() => IsMasterPreloadEnabled() && AndroidSettingsBridge.GetBool("preload_startup_main_menu_enabled", true);

    private static bool IsMenuHotspotPreloadEnabled() => IsMasterPreloadEnabled() && AndroidSettingsBridge.GetBool("preload_menu_hotspots_enabled", false);

    private static bool IsCombatCodeWarmupEnabled() => IsMasterPreloadEnabled() && AndroidSettingsBridge.GetBool("preload_combat_code_enabled", false);

    private static bool IsRuntimePreloadEnabled() => IsMasterPreloadEnabled() && AndroidSettingsBridge.GetBool("preload_runtime_enabled", true);

    private static bool IsPreloadDebugEnabled() => AndroidSettingsBridge.GetBool("preload_debug_enabled", false);

    private static bool IsVfxTreeWarmupEnabled() => IsMasterPreloadEnabled() && AndroidSettingsBridge.GetBool("preload_vfx_tree_warmup_enabled", false);

    private static bool IsVfxWarmupRetainCacheEnabled() => IsMasterPreloadEnabled() && AndroidSettingsBridge.GetBool("preload_vfx_retain_cache_enabled", false);

    private static bool IsProtectWarmCacheEnabled() => IsMasterPreloadEnabled() && AndroidSettingsBridge.GetBool("preload_protect_warm_cache_enabled", true);

    private static bool IsGameplayAssetPreloadEnabled() => IsMasterPreloadEnabled() && AndroidSettingsBridge.GetBool("preload_gameplay_assets_enabled", false);

    private static bool IsLearnedWarmListEnabled() => IsMasterPreloadEnabled() && AndroidSettingsBridge.GetBool("preload_learned_assets_enabled", true);

    private static bool IsCombatHitEffectWarmupEnabled() => IsMasterPreloadEnabled() && AndroidSettingsBridge.GetBool("preload_combat_hit_effect_warmup_enabled", false);

    private static int GetVfxTreeWarmupFrames()
    {
        if (!IsVfxTreeWarmupEnabled())
            return 0;
        return Math.Max(1, Math.Min(12, AndroidSettingsBridge.GetInt("preload_vfx_tree_warmup_frames", 3)));
    }

    private static string GetVfxTreeWarmupScope()
    {
        if (!IsVfxTreeWarmupEnabled())
            return "safe";
        var value = AndroidSettingsBridge.GetString("preload_vfx_tree_warmup_scope", "safe").Trim().ToLowerInvariant();
        return value == "all" ? "all" : "safe";
    }

    private static string GetVfxWarmupMode()
    {
        if (!IsMasterPreloadEnabled())
            return "off";
        var value = AndroidSettingsBridge.GetString("preload_vfx_mode", "off").Trim().ToLowerInvariant();
        return value == "hot" || value == "full" ? value : "off";
    }

    private static string GetShaderWarmupMode()
    {
        if (!IsMasterPreloadEnabled())
            return "off";
        var value = AndroidSettingsBridge.GetString("preload_shader_mode", "off").Trim().ToLowerInvariant();
        return value == "load_resources" ? value : "off";
    }

    private static bool IsShaderResourcePreloadEnabled() => GetShaderWarmupMode() == "load_resources";

    private static bool ShouldRunCommonMainMenuResourcePreload() => IsStartupCommonPreloadEnabled() || IsStartupMainMenuPreloadEnabled() || IsShaderResourcePreloadEnabled();

    private static bool TryMarkStartupResourcePreloadStarted()
    {
        if (_startupResourcePreloadStarted)
            return false;
        _startupResourcePreloadStarted = true;
        return true;
    }

    private static bool ShouldUseOriginalCommonMainMenuPreload()
    {
        return IsMasterPreloadEnabled()
            && IsStartupCommonPreloadEnabled()
            && IsStartupMainMenuPreloadEnabled()
            && !IsShaderResourcePreloadEnabled();
    }

    private static bool ShouldRunStartupWarmupScreen() => IsMasterPreloadEnabled() && (IsCombatCodeWarmupEnabled() || GetVfxWarmupMode() != "off");

    private static bool ShouldRunAnyStartupPreload() => ShouldRunCommonMainMenuResourcePreload()
        || ShouldRunStartupWarmupScreen()
        || IsGameplayAssetPreloadEnabled()
        || IsLearnedWarmListEnabled()
        || IsMenuHotspotPreloadEnabled();

    private static bool ShouldAllowAssetSetPreload(string name)
    {
        if (!IsMasterPreloadEnabled())
            return false;
        if (string.Equals(name, "Common", StringComparison.Ordinal))
            return ShouldRunCommonMainMenuResourcePreload();
        if (string.Equals(name, "MainMenu", StringComparison.Ordinal))
            return IsStartupMainMenuPreloadEnabled();
        if (string.Equals(name, "MainMenuEssentials", StringComparison.Ordinal) || string.Equals(name, "IntroLogo", StringComparison.Ordinal))
            return true;
        return IsRuntimePreloadEnabled();
    }

    private static void LogPreloadSettings(string context)
    {
        if (IsPreloadDebugEnabled())
            PatchHelper.Log($"[PreloadDiag] settings at {context}: {GetPreloadSettingsSummary()} path={AndroidSettingsBridge.SettingsPath}");
    }

    private static string GetPreloadSettingsSummary()
    {
        return $"master={IsMasterPreloadEnabled()} startupCommon={IsStartupCommonPreloadEnabled()} startupMain={IsStartupMainMenuPreloadEnabled()} runtime={IsRuntimePreloadEnabled()} menuHotspots={IsMenuHotspotPreloadEnabled()} vfx={GetVfxWarmupMode()} combatCode={IsCombatCodeWarmupEnabled()} combatAnim={GetCombatAnimationWarmupMode()} combatAnimFrames={GetCombatAnimationWarmupFrames()} combatHitEffects={IsCombatHitEffectWarmupEnabled()} shader={GetShaderWarmupMode()} debug={IsPreloadDebugEnabled()} vfxTree={IsVfxTreeWarmupEnabled()} vfxTreeScope={GetVfxTreeWarmupScope()} vfxFrames={GetVfxTreeWarmupFrames()} vfxRetain={IsVfxWarmupRetainCacheEnabled()} protectWarmCache={IsProtectWarmCacheEnabled()} gameplayAssets={IsGameplayAssetPreloadEnabled()} learnedAssets={IsLearnedWarmListEnabled()}";
    }

    private static string GetCombatAnimationWarmupMode()
    {
        if (!IsMasterPreloadEnabled())
            return "off";
        return AndroidSettingsBridge.GetString("preload_combat_animation_warmup_mode", "off").Trim().ToLowerInvariant();
    }

    private static int GetCombatAnimationWarmupFrames()
    {
        if (!IsMasterPreloadEnabled())
            return 0;
        return Math.Max(1, Math.Min(12, AndroidSettingsBridge.GetInt("preload_combat_animation_warmup_frames", 1)));
    }

    private static void LogAssetPathSummary(string label, IReadOnlyList<string> paths)
    {
        try
        {
            int scenes = 0;
            int textures = 0;
            int shaders = 0;
            int materials = 0;
            int audio = 0;
            int other = 0;
            foreach (string path in paths)
            {
                if (path.EndsWith(".tscn", StringComparison.OrdinalIgnoreCase))
                    scenes++;
                else if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                    textures++;
                else if (path.EndsWith(".gdshader", StringComparison.OrdinalIgnoreCase))
                    shaders++;
                else if (path.EndsWith(".tres", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".res", StringComparison.OrdinalIgnoreCase))
                    materials++;
                else if (path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase))
                    audio++;
                else
                    other++;
            }
            PatchHelper.Log($"[PreloadDiag] {label}: total={paths.Count:N0} scenes={scenes:N0} textures={textures:N0} shaders={shaders:N0} materials={materials:N0} audio={audio:N0} other={other:N0}");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"[PreloadDiag] failed to summarize {label}: {exception.Message}");
        }
    }

    private static IDisposable EnterPreloadPhase(string phase)
    {
        return new PreloadPhaseScope(phase);
    }

    private sealed class PreloadPhaseScope : IDisposable
    {
        private readonly string _previous;

        public PreloadPhaseScope(string phase)
        {
            _previous = _preloadPhase;
            _preloadPhase = phase ?? "unknown";
            if (IsPreloadDebugEnabled())
                PatchHelper.Log($"[PreloadDiag] phase enter: {_preloadPhase}");
        }

        public void Dispose()
        {
            if (IsPreloadDebugEnabled())
                PatchHelper.Log($"[PreloadDiag] phase leave: {_preloadPhase}");
            _preloadPhase = _previous;
        }
    }

    private static IEnumerable<string> GetShaderWarmupPaths()
    {
        return new[]
        {
            "res://shaders/hsv.gdshader",
            "res://shaders/dark_blur.gdshader",
            "res://shaders/radial_blur.gdshader",
            "res://shaders/doom_overlay.gdshader",
            "res://shaders/vfx/distortion/vfx_screen_distortion_outward_shader.gdshader",
            "res://shaders/vfx/scream/vfx_scream_distortion_polar_shader.gdshader",
            "res://shaders/vfx/vfx_water_reflection_post.gdshader",
            "res://shaders/vfx/the_insatiable_sand_fall_2.gdshader",
            "res://shaders/blur/canvas_group_mask_blur.gdshader",
            "res://shaders/overlay_blend.gdshader",
            "res://shaders/mobile_compat/dark_blur_compat.gdshader",
            "res://shaders/mobile_compat/radial_blur_compat.gdshader",
            "res://shaders/mobile_compat/doom_overlay_compat.gdshader",
            "res://shaders/mobile_compat/screen_distortion_compat.gdshader",
            "res://shaders/mobile_compat/scream_distortion_compat.gdshader",
            "res://shaders/mobile_compat/water_reflection_post_compat.gdshader",
            "res://shaders/mobile_compat/sand_fall_post_compat.gdshader",
            "res://shaders/mobile_compat/overlay_blend_compat.gdshader",
        };
    }

    private static NGame FindGameNode(Node owner)
    {
        if (owner is NGame direct)
            return direct;
        var current = owner;
        while (current != null)
        {
            if (current is NGame game)
                return game;
            current = current.GetParent();
        }
        return NGame.Instance;
    }

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
