using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.TestSupport;

namespace STS2Mobile.Patches;

/// <summary>
/// Android imported-payload builds can otherwise hit long synchronous stalls when
/// opening menu sub-screens or entering the first room/combat.  Keep the game's
/// normal preload contract, but route it through a conservative serialized loader
/// so all resources end up in PreloadManager.Cache before gameplay needs them.
/// </summary>
public static class AndroidResourceWarmupPatches
{
    private const int FrameYieldEvery = 8;
    private static bool _cardUiWarmupStarted;

    public static void Apply(Harmony harmony)
    {
        PatchHelper.Patch(harmony, typeof(PreloadManager), "LoadCommonAndMainMenuAssets", prefix: PatchHelper.Method(typeof(AndroidResourceWarmupPatches), nameof(LoadCommonAndMainMenuAssetsPrefix)));
        PatchHelper.Patch(harmony, typeof(PreloadManager), "LoadMainMenuAssets", prefix: PatchHelper.Method(typeof(AndroidResourceWarmupPatches), nameof(LoadMainMenuAssetsPrefix)));
        PatchHelper.Patch(harmony, typeof(PreloadManager), "LoadRunAssets", prefix: PatchHelper.Method(typeof(AndroidResourceWarmupPatches), nameof(LoadRunAssetsPrefix)));
        PatchHelper.Patch(harmony, typeof(PreloadManager), "LoadActAssets", prefix: PatchHelper.Method(typeof(AndroidResourceWarmupPatches), nameof(LoadActAssetsPrefix)));
        PatchHelper.Patch(harmony, typeof(PreloadManager), "LoadRoomCombatAssets", prefix: PatchHelper.Method(typeof(AndroidResourceWarmupPatches), nameof(LoadRoomCombatAssetsPrefix)));
        PatchHelper.Patch(harmony, typeof(PreloadManager), "LoadRoomEventAssets", prefix: PatchHelper.Method(typeof(AndroidResourceWarmupPatches), nameof(LoadRoomEventAssetsPrefix)));
        PatchHelper.Patch(harmony, typeof(PreloadManager), "LoadRoomTreasureAssets", prefix: PatchHelper.Method(typeof(AndroidResourceWarmupPatches), nameof(LoadRoomTreasureAssetsPrefix)));
        PatchHelper.Patch(harmony, typeof(PreloadManager), "LoadRoomMerchantAssets", prefix: PatchHelper.Method(typeof(AndroidResourceWarmupPatches), nameof(LoadRoomMerchantAssetsPrefix)));
        PatchHelper.Patch(harmony, typeof(PreloadManager), "LoadRoomRestSite", prefix: PatchHelper.Method(typeof(AndroidResourceWarmupPatches), nameof(LoadRoomRestSitePrefix)));
        PatchHelper.Patch(harmony, typeof(NGame), "InitPools", postfix: PatchHelper.Method(typeof(AndroidResourceWarmupPatches), nameof(InitPoolsPostfix)));
    }

    public static void InitPoolsPostfix()
    {
        if (!ShouldOverride())
            return;
        Callable.From(() => TaskHelper.RunSafely(WarmCardUiAsync())).CallDeferred();
    }

    public static bool LoadCommonAndMainMenuAssetsPrefix(ref Task __result)
    {
        if (!ShouldOverride())
            return true;
        __result = LoadAssetSetsAndroidAsync("Common+MainMenu", true, () => AssetSets.CommonAssets, () => AssetSets.MainMenuSet);
        return false;
    }

    public static bool LoadMainMenuAssetsPrefix(ref Task __result)
    {
        if (!ShouldOverride() || TestMode.IsOn)
            return true;
        __result = LoadAssetSetsAndroidAsync("MainMenu", false, () => AssetSets.MainMenuSet);
        return false;
    }

    public static bool LoadRunAssetsPrefix(IEnumerable<CharacterModel> characters, ref Task __result)
    {
        if (!ShouldOverride() || TestMode.IsOn)
            return true;
        __result = LoadRunAssetsAndroidAsync(characters);
        return false;
    }

