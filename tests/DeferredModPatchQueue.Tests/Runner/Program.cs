using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using STS2Mobile.Patches;
using Fixture;
using Daily = MegaCrit.Sts2.Core.Nodes.Screens.DailyRun.NDailyRunScreen;
using Model = MegaCrit.Sts2.Core.Models.SyntheticModel;
using DependentModel = MegaCrit.Sts2.Core.Models.DependentModel;
using AssetSets = MegaCrit.Sts2.Core.Assets.AssetSets;
using PoolAssetCache = MegaCrit.Sts2.Core.Assets.PoolAssetCache;
using ModHelper = MegaCrit.Sts2.Core.Modding.ModHelper;
using ResourcePool = MegaCrit.Sts2.Core.Modding.ResourcePool;
using RegisteredResourceModel = MegaCrit.Sts2.Core.Modding.RegisteredResourceModel;
using LaterResourceModel = MegaCrit.Sts2.Core.Modding.LaterResourceModel;

internal static class Program
{
    private const string ModHarmonyId = "tests.issue33.patchall";

    private static int Main()
    {
        var guard = new Harmony("tests.issue33.guard");
        DeferredModPatchQueue.Apply(guard);
        var modHarmony = new Harmony(ModHarmonyId);
        ModHelper.AddModelToPool(typeof(ResourcePool), typeof(RegisteredResourceModel));

        using (DeferredModPatchQueue.BeginModInitialization("synthetic-issue33-mod"))
        {
            modHarmony.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocManager), "Initialize"),
                postfix: new HarmonyMethod(typeof(Program), nameof(AfterLocalization)));
        }

        using (DeferredModPatchQueue.BeginModInitialization("LocManager.Initialize hooks"))
        {
            MegaCrit.Sts2.Core.Localization.LocManager.Initialize();
            modHarmony.CreateProcessor(Method(nameof(Daily.Direct)))
                .AddPrefix(new HarmonyMethod(typeof(DirectPatch).GetMethod(nameof(DirectPatch.Prefix), BindingFlags.Static | BindingFlags.Public)))
                .Patch();
            modHarmony.CreateProcessor(Method(typeof(PoolAssetCache), nameof(PoolAssetCache.Direct)))
                .AddPrefix(new HarmonyMethod(typeof(ResourceDirectPatch), nameof(ResourceDirectPatch.Prefix)))
                .Patch();

            Assert(HasOwner(Method(typeof(Model), nameof(Model.Register))), "safe model PatchAll target must patch immediately");
            Assert(!HasOwner(Method(typeof(DependentModel), nameof(DependentModel.Register))), "model cctor reading ModelDb must remain queued before essential init");
            Assert(!HasOwner(Method(nameof(Daily.SetupLobbyParams))), "unsafe mixed PatchAll target must remain queued");
            Assert(!HasOwner(Method(nameof(Daily.AllKinds))), "unsafe all-kinds PatchAll target must remain queued");
            Assert(!HasOwner(Method(nameof(Daily.Prepared))), "unsafe prepared PatchAll target must remain queued");
            Assert(!HasOwner(Method(nameof(Daily.Skipped))), "unsafe prepare-false PatchAll target must remain queued");
            Assert(!HasOwner(Method(nameof(Daily.Failing))), "unsafe failing PatchAll target must remain queued");
            Assert(!HasOwner(Method(nameof(Daily.Direct))), "unsafe direct processor target must remain queued");
            Assert(!Probe.UiTypeInitialized, "PatchAll queue inspection must not run NDailyRunScreen.cctor");
            Assert(!Probe.DependentModelInitialized, "dependent model cctor must not run before ModelDb.Init");
            Assert(!Probe.AssetSetsInitialized, "resource cctor must not read canonical models before essential init");
            Assert(!Probe.PoolAssetCacheInitialized, "resource cctor must not consume/freeze pools before later MOD registration");
            Assert(MegaCrit.Sts2.Core.Modding.RegistrationStatics.Register() == "ResourcePool:patched",
                "ID-only static initialization and its safe patch must remain immediate");
            Assert(Model.Register(1) == 11, "safe model patch must be usable before deferred flush");
            Assert(PrepareCleanupPatch.MainPrepareCount == 1, "class-level HarmonyPrepare must run once during PatchAll");
            Assert(PrepareCleanupPatch.MainCleanupCount == 1, "class-level HarmonyCleanup must run once during PatchAll");
            Assert(PrepareCleanupPatch.IndividualPrepareCount == 0, "deferred PatchAll job prepare must not run before replay");
            Assert(PrepareCleanupPatch.IndividualCleanupCount == 0, "deferred PatchAll job cleanup must not run before replay");
            Assert(PrepareFalsePatch.IndividualPrepareCount == 0, "prepare-false job must remain queued before replay");
            Assert(Probe.TargetFactoryReads == 0, "target factory must not read ModelDb before initialization");
            Assert(ModelFactoryPatch.PrepareCount == 0 && ModelFactoryPatch.CleanupCount == 0,
                "whole-class deferral must happen before class Prepare/Cleanup");
        }
        ModHelper.AddModelToPool(typeof(ResourcePool), typeof(LaterResourceModel));

        Probe.EssentialReady = true;
        DeferredModPatchQueue.FlushDeferredPatches("synthetic essential initialization");

        Assert(HasOwner(Method(nameof(Daily.SetupLobbyParams))), "mixed PatchAll UI job was not replayed");
        Assert(HasOwner(Method(nameof(Daily.AllKinds))), "all-kinds PatchAll UI job was not replayed");
        Assert(HasOwner(Method(nameof(Daily.Prepared))), "prepared PatchAll UI job was not replayed");
        Assert(!HasOwner(Method(nameof(Daily.Skipped))), "HarmonyPrepare=false job must not install a patch");
        Assert(!HasOwner(Method(nameof(Daily.Failing))), "failed job must not publish partial patch metadata");
        Assert(HasOwner(Method(nameof(Daily.Direct))), "later direct PatchProcessor job was not replayed after a failed PatchAll job");
        Assert(HasOwner(Method(typeof(DependentModel), nameof(DependentModel.Register))), "dependent model patch was not replayed");
        Assert(STS2Mobile.PatchHelper.Messages.Any(message =>
                message.Contains("failed to replay deferred patch", StringComparison.Ordinal)
                && message.Contains(".Failing", StringComparison.Ordinal)),
            "synthetic failing job must be isolated and reported");
        Assert(OwnerCount(Method(typeof(Model), nameof(Model.Register)), HarmonyPatchType.Prefix) == 1,
            "safe target must not be applied a second time during replay");

        var allKinds = Harmony.GetPatchInfo(Method(nameof(Daily.AllKinds)));
        Assert(OwnerCount(allKinds.Prefixes) == 1, "PatchAll prefix metadata was not preserved");
        var allKindsPrefix = allKinds.Prefixes.Single(patch => patch.owner == ModHarmonyId);
        Assert(allKindsPrefix.priority == Priority.High, "PatchAll prefix priority was not preserved");
        Assert(allKindsPrefix.before.Contains("tests.issue33.after"), "PatchAll prefix before-order metadata was not preserved");
        Assert(OwnerCount(allKinds.Postfixes) == 1, "PatchAll postfix metadata was not preserved");
        Assert(OwnerCount(allKinds.Transpilers) == 1, "PatchAll transpiler metadata was not preserved");
        Assert(OwnerCount(allKinds.Finalizers) == 1, "PatchAll finalizer metadata was not preserved");
        Assert(PrepareCleanupPatch.MainPrepareCount == 1, "class-level HarmonyPrepare must not rerun during replay");
        Assert(PrepareCleanupPatch.MainCleanupCount == 1, "class-level HarmonyCleanup must not rerun during replay");
        Assert(PrepareCleanupPatch.IndividualPrepareCount == 1, "per-target HarmonyPrepare must run exactly once for replayed job");
        Assert(PrepareCleanupPatch.IndividualCleanupCount == 1, "per-target HarmonyCleanup must run exactly once for replayed job");
        Assert(PrepareFalsePatch.IndividualPrepareCount == 1, "HarmonyPrepare=false must be evaluated exactly once at replay");
        Assert(PrepareFalsePatch.IndividualCleanupCount == 1, "HarmonyCleanup must still run once when HarmonyPrepare returns false");

        Assert(Daily.SetupLobbyParams(1) == 11, "mixed PatchAll prefix did not execute after replay");
        Assert(Daily.AllKinds(1) == 4, "prefix/postfix PatchAll behavior was not preserved");
        Assert(Daily.Prepared(1) == 21, "prepared PatchAll prefix did not execute after replay");
        Assert(Daily.Direct(1) == 31, "direct PatchProcessor prefix did not execute after replay");
        var dependentResult = DependentModel.Register(1);
        Assert(dependentResult == 41, $"dependent model patch did not run after essential initialization: {dependentResult}");
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(DependentModel).TypeHandle);
        Assert(Probe.DependentModelInitialized, "dependent model cctor did not finish after ModelDb became ready");
        Assert(MegaCrit.Sts2.Core.Models.OtherModel.FromFactory(2) == 52, "model factory patch was lost");
        Assert(MegaCrit.Sts2.Core.Models.OtherModel.FromIterator(2) == 62, "iterator factory patch was lost");
        Assert(Probe.TargetFactoryReads == 2, "both factories must enumerate exactly once after initialization");
        Assert(ModelFactoryPatch.PrepareCount == 1 && ModelFactoryPatch.CleanupCount == 1,
            "deferred class lifecycle must run exactly once");
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(AssetSets).TypeHandle);
        Assert(AssetSets.CommonAssets.SequenceEqual(new[] { "OtherModel", "asset_patch" }),
            "resource initialization must read ready models and apply its deferred patch");
        Assert(PoolAssetCache.GetPaths().SequenceEqual(new[] { "RegisteredResourceModel", "LaterResourceModel", "pool_patch" }),
            "resource pool must include both earlier and later MOD registrations before it freezes");
        Assert(Probe.AssetSetsInitialized && Probe.PoolAssetCacheInitialized, "both resource constructors must finish after readiness");
        Assert(PoolAssetCache.Direct(1) == 71, "direct resource patch must execute after deferred replay");
        try
        {
            ModHelper.AddModelToPool(typeof(ResourcePool), typeof(Model));
            throw new Exception("consumed model pool accepted a genuinely late registration");
        }
        catch (InvalidOperationException) { }

        var prepareBeforeSecondFlush = PrepareCleanupPatch.IndividualPrepareCount;
        var cleanupBeforeSecondFlush = PrepareCleanupPatch.IndividualCleanupCount;
        DeferredModPatchQueue.FlushDeferredPatches("synthetic duplicate flush");
        Assert(PrepareCleanupPatch.IndividualPrepareCount == prepareBeforeSecondFlush,
            "second flush must not replay PatchAll job again");
        Assert(PrepareCleanupPatch.IndividualCleanupCount == cleanupBeforeSecondFlush,
            "second flush must not rerun HarmonyCleanup");
        Assert(Probe.TargetFactoryReads == 2, "duplicate flush must not re-enumerate target factories");

        Console.WriteLine("DeferredModPatchQueue PatchAll regression test passed.");
        return 0;
    }

    private static void AfterLocalization() => new Harmony(ModHarmonyId).PatchAll(Assembly.GetExecutingAssembly());

    private static MethodInfo Method(string name) => Method(typeof(Daily), name);

    private static MethodInfo Method(Type type, string name) =>
        type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException(type.FullName, name);

    private static bool HasOwner(MethodBase original) =>
        Harmony.GetPatchInfo(original)?.Owners.Contains(ModHarmonyId) == true;

    private static int OwnerCount(MethodBase original, HarmonyPatchType patchType)
    {
        var info = Harmony.GetPatchInfo(original);
        return patchType switch
        {
            HarmonyPatchType.Prefix => OwnerCount(info?.Prefixes),
            HarmonyPatchType.Postfix => OwnerCount(info?.Postfixes),
            HarmonyPatchType.Transpiler => OwnerCount(info?.Transpilers),
            HarmonyPatchType.Finalizer => OwnerCount(info?.Finalizers),
            _ => throw new ArgumentOutOfRangeException(nameof(patchType))
        };
    }

    private static int OwnerCount(IEnumerable<Patch> patches) =>
        patches?.Count(patch => patch.owner == ModHarmonyId) ?? 0;

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}

