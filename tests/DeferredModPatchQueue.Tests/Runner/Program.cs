using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using STS2Mobile.Patches;
using Fixture;
using Daily = MegaCrit.Sts2.Core.Nodes.Screens.DailyRun.NDailyRunScreen;
using Model = MegaCrit.Sts2.Core.Models.SyntheticModel;

internal static class Program
{
    private const string ModHarmonyId = "tests.issue33.patchall";

    private static int Main()
    {
        var guard = new Harmony("tests.issue33.guard");
        DeferredModPatchQueue.Apply(guard);
        var modHarmony = new Harmony(ModHarmonyId);

        using (DeferredModPatchQueue.BeginModInitialization("synthetic-issue33-mod"))
        {
            modHarmony.PatchAll(Assembly.GetExecutingAssembly());
            modHarmony.CreateProcessor(Method(nameof(Daily.Direct)))
                .AddPrefix(new HarmonyMethod(typeof(DirectPatch).GetMethod(nameof(DirectPatch.Prefix), BindingFlags.Static | BindingFlags.Public)))
                .Patch();

            Assert(HasOwner(Method(typeof(Model), nameof(Model.Register))), "safe model PatchAll target must patch immediately");
            Assert(!HasOwner(Method(nameof(Daily.SetupLobbyParams))), "unsafe mixed PatchAll target must remain queued");
            Assert(!HasOwner(Method(nameof(Daily.AllKinds))), "unsafe all-kinds PatchAll target must remain queued");
            Assert(!HasOwner(Method(nameof(Daily.Prepared))), "unsafe prepared PatchAll target must remain queued");
            Assert(!HasOwner(Method(nameof(Daily.Skipped))), "unsafe prepare-false PatchAll target must remain queued");
            Assert(!HasOwner(Method(nameof(Daily.Failing))), "unsafe failing PatchAll target must remain queued");
            Assert(!HasOwner(Method(nameof(Daily.Direct))), "unsafe direct processor target must remain queued");
            Assert(!Probe.UiTypeInitialized, "PatchAll queue inspection must not run NDailyRunScreen.cctor");
            Assert(Model.Register(1) == 11, "safe model patch must be usable before deferred flush");
            Assert(PrepareCleanupPatch.MainPrepareCount == 1, "class-level HarmonyPrepare must run once during PatchAll");
            Assert(PrepareCleanupPatch.MainCleanupCount == 1, "class-level HarmonyCleanup must run once during PatchAll");
            Assert(PrepareCleanupPatch.IndividualPrepareCount == 0, "deferred PatchAll job prepare must not run before replay");
            Assert(PrepareCleanupPatch.IndividualCleanupCount == 0, "deferred PatchAll job cleanup must not run before replay");
            Assert(PrepareFalsePatch.IndividualPrepareCount == 0, "prepare-false job must remain queued before replay");
        }

        Probe.EssentialReady = true;
        DeferredModPatchQueue.FlushDeferredPatches("synthetic essential initialization");

        Assert(HasOwner(Method(nameof(Daily.SetupLobbyParams))), "mixed PatchAll UI job was not replayed");
        Assert(HasOwner(Method(nameof(Daily.AllKinds))), "all-kinds PatchAll UI job was not replayed");
        Assert(HasOwner(Method(nameof(Daily.Prepared))), "prepared PatchAll UI job was not replayed");
        Assert(!HasOwner(Method(nameof(Daily.Skipped))), "HarmonyPrepare=false job must not install a patch");
        Assert(!HasOwner(Method(nameof(Daily.Failing))), "failed job must not publish partial patch metadata");
        Assert(HasOwner(Method(nameof(Daily.Direct))), "later direct PatchProcessor job was not replayed after a failed PatchAll job");
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

        var prepareBeforeSecondFlush = PrepareCleanupPatch.IndividualPrepareCount;
        var cleanupBeforeSecondFlush = PrepareCleanupPatch.IndividualCleanupCount;
        DeferredModPatchQueue.FlushDeferredPatches("synthetic duplicate flush");
        Assert(PrepareCleanupPatch.IndividualPrepareCount == prepareBeforeSecondFlush,
            "second flush must not replay PatchAll job again");
        Assert(PrepareCleanupPatch.IndividualCleanupCount == cleanupBeforeSecondFlush,
            "second flush must not rerun HarmonyCleanup");

        Console.WriteLine("DeferredModPatchQueue PatchAll regression test passed.");
        return 0;
    }

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
