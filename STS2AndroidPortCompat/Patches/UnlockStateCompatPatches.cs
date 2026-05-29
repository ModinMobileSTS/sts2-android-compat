using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Unlocks;

namespace STS2Mobile.Patches;

/// <summary>
/// Guards vanilla UnlockState static initialization against modded ModelDb.Acts
/// patches running before ModelDb.Init has populated every vanilla act id.
///
/// BaseLib/RitsuLib append to ModelDb.Acts during mod initialization.  Some
/// optional RitsuLib patch resolution touches UnlockState before the normal
/// two-phase ModelDb init.  Vanilla UnlockState..cctor walks ModelDb.AllEncounters,
/// which walks patched ModelDb.Acts and can call ModelDb.Act&lt;Overgrowth&gt; while
/// ACT.OVERGROWTH is absent from _contentById.  A failing .cctor poisons the
/// type for the rest of the process.
///
/// Prefixing ModelDb.Acts is intentionally narrow: before ModelDb init completes
/// we return only act instances that are already present in _contentById, so
/// getters for not-yet-registered vanilla acts are not evaluated.  After init,
/// normal modded ModelDb.Acts behavior resumes.
/// </summary>
public static class UnlockStateCompatPatches
{
    private static bool _modelDbInitCompleted;
    private static bool _loggedEarlyActs;
    private static FieldInfo _contentByIdField;

    public static void Apply(Harmony harmony)
    {
        try
        {
            var actsGetter = AccessTools.PropertyGetter(typeof(ModelDb), nameof(ModelDb.Acts));
            var actsPrefix = AccessTools.Method(typeof(UnlockStateCompatPatches), nameof(ActsGetterPrefix));
            if (actsGetter == null || actsPrefix == null)
            {
                PatchHelper.Log("UnlockStateCompat: ModelDb.Acts getter not found");
                return;
            }
            harmony.Patch(actsGetter, prefix: new HarmonyMethod(actsPrefix) { priority = Priority.First });
            PatchHelper.Log("Patched ModelDb.Acts getter for early UnlockState safety");

            var cacheInit = AccessTools.Method(typeof(ModelIdSerializationCache), nameof(ModelIdSerializationCache.Init));
            var cachePostfix = AccessTools.Method(typeof(UnlockStateCompatPatches), nameof(ModelDbInitializedPostfix));
            if (cacheInit != null && cachePostfix != null)
            {
                harmony.Patch(cacheInit, postfix: new HarmonyMethod(cachePostfix) { priority = Priority.Last });
                PatchHelper.Log("Patched ModelIdSerializationCache.Init to mark ModelDb initialization complete");
            }
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"UnlockStateCompat patch failed: {exception}");
        }
    }

    public static void ModelDbInitializedPostfix()
    {
        _modelDbInitCompleted = true;
        PatchHelper.Log("UnlockStateCompat: ModelDb initialization marked complete; ModelDb.Acts guard disabled");
    }

    public static bool ActsGetterPrefix(ref IEnumerable<ActModel> __result)
    {
        if (_modelDbInitCompleted)
            return true;

        if (!IsUnlockStateBeingInitialized())
            return true;

        var safeActs = TryGetCurrentlyRegisteredActs();
        if (safeActs == null || safeActs.Count == 0)
            return true;

        __result = safeActs;
        if (!_loggedEarlyActs)
        {
            _loggedEarlyActs = true;
            PatchHelper.Log($"UnlockStateCompat: supplied {safeActs.Count} already-registered acts to early UnlockState initialization");
        }
        return false;
    }

    private static bool IsUnlockStateBeingInitialized()
    {
        try
        {
            var exception = new Exception();
            var trace = new System.Diagnostics.StackTrace(exception, false);
            for (var index = 0; index < trace.FrameCount; index++)
            {
                var method = trace.GetFrame(index)?.GetMethod();
                if (method?.DeclaringType == typeof(UnlockState) && method.Name == ".cctor")
                    return true;
            }
        }
        catch
        {
        }
        return false;
    }

    private static List<ActModel> TryGetCurrentlyRegisteredActs()
    {
        try
        {
            _contentByIdField ??= typeof(ModelDb).GetField("_contentById", BindingFlags.NonPublic | BindingFlags.Static);
            if (_contentByIdField?.GetValue(null) is not System.Collections.IDictionary dict)
                return null;

            var acts = new List<ActModel>();
            foreach (System.Collections.DictionaryEntry entry in dict)
            {
                if (entry.Value is ActModel act)
                    acts.Add(act);
            }
            return acts.OrderBy(act => act.Id.ToString(), StringComparer.Ordinal).ToList();
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"UnlockStateCompat: failed to enumerate registered acts: {exception.Message}");
            return null;
        }
    }
}