[HarmonyPatch(typeof(DependentModel), nameof(DependentModel.Register))]
internal static class DependentModelPatch
{
    [HarmonyPrefix]
    private static void Prefix(ref int value) => value += 40;
}

[HarmonyPatch]
internal static class MixedTargetsPatch
{
    [HarmonyTargetMethods]
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return typeof(Daily).GetMethod(nameof(Daily.SetupLobbyParams));
        yield return typeof(Model).GetMethod(nameof(Model.Register));
    }

    [HarmonyPrefix]
    private static void Prefix(ref int value) => value += 10;
}

[HarmonyPatch(typeof(Daily), nameof(Daily.AllKinds))]
internal static class AllKindsPatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.High)]
    [HarmonyBefore("tests.issue33.after")]
    private static void Prefix(ref int value) => value += 1;

    [HarmonyPostfix]
    private static void Postfix(ref int __result) => __result += 2;

    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) => instructions;

    [HarmonyFinalizer]
    private static Exception Finalizer(Exception __exception) => __exception;
}

[HarmonyPatch(typeof(Daily), nameof(Daily.Prepared))]
internal static class PrepareCleanupPatch
{
    internal static int MainPrepareCount;
    internal static int MainCleanupCount;
    internal static int IndividualPrepareCount;
    internal static int IndividualCleanupCount;

