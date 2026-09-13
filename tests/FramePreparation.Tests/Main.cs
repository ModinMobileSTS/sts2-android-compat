using System;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;
using STS2Mobile.Android;
using STS2Mobile.Patches;

public partial class Main : Node
{
    public override void _Ready() => Callable.From(() => { _ = RunAsync(); }).CallDeferred();

    private async Task RunAsync()
    {
        try
        {
            CheckOriginalContracts();
            var harmony = new Harmony("sts2.frame-preparation.native-tests");
            // Exercise mobile gating without replacing any rendering/resource API.
            harmony.Patch(AccessTools.Method(typeof(OS), nameof(OS.GetName)), prefix: new HarmonyMethod(typeof(Main), nameof(MobilePlatform)));
            ShaderCompatibilityPatches.Apply(harmony);
            CombatVfxPoolPatches.Apply(harmony);
            RuntimeAssetLoadingPatches.Apply(harmony);
            var game = new NGame();
            AddChild(game);
            await Frame();
            await CheckShaderLifecycle(game);
            await CheckVfxReuse();
            await CheckRuntimeBudgets();
            await CheckFontScaling();
            GD.Print("PASS: native Godot shader lifecycle/isolation, VFX reuse, runtime resource budgets/failure recovery and font restoration.");
            GetTree().Quit();
        }
        catch (Exception exception)
        {
            GD.PrintErr(exception);
            GetTree().Quit(1);
        }
    }

    private static bool MobilePlatform(ref string __result) { __result = "Android"; return false; }
    private async Task Frame() => await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static ShaderMaterial Original(string path = "res://shaders/dark_blur.gdshader")
    {
        var shader = new Shader { Code = "shader_type canvas_item; uniform float weight = 1.0; void fragment() { COLOR *= weight; }" };
        shader.TakeOverPath(path);
        var material = new ShaderMaterial { Shader = shader };
        material.SetShaderParameter("weight", 0.75f);
        return material;
    }
    private static bool Replaced(CanvasItem item) => item.Material is ShaderMaterial material
        && material.Shader.ResourcePath == "res://shaders/mobile_compat/dark_blur_compat.gdshader";

    private async Task CheckShaderLifecycle(NGame game)
    {
        var shared = Original();
        var parent = new MaterialParent { SharedMaterial = shared };
        game.AddChild(parent);
        await Frame();
        Require(Replaced(parent.First) && Replaced(parent.Second), "Materials assigned by parent _Ready must be replaced.");
        Require(ReferenceEquals(shared.Shader, parent.SharedMaterial.Shader)
            && shared.Shader.ResourcePath == "res://shaders/dark_blur.gdshader", "Do not mutate the original shared material.");
        ((ShaderMaterial)parent.First.Material).SetShaderParameter("weight", 0.25f);
        Require(Math.Abs(((ShaderMaterial)parent.Second.Material).GetShaderParameter("weight").AsSingle() - 0.75f) < 0.001f,
            "Replacement materials must retain per-node parameter isolation.");

        parent.RemoveChild(parent.First);
        parent.First.Material = shared;
        parent.AddChild(parent.First);
        await Frame();
        Require(Replaced(parent.First), "Re-entered ready nodes must be processed without another Ready signal.");

        var removed = new ColorRect { Material = shared };
        parent.AddChild(removed);
        parent.RemoveChild(removed);
        var freed = new ColorRect { Material = shared };
        parent.AddChild(freed);
        freed.Free();
        await Frame();
        Require(ReferenceEquals(removed.Material, shared), "Detached nodes must not be mutated by pending work.");
        removed.Free();

        var excluded = new ColorRect { Material = Original("res://shaders/blur/canvas_group_mask_blur.gdshader") };
        parent.AddChild(excluded);
        await Frame();
        Require(((ShaderMaterial)excluded.Material).Shader.ResourcePath.EndsWith("canvas_group_mask_blur.gdshader"), "Card-mask shader must remain excluded.");
        AndroidSettingsBridge.Enabled = false;
        ShaderCompatibilityPatches.RefreshSettings();
        var disabled = new ColorRect { Material = shared };
        parent.AddChild(disabled);
        await Frame();
        Require(ReferenceEquals(disabled.Material, shared), "Disabled compatibility must preserve the original material.");
        AndroidSettingsBridge.Enabled = true;
        ShaderCompatibilityPatches.RefreshSettings();
        await Frame();
        Require(Replaced(disabled), "Enabling compatibility must cover nodes created while disabled.");

    }

}

public partial class MaterialParent : Node
{
    public ShaderMaterial SharedMaterial;
    public ColorRect First = new();
    public ColorRect Second = new();
    public override void _EnterTree() { AddChild(First); AddChild(Second); }
    public override void _Ready() { First.Material = SharedMaterial; Second.Material = SharedMaterial; }
}