    public static bool LoadActAssetsPrefix(ActModel act, ref Task __result)
    {
        if (!ShouldOverride() || TestMode.IsOn)
            return true;
        __result = LoadActAssetsAndroidAsync(act);
        return false;
    }

    public static bool LoadRoomCombatAssetsPrefix(object encounter, object runState, ref Task __result)
    {
        if (!ShouldOverride() || TestMode.IsOn)
            return true;
        __result = LoadRoomAssetsAndroidAsync("Combat Room", () => GetCombatAssetPaths(encounter, runState));
        return false;
    }

    public static bool LoadRoomEventAssetsPrefix(object eventModel, object runState, ref Task __result)
    {
        if (!ShouldOverride() || TestMode.IsOn)
            return true;
        __result = LoadRoomAssetsAndroidAsync("Event Room", () => InvokeEnumerable(eventModel, "GetAssetPaths", runState));
        return false;
    }

    public static bool LoadRoomTreasureAssetsPrefix(ActModel actModel, ref Task __result)
    {
        if (!ShouldOverride() || TestMode.IsOn)
            return true;
        __result = LoadRoomAssetsAndroidAsync("Treasure Room", () => new[] { GetStringProperty(actModel, "ChestSpineResourcePath") }.Concat(GetStaticAssetPaths("MegaCrit.Sts2.Core.Nodes.Rooms.NTreasureRoom")));
        return false;
    }

    public static bool LoadRoomMerchantAssetsPrefix(ref Task __result)
    {
        if (!ShouldOverride() || TestMode.IsOn)
            return true;
        __result = LoadRoomAssetsAndroidAsync("Merchant Room", () => GetStaticAssetPaths("MegaCrit.Sts2.Core.Nodes.Rooms.NMerchantRoom"));
        return false;
    }

    public static bool LoadRoomRestSitePrefix(ActModel actModel, IEnumerable<object> restSiteOptions, ref Task __result)
    {
        if (!ShouldOverride() || TestMode.IsOn)
            return true;
        __result = LoadRoomAssetsAndroidAsync("RestSite Room", () => new[] { GetStringProperty(actModel, "RestSiteBackgroundPath") }.Concat(restSiteOptions.SelectMany(GetAssetPathsProperty)));
        return false;
    }

    private static async Task LoadRunAssetsAndroidAsync(IEnumerable<CharacterModel> characters)
    {
        var list = characters?.ToList() ?? new List<CharacterModel>();
        bool isMultiplayer = false;
        try
        {
            isMultiplayer = RunManager.Instance?.NetService?.Type.IsMultiplayer() ?? false;
        }
        catch
        {
            isMultiplayer = false;
        }
        AssetSets.RunSet = new HashSet<string>(GetRunAssetPaths(list, isMultiplayer));
        await LoadAssetSetsAndroidAsync("characters=" + string.Join(',', list.Select(c => c.Id.Entry)), false, () => AssetSets.CommonAssets, () => AssetSets.RunSet, () => GetAllKnownCardVisualAssetPaths());
        await WarmCardUiAsync();
        GC.Collect();
    }

    private static async Task LoadActAssetsAndroidAsync(ActModel act)
    {
        AssetSets.Act = new HashSet<string>(act.AssetPaths);
        await LoadAssetSetsAndroidAsync("Act=" + act.Id.Entry, false, () => AssetSets.CommonAssets, () => AssetSets.RunSet, () => AssetSets.Act, () => GetAllKnownCardVisualAssetPaths());
        await WarmCardUiAsync();
        GC.Collect();
    }

    private static async Task LoadRoomAssetsAndroidAsync(string roomName, Func<IEnumerable<string>> additionalAssetsProvider)
    {
        var roomAssets = new HashSet<string>(StringComparer.Ordinal);
        AddAssets(roomAssets, additionalAssetsProvider);
        await LoadAssetSetsAndroidAsync(roomName, false, () => AssetSets.CommonAssets, () => AssetSets.RunSet, () => AssetSets.Act, () => roomAssets);
        GC.Collect();
    }

