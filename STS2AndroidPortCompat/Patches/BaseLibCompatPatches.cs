using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;

namespace STS2Mobile.Patches;

// Mobile-compat shim for BaseLib v3.x.
//
// BaseLib's BaseLib.Utils.Patching.AsyncMethodCall.Create transpiler injects new
// yield states into compiler-emitted async state-machine MoveNext methods. On
// Android (Godot Mono + MonoMod/Cecil-based emit) this can crash the native
// renderer with a Godot StringName double-unref, e.g.
// "BUG: Unreferenced static string to 0: _initialize".  The crash happens after
// BaseLib starts patching StateMachineType methods, before the game reaches the
// menu.
//
// This compatibility shim prefixes AsyncMethodCall.Create and returns the
// original IL unchanged.  Effect:
//   - BaseLib loads (DLL + PCK init succeeds)
//   - Node factories, config UI, CustomPile patch, content patches: should work
//   - BaseLib async hooks: disabled/no-op
//   - Mods that depend on BaseLib async hook callbacks may load but those hooks
//     will not fire
//
// This is a degraded-mode workaround copied from the working mobile launcher;
// it is intentionally narrower than disabling BaseLib entirely.
public static class BaseLibCompatPatches
{
    private static Harmony _harmony;
    private static bool _asyncMethodCallPatched;
    private static bool _extendedSavePatchesPatched;

    public static void Apply(Harmony harmony)
    {
        _harmony = harmony;
        AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
        PatchHelper.Log("BaseLibCompatPatches: registered AssemblyLoad listener for BaseLib");
    }

    private static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
    {
        if (_asyncMethodCallPatched && _extendedSavePatchesPatched) return;
        var asmName = args.LoadedAssembly.GetName().Name;
        if (asmName != "BaseLib") return;
        TryPatchBaseLib(args.LoadedAssembly);
    }

    private static void TryPatchBaseLib(System.Reflection.Assembly baseLibAssembly)
    {
        TryPatchAsyncMethodCall(baseLibAssembly);
        TryPatchExtendedSavePatches(baseLibAssembly);
    }

    private static void TryPatchAsyncMethodCall(System.Reflection.Assembly baseLibAssembly)
    {
        if (_asyncMethodCallPatched)
            return;
        try
        {
            var asyncMethodCallType = baseLibAssembly.GetType("BaseLib.Utils.Patching.AsyncMethodCall");
            if (asyncMethodCallType == null)
            {
                PatchHelper.Log("BaseLibCompat: AsyncMethodCall type not found in BaseLib assembly");
                return;
            }

            var createMethod = AccessTools.Method(asyncMethodCallType, "Create");
            if (createMethod == null)
            {
                PatchHelper.Log("BaseLibCompat: AsyncMethodCall.Create method not found");
                return;
            }

            var prefix = AccessTools.Method(typeof(BaseLibCompatPatches), nameof(AsyncMethodCallCreatePrefix));
            _harmony.Patch(createMethod, prefix: new HarmonyMethod(prefix));
            _asyncMethodCallPatched = true;
            PatchHelper.Log("Patched BaseLib.Utils.Patching.AsyncMethodCall.Create (state-machine hooks disabled for mobile compat)");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"BaseLibCompat: failed to patch AsyncMethodCall on load: {exception.Message}");
        }
    }

    private static void TryPatchExtendedSavePatches(System.Reflection.Assembly baseLibAssembly)
    {
        if (_extendedSavePatchesPatched)
            return;
        try
        {
            var extendedSavePatchesType = baseLibAssembly.GetType("BaseLib.Patches.Saves.ExtendedSavePatches");
            if (extendedSavePatchesType == null)
            {
                PatchHelper.Log("BaseLibCompat: ExtendedSavePatches type not found in BaseLib assembly");
                return;
            }

            var patchMethod = AccessTools.Method(extendedSavePatchesType, "Patch", new[] { typeof(Harmony) })
                ?? extendedSavePatchesType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                    .FirstOrDefault(method => method.Name == "Patch" && method.GetParameters().Length == 1 && method.GetParameters()[0].ParameterType.FullName == typeof(Harmony).FullName);
            if (patchMethod == null)
            {
                PatchHelper.Log("BaseLibCompat: ExtendedSavePatches.Patch(Harmony) method not found");
                return;
            }

            var prefix = AccessTools.Method(typeof(BaseLibCompatPatches), nameof(ExtendedSavePatchesPatchPrefix));
            _harmony.Patch(patchMethod, prefix: new HarmonyMethod(prefix));
            _extendedSavePatchesPatched = true;
            PatchHelper.Log("Patched BaseLib.Patches.Saves.ExtendedSavePatches.Patch (extended save context disabled for mobile compat)");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"BaseLibCompat: failed to patch ExtendedSavePatches on load: {exception.Message}");
        }
    }

    public static bool AsyncMethodCallCreatePrefix(IEnumerable<CodeInstruction> code, ref List<CodeInstruction> __result)
    {
        Console.WriteLine("[BaseLibCompat] Skipping AsyncMethodCall.Create (mobile workaround) — async hook will not fire");
        __result = code.ToList();
        return false;
    }

    public static bool ExtendedSavePatchesPatchPrefix()
    {
        Console.WriteLine("[BaseLibCompat] Skipping ExtendedSavePatches.Patch (mobile workaround) — BaseLib extended save context support disabled");
        return false;
    }
}
