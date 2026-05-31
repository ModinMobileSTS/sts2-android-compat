using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace STS2Mobile.Patches;

/// <summary>
/// Mobile compatibility shims for RitsuLib.
///
/// RitsuLib 0.3.x registers an optional patch for RunState.CreateForTest during
/// mod initialization.  On the Android/Godot host, resolving/patching that test
/// helper can force UnlockState's static constructor before ModelDb.Init has run.
/// Once UnlockState observes BaseLib/RitsuLib's patched ModelDb.Acts while
/// vanilla act ids such as ACT.OVERGROWTH are not registered yet, the CLR marks
/// UnlockState as permanently failed for the process.  The user then reaches the
/// main menu, but starting/loading a run fails with the same cached
/// TypeInitializationException.
///
/// The CreateForTest hook is optional and not used by normal gameplay, so make
/// RitsuLib's patch resolver treat that one target as missing.  Other runtime
/// identity hooks (CreateForNewRun / FromSerializable / inventory updates) still
/// apply normally.
/// </summary>
public static class RitsuLibCompatPatches
{
    private static Harmony _harmony;
    private static bool _resolverPatched;
    private static bool _loggedSkip;

    public static void Apply(Harmony harmony)
    {
        _harmony = harmony;
        AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
        PatchHelper.Log("RitsuLibCompatPatches: registered AssemblyLoad listener for STS2-RitsuLib");

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (IsRitsuLibAssembly(assembly))
                TryPatchRitsuLib(assembly);
        }
    }

    private static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
    {
        if (_resolverPatched)
            return;

        if (IsRitsuLibAssembly(args.LoadedAssembly))
            TryPatchRitsuLib(args.LoadedAssembly);
    }

    private static bool IsRitsuLibAssembly(Assembly assembly)
    {
        var name = assembly.GetName().Name;
        return string.Equals(name, "STS2-RitsuLib", StringComparison.Ordinal)
            || string.Equals(name, "RitsuLib", StringComparison.Ordinal);
    }

    private static void TryPatchRitsuLib(Assembly ritsuLibAssembly)
    {
        if (_resolverPatched)
            return;

        try
        {
            var resolverType = ritsuLibAssembly.GetType("STS2RitsuLib.Patching.Core.PatchTargetMethodResolver");
            if (resolverType == null)
            {
                PatchHelper.Log("RitsuLibCompat: PatchTargetMethodResolver type not found in STS2-RitsuLib assembly");
                return;
            }

            var resolveMethod = resolverType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(method => method.Name == "Resolve"
                    && method.GetParameters().Length == 4
                    && method.GetParameters()[0].ParameterType == typeof(Type)
                    && method.GetParameters()[1].ParameterType == typeof(string));
            if (resolveMethod == null)
            {
                PatchHelper.Log("RitsuLibCompat: PatchTargetMethodResolver.Resolve(Type,string,...) method not found");
                return;
            }

            var prefix = AccessTools.Method(typeof(RitsuLibCompatPatches), nameof(ResolvePrefix));
            _harmony.Patch(resolveMethod, prefix: new HarmonyMethod(prefix));
            _resolverPatched = true;
            PatchHelper.Log("Patched RitsuLib PatchTargetMethodResolver.Resolve (skip optional RunState.CreateForTest pre-ModelDb hook on mobile)");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"RitsuLibCompat: failed to patch resolver on load: {exception}");
        }
    }

    public static bool ResolvePrefix(Type targetType, string methodName, ref MethodBase __result)
    {
        if (targetType?.FullName == "MegaCrit.Sts2.Core.Runs.RunState"
            && methodName == "CreateForTest")
        {
            __result = null;
            if (!_loggedSkip)
            {
                _loggedSkip = true;
                Console.WriteLine("[RitsuLibCompat] Treating optional RunState.CreateForTest target as missing to avoid early UnlockState initialization on mobile");
            }
            return false;
        }

        return true;
    }
}
