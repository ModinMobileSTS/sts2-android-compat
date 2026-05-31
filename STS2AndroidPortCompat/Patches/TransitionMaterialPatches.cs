using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Nodes;

namespace STS2Mobile.Patches;

/// <summary>
/// The original PC transition code stores fade/fight transition ShaderMaterial
/// resources in PreloadManager.Cache and then assigns that cached instance to
/// NTransition.Material.  When preloading is disabled, the startup deferred
/// asset cleanup calls AssetCache.UnloadMissedCacheAssets shortly after the main
/// menu appears.  The shared cached material can be disposed while the main-menu
/// FadeIn task is still animating it, leaving Android on a black transition
/// screen with ObjectDisposedException: Godot.ShaderMaterial.
///
/// Keep transition materials node-local: duplicate the scene material on ready
/// and duplicate cached transition materials before callers receive them.  This
/// mirrors the legacy Android source-port behavior without replacing the full
/// NTransition methods, so it stays compatible with both supported game builds.
/// </summary>
public static class TransitionMaterialPatches
{
    private const string FadeTransitionPath = "res://materials/transitions/fade_transition_mat.tres";
    private const string FightTransitionPath = "res://materials/transitions/fight_transition_mat.tres";

    private static bool _loggedEnabled;
    private static bool _loggedDuplicateFailure;

    public static void Apply(Harmony harmony)
    {
        PatchHelper.Patch(harmony, typeof(NTransition), "_Ready", postfix: PatchHelper.Method(typeof(TransitionMaterialPatches), nameof(TransitionReadyPostfix)));
        PatchHelper.Patch(harmony, typeof(AssetCache), "GetMaterial", postfix: PatchHelper.Method(typeof(TransitionMaterialPatches), nameof(GetMaterialPostfix)));
        PatchHelper.Log("Android transition material safety patches enabled.");
    }

    public static void TransitionReadyPostfix(NTransition __instance)
    {
        try
        {
            if (__instance == null)
                return;
            if (__instance.Material is ShaderMaterial shaderMaterial && GodotObject.IsInstanceValid(shaderMaterial))
                __instance.Material = DuplicateMaterial(shaderMaterial) ?? shaderMaterial;
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Transition material ready safety failed: {exception.Message}");
        }
    }

    public static void GetMaterialPostfix(string path, ref Material __result)
    {
        try
        {
            if (!IsTransitionMaterial(path))
                return;
            if (__result is not ShaderMaterial shaderMaterial || !GodotObject.IsInstanceValid(shaderMaterial))
                return;
            var duplicate = DuplicateMaterial(shaderMaterial);
            if (duplicate != null)
            {
                __result = duplicate;
                if (!_loggedEnabled)
                {
                    _loggedEnabled = true;
                    PatchHelper.Log("Transition material cache result duplicated to avoid disposal during disabled-preload startup cleanup.");
                }
            }
        }
        catch (Exception exception)
        {
            if (!_loggedDuplicateFailure)
            {
                _loggedDuplicateFailure = true;
                PatchHelper.Log($"Transition material duplicate failed; using cached material: {exception.Message}");
            }
        }
    }

    private static bool IsTransitionMaterial(string path)
    {
        return string.Equals(path, FadeTransitionPath, StringComparison.Ordinal)
            || string.Equals(path, FightTransitionPath, StringComparison.Ordinal);
    }

    private static ShaderMaterial DuplicateMaterial(ShaderMaterial shaderMaterial)
    {
        try
        {
            return shaderMaterial.Duplicate() as ShaderMaterial;
        }
        catch
        {
            return null;
        }
    }
}
