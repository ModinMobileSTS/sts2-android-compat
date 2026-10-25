using System;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using STS2Mobile.Android;
using STS2Mobile.Patches;

public partial class Main
{
    private const string EnergyCounterScene = "res://scenes/combat/energy_counters/ironclad_energy_counter.tscn";
    private const string EnergyFrontScene = "res://scenes/vfx/energy/ironclad/ironclad_energy_vfx_front.tscn";
    private const string EnergyEmbersScene = "res://scenes/vfx/fire_impact/vfx_fire_embers_straight.tscn";
    private const string EnergyEmbersNode = "EnergyVfxFront/vfx_fire_embers_straight";
    private static string _particleRenderingMethod = "gl_compatibility";
    private static string _particleAdapter = "HUAWEI Maleoon 920";

    private static bool ParticleRenderingMethod(ref string __result) { __result = _particleRenderingMethod; return false; }
    private static bool ParticleAdapter(ref string __result) { __result = _particleAdapter; return false; }

    private static PackedScene EnergyScene(ParticleProcessMaterial material, string counterPath = EnergyCounterScene,
        string frontPath = EnergyFrontScene, string embersPath = EnergyEmbersScene)
    {
        using var embers = Pack(new GpuParticles2D
        {
            Name = "vfx_fire_embers_straight", Emitting = false, OneShot = true,
            Amount = 6, Lifetime = 0.5, ProcessMaterial = material
        });
        embers.TakeOverPath(embersPath);
        var frontNode = new Node2D { Name = "EnergyVfxFront" };
        var particles = embers.Instantiate<GpuParticles2D>();
        frontNode.AddChild(particles);
        particles.Owner = frontNode;
        var sibling = new GpuParticles2D { Name = "OtherFire", Emitting = false, ProcessMaterial = material };
        frontNode.AddChild(sibling);
        sibling.Owner = frontNode;
        using var front = Pack(frontNode);
        front.TakeOverPath(frontPath);
        var counter = new NEnergyCounter { Name = "IroncladEnergyCounter" };
        var frontInstance = front.Instantiate<Node2D>();
        counter.AddChild(frontInstance);
        frontInstance.Owner = counter;
        var scene = Pack(counter);
        scene.TakeOverPath(counterPath);
        return scene;
    }

