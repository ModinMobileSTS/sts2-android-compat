using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Timeline;
using MegaCrit.Sts2.Core.Unlocks;

namespace STS2Mobile.Patches;

/// <summary>
/// Guards early UnlockState static initialization on Android/Mono.
///
/// Harmony-patching a member of UnlockState (for example HextechRunes patching
/// UnlockState.Relics during its mod initializer) can eagerly run
/// UnlockState..cctor on Android/Mono. PC/CoreCLR does not trigger the cctor this
/// early. Vanilla UnlockState..cctor walks ModelDb.AllEncounters; if content mods
/// have already patched acts/encounters but ModelDb.Init has not completed, that
/// walk can touch not-yet-registered MOD encounters such as
/// ENCOUNTER.YUWANCARD-KILLER_ELITE and permanently poison the UnlockState type.
///
/// Before ModelDb initialization is marked complete, AllEncounters returns an
/// empty list. This is intentionally broader than a stack-trace-only cctor guard:
/// the early Android/Mono stack is not always reliable once Harmony dynamic
/// methods are involved, and pre-init AllEncounters is unsafe anyway. After init,
/// normal modded ModelDb.AllEncounters / Acts behavior resumes.
/// </summary>
public static class UnlockStateCompatPatches
{
    private static bool _modelDbInitCompleted;
    private static bool _loggedEarlyEncounters;
    private static bool _loggedEarlyActs;
    private static Action<UnlockState> _setUnlockStateAll;

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

            var allEncountersGetter = AccessTools.PropertyGetter(typeof(ModelDb), nameof(ModelDb.AllEncounters));
            var allEncountersPrefix = AccessTools.Method(typeof(UnlockStateCompatPatches), nameof(AllEncountersGetterPrefix));
            if (allEncountersGetter != null && allEncountersPrefix != null)
            {
                harmony.Patch(allEncountersGetter, prefix: new HarmonyMethod(allEncountersPrefix) { priority = Priority.First });
                PatchHelper.Log("Patched ModelDb.AllEncounters getter for early UnlockState safety");
            }

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
        RepairUnlockStateAllIfInitializedEarly();
        PatchHelper.Log("UnlockStateCompat: ModelDb initialization marked complete; ModelDb.AllEncounters/Acts guard disabled");
    }

    public static bool AllEncountersGetterPrefix(ref IEnumerable<EncounterModel> __result)
    {
        if (_modelDbInitCompleted)
            return true;

        __result = Array.Empty<EncounterModel>();
        if (!_loggedEarlyEncounters)
        {
            _loggedEarlyEncounters = true;
            PatchHelper.Log("UnlockStateCompat: supplied empty encounter list before ModelDb initialization completed");
        }
        return false;
    }

    public static bool ActsGetterPrefix(ref IEnumerable<ActModel> __result)
    {
        if (_modelDbInitCompleted)
            return true;

        if (!IsUnlockStateBeingInitialized())
            return true;

        __result = Array.Empty<ActModel>();
        if (!_loggedEarlyActs)
        {
            _loggedEarlyActs = true;
            PatchHelper.Log("UnlockStateCompat: supplied empty act list to early UnlockState initialization");
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
                var declaringType = method?.DeclaringType;
                if (declaringType == typeof(UnlockState) || declaringType?.FullName == typeof(UnlockState).FullName)
                    return true;
            }
        }
        catch
        {
        }
        return false;
    }

    private static void RepairUnlockStateAllIfInitializedEarly()
    {
        try
        {
            var allField = typeof(UnlockState).GetField("all", BindingFlags.Public | BindingFlags.Static);
            if (allField == null)
                return;

            // If Android/Mono forced UnlockState..cctor to run before ModelDb init,
            // UnlockState.all was created from an intentionally-empty
            // AllEncounters list. Rebuild it now that ModelDb is complete so later
            // gameplay sees the normal PC-style "all encounters seen" state.
            var repaired = new UnlockState(EpochModel.AllEpochIds, ModelDb.AllEncounters.Select(e => e.Id), 999999999);
            SetUnlockStateAll(allField, repaired);
            PatchHelper.Log("UnlockStateCompat: repaired UnlockState.all after early static initialization");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"UnlockStateCompat: failed to repair UnlockState.all: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static void SetUnlockStateAll(FieldInfo allField, UnlockState value)
    {
        try
        {
            allField.SetValue(null, value);
            return;
        }
        catch (FieldAccessException)
        {
            // .NET 8/9 forbids FieldInfo.SetValue on static readonly fields after
            // type initialization. Harmony already relies on DynamicMethod on this
            // runtime, so use a tiny stsfld method as the readonly-field fallback.
        }

        _setUnlockStateAll ??= CreateUnlockStateAllSetter(allField);
        _setUnlockStateAll(value);
    }

    private static Action<UnlockState> CreateUnlockStateAllSetter(FieldInfo allField)
    {
        var method = new DynamicMethod(
            "STS2Mobile_SetUnlockStateAll",
            typeof(void),
            new[] { typeof(UnlockState) },
            typeof(UnlockStateCompatPatches).Module,
            skipVisibility: true);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Stsfld, allField);
        il.Emit(OpCodes.Ret);
        return (Action<UnlockState>)method.CreateDelegate(typeof(Action<UnlockState>));
    }
}