    [HarmonyPrepare]
    private static bool Prepare(MethodBase original)
    {
        if (original == null)
            MainPrepareCount++;
        else
            IndividualPrepareCount++;
        return true;
    }

    [HarmonyCleanup]
    private static Exception Cleanup(MethodBase original, Exception exception)
    {
        if (original == null)
            MainCleanupCount++;
        else
            IndividualCleanupCount++;
        return exception;
    }

    [HarmonyPrefix]
    private static void Prefix(ref int value) => value += 20;
}

[HarmonyPatch(typeof(Daily), nameof(Daily.Skipped))]
internal static class PrepareFalsePatch
{
    internal static int IndividualPrepareCount;
    internal static int IndividualCleanupCount;

    [HarmonyPrepare]
    private static bool Prepare(MethodBase original)
    {
        if (original == null)
            return true;
        IndividualPrepareCount++;
        return false;
    }

    [HarmonyCleanup]
    private static Exception Cleanup(MethodBase original, Exception exception)
    {
        if (original != null)
            IndividualCleanupCount++;
        return exception;
    }

    [HarmonyPrefix]
    private static void Prefix(ref int value) => value += 100;
}

[HarmonyPatch(typeof(Daily), nameof(Daily.Failing))]
internal static class FailingPatch
{
    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
        throw new InvalidOperationException("synthetic replay failure");
}