    private static async Task LoadAssetSetsAndroidAsync(string name, bool unloadMissedFirst, params Func<IEnumerable<string>>[] providers)
    {
        if (!PreloadManager.Enabled)
        {
            await Task.Yield();
            return;
        }

        if (unloadMissedFirst)
            PreloadManager.Cache.UnloadMissedCacheAssets();

        var target = new HashSet<string>(StringComparer.Ordinal);
        foreach (var provider in providers)
            AddAssets(target, provider);

        var loaded = new HashSet<string>(PreloadManager.Cache.GetLoadedCacheAssets(), StringComparer.Ordinal);
        var stale = loaded.Except(target).ToArray();
        if (stale.Length > 0)
            PreloadManager.Cache.UnloadAssets(stale);

        var needLoaded = target.Except(loaded).Where(path => !string.IsNullOrWhiteSpace(path)).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        await Task.Yield();
        await LoadResourcesSerializedAsync(name, needLoaded);
    }

    private static async Task LoadResourcesSerializedAsync(string name, IReadOnlyList<string> paths)
    {
        var stopwatch = Stopwatch.StartNew();
        PatchHelper.Log($"[Preload] Android serialized load '{name}' begin: {paths.Count:N0} resources.");
        int loaded = 0;
        foreach (string path in paths)
        {
            try
            {
                if (!PreloadManager.Cache.ContainsKey(path))
                {
                    var resource = ResourceLoader.Load<Resource>(path, null, ResourceLoader.CacheMode.Reuse);
                    if (resource != null)
                        PreloadManager.Cache.SetAsset(path, resource);
                    else
                        PatchHelper.Log($"[Preload] Android load returned null: {path}");
                }
            }
            catch (Exception exception)
            {
                PatchHelper.Log($"[Preload] Android load skipped {path}: {exception.Message}");
            }

            loaded++;
            if ((loaded % FrameYieldEvery) == 0)
                await WaitForFrameAsync();
        }
        PatchHelper.Log($"[Preload] Android serialized load '{name}' complete: {loaded:N0}/{paths.Count:N0} in {stopwatch.ElapsedMilliseconds:N0}ms, cached={PreloadManager.Cache.GetCacheKeys().Count():N0}.");
    }

