using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Localization;

namespace STS2Mobile.Patches;

/// <summary>
/// Guards Android/Mono's eager type-initializer behavior while user MOD
/// initializers are still applying Harmony patches.
///
/// On PC, Harmony patching a game method usually does not run the declaring
/// type's static constructor at this point. On Android/Godot Mono it can. Some
/// vanilla node static constructors build HoverTip instances, and HoverTip
/// formats LocString values immediately. That is still before
/// OneTimeInitialization.ExecuteEssential -> LocManager.Initialize, so the
/// formatting path can throw and permanently poison the type initializer.
/// </summary>
public static class EarlyLocalizationFallbackPatches
{
    private static bool _locManagerInitialized;
    private static bool _loggedFallback;
    private static bool _loggedInitialized;

    public static void Apply(Harmony harmony)
    {
        PatchLocStringGetFormattedText(harmony);
        PatchLocManagerInitialize(harmony);
    }

    private static void PatchLocStringGetFormattedText(Harmony harmony)
    {
        try
        {
            var target = AccessTools.Method(typeof(LocString), nameof(LocString.GetFormattedText), Type.EmptyTypes);
            var finalizer = AccessTools.Method(typeof(EarlyLocalizationFallbackPatches), nameof(GetFormattedTextFinalizer));
            if (target == null || finalizer == null)
            {
                PatchHelper.Log("EarlyLocalizationFallback: LocString.GetFormattedText or finalizer not found");
                return;
            }

            harmony.Patch(target, finalizer: new HarmonyMethod(finalizer));
            PatchHelper.Log("Patched LocString.GetFormattedText for pre-LocManager Android cctor safety.");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"FAILED LocString.GetFormattedText early localization fallback: {exception}");
        }
    }

    private static void PatchLocManagerInitialize(Harmony harmony)
    {
        try
        {
            var target = AccessTools.Method(typeof(LocManager), nameof(LocManager.Initialize), Type.EmptyTypes);
            var postfix = AccessTools.Method(typeof(EarlyLocalizationFallbackPatches), nameof(InitializePostfix));
            if (target == null || postfix == null)
            {
                PatchHelper.Log("EarlyLocalizationFallback: LocManager.Initialize or postfix not found");
                return;
            }

            harmony.Patch(target, postfix: new HarmonyMethod(postfix) { priority = Priority.Last });
            PatchHelper.Log("Patched LocManager.Initialize to disable early localization fallback after normal init.");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"FAILED LocManager.Initialize early localization marker: {exception}");
        }
    }

    public static Exception GetFormattedTextFinalizer(LocString __instance, Exception __exception, ref string __result)
    {
        if (__exception == null)
            return null;

        if (_locManagerInitialized)
            return __exception;

        __result = BuildFallbackText(__instance);
        if (!_loggedFallback)
        {
            _loggedFallback = true;
            PatchHelper.Log(
                $"EarlyLocalizationFallback: suppressed pre-LocManager LocString.GetFormattedText failure ({__exception.GetType().Name}: {__exception.Message}); fallback={__result}");
        }

        return null;
    }

    public static void InitializePostfix()
    {
        _locManagerInitialized = true;
        if (!_loggedInitialized)
        {
            _loggedInitialized = true;
            PatchHelper.Log("EarlyLocalizationFallback: LocManager initialized; fallback disabled.");
        }
    }

    private static string BuildFallbackText(LocString locString)
    {
        if (locString == null)
            return string.Empty;

        try
        {
            var table = locString.LocTable;
            var key = locString.LocEntryKey;
            if (!string.IsNullOrWhiteSpace(table) && !string.IsNullOrWhiteSpace(key))
                return $"{table}.{key}";
            if (!string.IsNullOrWhiteSpace(key))
                return key;
            if (!string.IsNullOrWhiteSpace(table))
                return table;
        }
        catch
        {
            // Do not let diagnostic fallback construction rethrow into a type
            // initializer. Returning a stable placeholder is safer at this phase.
        }

        return string.Empty;
    }
}