internal static class DirectPatch
{
    public static void Prefix(ref int value) => value += 30;
}

[HarmonyPatch]
internal static class ModelFactoryPatch
{
    internal static int PrepareCount;
    internal static int CleanupCount;
    [HarmonyPrepare]
    private static bool Prepare(MethodBase original)
    {
        if (original == null) PrepareCount++;
        return true;
    }
    [HarmonyCleanup]
    private static void Cleanup(MethodBase original)
    {
        if (original == null) CleanupCount++;
    }
    [HarmonyTargetMethods]
    private static IEnumerable<MethodBase> Discover() => FindModels();
    private static IEnumerable<MethodBase> FindModels() => MegaCrit.Sts2.Core.Models.ModelDb.AllCards
        .Select(model => (MethodBase)model.GetType().GetMethod("FromFactory"));
    [HarmonyPrefix]
    private static void Prefix(ref int value) => value += 50;
}

[HarmonyPatch]
internal static class IteratorModelFactoryPatch
{
    // Name-based discovery is supported by the real Harmony processor.
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var model in MegaCrit.Sts2.Core.Models.ModelDb.AllCards)
            yield return model.GetType().GetMethod("FromIterator");
    }
    [HarmonyPrefix]
    private static void Prefix(ref int value) => value += 60;
}

[HarmonyPatch(typeof(AssetSets), "get_CommonAssets")]
internal static class ResourceAssetPatch
{
    // Force the eager-cctor behavior observed on Android/Mono at the
    // per-target prepare boundary; CoreCLR normally leaves this cctor lazy.
    [HarmonyPrepare]
    private static bool Prepare(MethodBase original)
    {
        if (original != null)
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(original.DeclaringType.TypeHandle);
        return true;
    }
    [HarmonyPostfix]
    private static void Postfix(ref IReadOnlyList<string> __result) => __result = __result.Concat(new[] { "asset_patch" }).ToArray();
}

[HarmonyPatch(typeof(PoolAssetCache), nameof(PoolAssetCache.GetPaths))]
internal static class ResourcePoolPatch
{
    [HarmonyPrepare]
    private static bool Prepare(MethodBase original)
    {
        if (original != null)
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(original.DeclaringType.TypeHandle);
        return true;
    }
    [HarmonyPostfix]
    private static void Postfix(ref IReadOnlyList<string> __result) => __result = __result.Concat(new[] { "pool_patch" }).ToArray();
}

[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Modding.RegistrationStatics), nameof(MegaCrit.Sts2.Core.Modding.RegistrationStatics.Register))]
internal static class RegistrationSafePatch
{
    [HarmonyPostfix]
    private static void Postfix(ref string __result) => __result += ":patched";
}

internal static class ResourceDirectPatch
{
    public static void Prefix(ref int value) => value += 70;
}
