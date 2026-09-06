using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;

namespace STS2Mobile.Patches;

internal static class AndroidParticlePreprocessPatches
{
    internal static void Apply(Harmony harmony)
    {
        var assembly = typeof(NGame).Assembly;
        PatchHelper.Patch(harmony, assembly.GetType("MegaCrit.Sts2.Core.Nodes.Rooms.NCombatRoom"),
            "SetUpBackground", postfix: PatchHelper.Method(typeof(AndroidParticlePreprocessPatches), nameof(BackgroundReadyPostfix)));
        PatchHelper.Patch(harmony, assembly.GetType("MegaCrit.Sts2.Core.Nodes.Events.NAncientEventLayout"),
            "InitializeVisuals", postfix: PatchHelper.Method(typeof(AndroidParticlePreprocessPatches), nameof(BackgroundReadyPostfix)));
    }

    public static void BackgroundReadyPostfix(Node __instance)
    {
        if (!OS.GetName().Equals("Android", StringComparison.OrdinalIgnoreCase))
            return;
        try
        {
            int changed = TrimBackground(__instance, false);
            if (changed > 0)
                PatchHelper.Log($"Android ambient particle preprocessing bounded: emitters={changed}; amount/material/lifetime unchanged.");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Android ambient particle preprocessing skipped: {exception.Message}");
        }
    }

    private static int TrimBackground(Node node, bool knownBackground)
    {
        if (node == null || !GodotObject.IsInstanceValid(node))
            return 0;
        knownBackground |= node.SceneFilePath == "res://scenes/backgrounds/waterfall_giant_boss/layers/waterfall_giant_boss_bg_00_a.tscn"
            || node.SceneFilePath == "res://scenes/events/background_scenes/orobas.tscn";
        int changed = 0;
        if (knownBackground && node is GpuParticles2D particles
            && !particles.OneShot && !particles.TrailEnabled && particles.SubEmitter.IsEmpty
            && particles.Explosiveness == 0f && particles.ProcessMaterial is ParticleProcessMaterial material
            && material.SubEmitterMode == ParticleProcessMaterial.SubEmitterModeEnum.Disabled)
        {
            double bounded = BoundPreprocess(particles.Preprocess, particles.Lifetime);
            if (bounded < particles.Preprocess)
            {
                particles.Preprocess = bounded;
                changed++;
            }
        }
        // Once per background setup, never per frame or global AddChild interception.
        for (int i = 0; i < node.GetChildCount(); i++)
            changed += TrimBackground(node.GetChild(i), knownBackground);
        return changed;
    }

    internal static double BoundPreprocess(double preprocess, double lifetime)
    {
        if (!double.IsFinite(preprocess) || !double.IsFinite(lifetime)
            || lifetime <= 0 || preprocess <= 2 * lifetime)
            return preprocess;
        // At least one complete emission cycle, retaining the original cycle phase.
        // Do not truncate long-lived particles to an arbitrary time limit.
        return lifetime + preprocess % lifetime;
    }
}