    private async Task CheckEnergyParticles(Harmony harmony, NGame game)
    {
        var methodGetter = AccessTools.Method(typeof(RenderingServer), nameof(RenderingServer.GetCurrentRenderingMethod));
        var adapterGetter = AccessTools.Method(typeof(RenderingServer), nameof(RenderingServer.GetVideoAdapterName));
        var methodPrefix = AccessTools.Method(typeof(Main), nameof(ParticleRenderingMethod));
        var adapterPrefix = AccessTools.Method(typeof(Main), nameof(ParticleAdapter));
        harmony.Patch(methodGetter, prefix: new HarmonyMethod(methodPrefix));
        harmony.Patch(adapterGetter, prefix: new HarmonyMethod(adapterPrefix));
        using var original = new ParticleProcessMaterial
        {
            TurbulenceEnabled = true, TurbulenceNoiseStrength = 20, TurbulenceNoiseScale = 10,
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Ring,
            EmissionRingRadius = 80, Gravity = new Vector3(0, -80, 0),
            Color = new Color(1, 0.3f, 0.1f), LifetimeRandomness = 0.5f
        };
        original.TakeOverPath(EnergyFrontScene + "::ParticleProcessMaterial_synthetic");
        try
        {
            foreach (var gate in new[]
            {
                ("Android", "gl_compatibility", "HUAWEI Maleoon 920", false),
                ("Android", "gl_compatibility", "Qualcomm Adreno 735", true),
                ("Android", "mobile", "HUAWEI Maleoon 920", true),
                ("Android", "forward_plus", "HUAWEI Maleoon 920", true),
                ("iOS", "gl_compatibility", "HUAWEI Maleoon 920", true),
                ("Linux", "gl_compatibility", "HUAWEI Maleoon 920", true)
            })
            {
                _testPlatform = gate.Item1;
                _particleRenderingMethod = gate.Item2;
                _particleAdapter = gate.Item3;
                AndroidSettingsBridge.Enabled = gate.Item4;
                using var scene = EnergyScene(original);
                var counter = NEnergyCounter.Create(scene);
                Require(ReferenceEquals(counter.GetNode<GpuParticles2D>(EnergyEmbersNode).ProcessMaterial, original),
                    $"Ineligible energy particles changed: platform={gate.Item1}, renderer={gate.Item2}, gpu={gate.Item3}, enabled={gate.Item4}");
                counter.Free();
            }
            _testPlatform = "Android";
            _particleRenderingMethod = "gl_compatibility";
            _particleAdapter = "HUAWEI Maleoon 920";
            AndroidSettingsBridge.Enabled = true;
            using var eligibleScene = EnergyScene(original);
            var first = NEnergyCounter.Create(eligibleScene);
            var second = NEnergyCounter.Create(eligibleScene);
            var firstParticles = first.GetNode<GpuParticles2D>(EnergyEmbersNode);
            var secondParticles = second.GetNode<GpuParticles2D>(EnergyEmbersNode);
            var firstMaterial = (ParticleProcessMaterial)firstParticles.ProcessMaterial;
            Require(!first.IsInsideTree() && !firstMaterial.TurbulenceEnabled,
                "Maleoon turbulence must be disabled before the energy counter first enters the tree.");
            Require(original.TurbulenceEnabled && !ReferenceEquals(original, firstMaterial)
                && !ReferenceEquals(firstMaterial, secondParticles.ProcessMaterial),
                "Energy counters must use independent material copies without mutating the shared template.");
            Require(ReferenceEquals(first.GetNode<GpuParticles2D>("EnergyVfxFront/OtherFire").ProcessMaterial, original),
                "Other emitters must retain their original shared material, including turbulence.");
            Require(firstMaterial.Gravity == original.Gravity && firstMaterial.Color == original.Color
                && firstMaterial.EmissionShape == original.EmissionShape && firstMaterial.EmissionRingRadius == original.EmissionRingRadius
                && firstMaterial.TurbulenceNoiseStrength == original.TurbulenceNoiseStrength
                && firstMaterial.LifetimeRandomness == original.LifetimeRandomness
                && firstParticles.Amount == 6 && firstParticles.Lifetime == 0.5 && firstParticles.OneShot && !firstParticles.Emitting,
                "The compatibility copy must retain particle motion, appearance, emission count and playback timing.");
            game.AddChild(first);
            firstParticles.Restart();
            for (int i = 0; i < 4; i++) await Frame();
            Require(firstParticles.Emitting && ReferenceEquals(firstMaterial, firstParticles.ProcessMaterial),
                "The original particle Restart must still play after the compatibility change.");
            ShaderCompatibilityPatches.RefreshSettings();
            Require(ReferenceEquals(firstMaterial, firstParticles.ProcessMaterial),
                "Repeated settings application must not replace an already compatible material again.");
            first.Free();
            second.Free();

            foreach (var paths in new[]
            {
                ("res://mods/custom_energy_counter.tscn", EnergyFrontScene, EnergyEmbersScene),
                (EnergyCounterScene, "res://mods/custom_energy_front.tscn", EnergyEmbersScene),
                (EnergyCounterScene, EnergyFrontScene, "res://mods/custom_embers.tscn")
            })
            {
                using var scene = EnergyScene(original, paths.Item1, paths.Item2, paths.Item3);
                var counter = NEnergyCounter.Create(scene);
                Require(ReferenceEquals(counter.GetNode<GpuParticles2D>(EnergyEmbersNode).ProcessMaterial, original),
                    "A custom counter, front effect or emitter must not receive the built-in particle replacement.");
                counter.Free();
            }
            using var customMaterial = (ParticleProcessMaterial)original.Duplicate();
            customMaterial.TakeOverPath("res://mods/custom_particle_material.tres");
            using var customScene = EnergyScene(customMaterial);
            var customCounter = NEnergyCounter.Create(customScene);
            Require(ReferenceEquals(customCounter.GetNode<GpuParticles2D>(EnergyEmbersNode).ProcessMaterial, customMaterial)
                && customMaterial.TurbulenceEnabled, "A MOD process material must stay untouched even inside the original scene.");
            customCounter.Free();

            AndroidSettingsBridge.Enabled = false;
            using var lateScene = EnergyScene(original);
            var late = NEnergyCounter.Create(lateScene);
            game.AddChild(late);
            AndroidSettingsBridge.Enabled = true;
            ShaderCompatibilityPatches.RefreshSettings();
            Require(!((ParticleProcessMaterial)late.GetNode<GpuParticles2D>(EnergyEmbersNode).ProcessMaterial).TurbulenceEnabled,
                "Enabling compatibility must cover an existing known energy counter.");
            late.Free();
            GD.Print("PASS: native energy particle pre-tree replacement, gating, material isolation, MOD exclusions, Restart and settings refresh.");
        }
        finally
        {
            _testPlatform = "Android";
            AndroidSettingsBridge.Enabled = true;
            harmony.Unpatch(methodGetter, methodPrefix);
            harmony.Unpatch(adapterGetter, adapterPrefix);
        }
    }
}