    private static async Task WarmCardUiAsync()
    {
        if (!PreloadManager.Enabled || TestMode.IsOn)
            return;
        if (_cardUiWarmupStarted)
            return;
        _cardUiWarmupStarted = true;

        var stopwatch = Stopwatch.StartNew();
        try
        {
            int warmedCards = 0;
            await LoadResourcesSerializedAsync("CardVisuals", GetAllKnownCardVisualAssetPaths().Distinct(StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal).ToArray());
            foreach (CardModel card in ModelDb.AllCards)
            {
                WarmOneCardModel(card);
                warmedCards++;
                if ((warmedCards % 12) == 0)
                    await WaitForFrameAsync();
            }

            WarmPool(typeof(NCard), 72);
            await WaitForFrameAsync();
            WarmPool(typeof(NGridCardHolder), 72);
            PatchHelper.Log($"[Preload] Android card UI warmup complete: cards={warmedCards:N0}, time={stopwatch.ElapsedMilliseconds:N0}ms.");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"[Preload] Android card UI warmup failed: {exception}");
        }
    }

    private static void WarmOneCardModel(CardModel card)
    {
        try
        {
            _ = card.Title;
            _ = card.EnergyCost;
            _ = card.DynamicVars;
            LoadCached(card.PortraitPath);
            if (card.Rarity == CardRarity.Ancient)
                _ = card.AncientTextBg;
            else
            {
                _ = card.Frame;
                _ = card.PortraitBorder;
                _ = card.BannerTexture;
            }
            _ = card.EnergyIcon;
            _ = card.BannerMaterial;
            _ = card.FrameMaterial;
            if (card.HasBuiltInOverlay)
                LoadCached(card.OverlayPath);
            var affliction = card.Affliction;
            if (affliction != null && affliction.HasOverlay)
                LoadCached(GetStringProperty(affliction, "OverlayPath"));
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"[Preload] Card model warmup skipped {card?.Id}: {exception.Message}");
        }
    }

    private static IEnumerable<string> GetAllKnownCardVisualAssetPaths()
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        AddAssets(paths, () => GetStaticAssetPaths("MegaCrit.Sts2.Core.Nodes.Cards.NCard"));
        AddAssets(paths, () => GetStaticAssetPaths("MegaCrit.Sts2.Core.Nodes.Cards.Holders.NGridCardHolder"));
        AddAssets(paths, () => GetStaticAssetPaths("MegaCrit.Sts2.Core.Nodes.Cards.Holders.NHandCardHolder"));
        AddAssets(paths, () => GetStaticAssetPaths("MegaCrit.Sts2.Core.Nodes.Cards.Holders.NPreviewCardHolder"));
        AddAssets(paths, () => GetStaticAssetPaths("MegaCrit.Sts2.Core.Nodes.Cards.Holders.NSelectedHandCardHolder"));
        AddAssets(paths, () => GetStaticAssetPaths("MegaCrit.Sts2.Core.Nodes.Cards.Holders.NHandCardHolder"));
        foreach (var pool in ModelDb.AllCardPools)
        {
            AddPath(paths, pool.FrameMaterialPath);
            AddPath(paths, pool.EnergyIconPath);
        }
        foreach (CardModel card in ModelDb.AllCards)
        {
            AddPath(paths, card.PortraitPath);
            try
            {
                if (ResourceLoader.Exists(card.BetaPortraitPath))
                    AddPath(paths, card.BetaPortraitPath);
            }
            catch { }
            if (card.Rarity == CardRarity.Ancient)
                TryAddPropertyResource(paths, card, "AncientTextBgPath");
            TryAddPropertyResource(paths, card, "FramePath");
            TryAddPropertyResource(paths, card, "PortraitBorderPath");
            TryAddPropertyResource(paths, card, "BannerTexturePath");
            TryAddPropertyResource(paths, card, "BannerMaterialPath");
            if (card.HasBuiltInOverlay)
                AddPath(paths, card.OverlayPath);
            var affliction = card.Affliction;
            if (affliction != null && affliction.HasOverlay)
                AddPath(paths, GetStringProperty(affliction, "OverlayPath"));
        }
        return paths;
    }

    private static void TryAddPropertyResource(HashSet<string> paths, object instance, string propertyName)
    {
        AddPath(paths, GetStringProperty(instance, propertyName));
    }

    private static void AddPath(HashSet<string> paths, string path)
    {
        if (!string.IsNullOrWhiteSpace(path))
            paths.Add(path);
    }

    private static void LoadCached(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || PreloadManager.Cache.ContainsKey(path))
            return;
        try
        {
            var resource = ResourceLoader.Load<Resource>(path, null, ResourceLoader.CacheMode.Reuse);
            if (resource != null)
                PreloadManager.Cache.SetAsset(path, resource);
        }
        catch
        {
            // Best effort: some optional overlays do not exist in older payloads.
        }
    }

    private static void WarmPool(Type poolableType, int targetFreeCount)
    {
        try
        {
            Type nodePoolType = typeof(NGame).Assembly.GetType("MegaCrit.Sts2.Core.Nodes.Pooling.NodePool");
            if (nodePoolType == null)
                return;
            MethodInfo getMethod = nodePoolType.GetMethod("Get", new[] { typeof(Type) });
            Type poolableInterface = poolableType.Assembly.GetType("MegaCrit.Sts2.Core.Nodes.Pooling.IPoolable");
            MethodInfo freeMethod = poolableInterface == null ? null : nodePoolType.GetMethod("Free", new[] { poolableInterface });
            if (getMethod == null || freeMethod == null)
                return;

            var objects = new List<object>(targetFreeCount);
            for (int i = 0; i < targetFreeCount; i++)
                objects.Add(getMethod.Invoke(null, new object[] { poolableType }));
            foreach (object obj in objects)
                freeMethod.Invoke(null, new[] { obj });
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"[Preload] Pool warmup skipped for {poolableType.Name}: {exception.Message}");
        }
    }

    private static bool ShouldOverride()
    {
        return OS.GetName().Equals("Android", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task WaitForFrameAsync()
    {
        var tree = Engine.GetMainLoop() as SceneTree;
        if (tree == null)
        {
            await Task.Yield();
            return;
        }
        await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
    }

    private static void AddAssets(HashSet<string> target, Func<IEnumerable<string>> provider)
    {
        try
        {
            var assets = provider?.Invoke();
            if (assets == null)
                return;
            foreach (string path in assets)
            {
                if (!string.IsNullOrWhiteSpace(path))
                    target.Add(path);
            }
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"[Preload] Android asset collection failed: {exception.Message}");
        }
    }

    private static IEnumerable<string> GetRunAssetPaths(IEnumerable<CharacterModel> characters, bool isMultiplayer)
    {
        var characterList = characters.ToList();
        IEnumerable<CardModel> cards = ModelDb.AllSharedCardPools.SelectMany(pool => pool.AllCards);
        if (!isMultiplayer)
            cards = cards.Where(card => card.MultiplayerConstraint != CardMultiplayerConstraint.MultiplayerOnly);
        return new IEnumerable<string>[]
        {
            cards.SelectMany(card => card.RunAssetPaths),
            characterList.SelectMany(character => character.CardPool.AllCards.SelectMany(card => card.RunAssetPaths)),
            characterList.SelectMany(character => character.AssetPaths),
            GetStaticAssetPaths("MegaCrit.Sts2.Core.Nodes.Cards.NCard"),
            GetStaticAssetPaths("MegaCrit.Sts2.Core.Nodes.Rooms.NMapRoom"),
            GetStaticAssetPaths("MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NChooseACardSelectionScreen"),
            GetStaticAssetPaths("MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen.NGameOverScreen"),
            GetStaticAssetPaths("MegaCrit.Sts2.Core.Nodes.Relics.NRelicInventoryHolder"),
        }.SelectMany(paths => paths);
    }

    private static IEnumerable<string> GetCombatAssetPaths(object encounter, object runState)
    {
        return GetStaticAssetPaths("MegaCrit.Sts2.Core.Nodes.Rooms.NCombatRoom").Concat(InvokeEnumerable(encounter, "GetAssetPaths", runState));
    }

    private static IEnumerable<string> GetStaticAssetPaths(string typeName)
    {
        try
        {
            var type = typeof(NGame).Assembly.GetType(typeName);
            var property = type?.GetProperty("AssetPaths", BindingFlags.Public | BindingFlags.Static);
            if (property?.GetValue(null) is IEnumerable<string> paths)
                return paths;
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"[Preload] AssetPaths failed for {typeName}: {exception.Message}");
        }
        return Array.Empty<string>();
    }

    private static IEnumerable<string> GetAssetPathsProperty(object instance)
    {
        try
        {
            if (instance?.GetType().GetProperty("AssetPaths", BindingFlags.Public | BindingFlags.Instance)?.GetValue(instance) is IEnumerable<string> paths)
                return paths;
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"[Preload] instance AssetPaths failed for {instance?.GetType().FullName}: {exception.Message}");
        }
        return Array.Empty<string>();
    }

    private static IEnumerable<string> InvokeEnumerable(object instance, string methodName, params object[] args)
    {
        try
        {
            var method = instance?.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);
            if (method?.Invoke(instance, args) is IEnumerable<string> paths)
                return paths;
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"[Preload] {methodName} failed for {instance?.GetType().FullName}: {exception.Message}");
        }
        return Array.Empty<string>();
    }

    private static string GetStringProperty(object instance, string propertyName)
    {
        try
        {
            return instance?.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)?.GetValue(instance) as string;
        }
        catch
        {
            return null;
        }
    }
}
