using System;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;

namespace STS2Mobile.Patches;

/// <summary>
/// Keeps the original STS2 cache selection and eviction behavior, but prevents
/// cache eviction from explicitly disposing a Godot Resource that may still be
/// referenced by pooled nodes, long-lived UI fields, or asynchronous work.
/// </summary>
public static class AndroidAssetCacheLifecyclePatches
{
    public static void Apply(Harmony harmony)
    {
        MethodInfo removeAndGetResource = AccessTools.Method(
            typeof(AssetCache),
            "RemoveAndGetResource",
            new[] { typeof(string) });
        if (removeAndGetResource == null || removeAndGetResource.ReturnType != typeof(Resource))
            throw new MissingMethodException(typeof(AssetCache).FullName, "RemoveAndGetResource(string)");

        harmony.Patch(
            removeAndGetResource,
            postfix: new HarmonyMethod(PatchHelper.Method(
                typeof(AndroidAssetCacheLifecyclePatches),
                nameof(RemoveAndGetResourcePostfix))));

        PatchHelper.Log("Android AssetCache disposal guard enabled; preload and cache-retention coverage remain unchanged.");
    }

    public static void RemoveAndGetResourcePostfix(ref Resource __result)
    {
        if (!IsAndroid())
            return;

        // RemoveAndGetResource has already removed the path from the original
        // cache. Both of its callers only use the returned value to Dispose it,
        // so returning null preserves eviction while leaving Resource lifetime
        // to Godot RefCounted ownership and managed references.
        __result = null;
    }

    private static bool IsAndroid()
    {
        try
        {
            return OS.GetName().Equals("Android", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
