using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;
using STS2Mobile.Android;
using STS2Mobile.Patches;

public partial class Main : Node
{
    private static readonly HashSet<string> ActiveRequests = new();
    private static int _maxRequests;
    private static int _retrieved;
    private static bool _retrievedInProgress;
    private static bool _wrongThread;

    public override void _Ready() => Callable.From(() => { _ = RunAsync(); }).CallDeferred();

    private async Task RunAsync()
    {
        try
        {
            var harmony = new Harmony("sts2.frame-preparation.native-tests");
            // Exercise mobile gating without replacing any rendering/resource API.
            harmony.Patch(AccessTools.Method(typeof(OS), nameof(OS.GetName)), prefix: new HarmonyMethod(typeof(Main), nameof(MobilePlatform)));
            harmony.Patch(AccessTools.Method(typeof(ResourceLoader), nameof(ResourceLoader.LoadThreadedRequest)), postfix: new HarmonyMethod(typeof(Main), nameof(Requested)));
            harmony.Patch(AccessTools.Method(typeof(ResourceLoader), nameof(ResourceLoader.LoadThreadedGet)), prefix: new HarmonyMethod(typeof(Main), nameof(Retrieving)));
            ShaderCompatibilityPatches.Apply(harmony);
            var game = new NGame();
            AddChild(game);
            await Frame();
            await CheckShaderLifecycle(game);
            await CheckBackgroundPreparation();
            GD.Print("PASS: native Godot resource readiness/serialization/failure recovery and shader parent-Ready/reparent/removal/isolation/exclusion.");
            GetTree().Quit();
        }
        catch (Exception exception)
        {
            GD.PrintErr(exception);
            GetTree().Quit(1);
        }
    }

    private static bool MobilePlatform(ref string __result) { __result = "Android"; return false; }
    private static void Requested(string path, Error __result)
    {
        if (__result != Error.Ok) return;
        ActiveRequests.Add(path);
        _maxRequests = Math.Max(_maxRequests, ActiveRequests.Count);
        _wrongThread |= OS.GetThreadCallerId() != OS.GetMainThreadId();
    }
    private static void Retrieving(string path)
    {
        _retrievedInProgress |= ResourceLoader.LoadThreadedGetStatus(path) == ResourceLoader.ThreadLoadStatus.InProgress;
        _wrongThread |= OS.GetThreadCallerId() != OS.GetMainThreadId();
        ActiveRequests.Remove(path);
        _retrieved++;
    }
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

    private async Task CheckBackgroundPreparation()
    {
        var scene = new StringBuilder("[gd_scene format=3]\n[node name=\"Root\" type=\"Node\"]\n");
        for (int i = 0; i < 12000; i++) scene.Append($"[node name=\"Child{i}\" type=\"Node\" parent=\".\"]\n");
        string[] paths = { "user://frame-a.tscn", "user://frame-b.tscn", "user://frame-invalid.tres" };
        try
        {
            using (var file = FileAccess.Open(paths[0], FileAccess.ModeFlags.Write)) file.StoreString(scene.ToString());
            using (var file = FileAccess.Open(paths[1], FileAccess.ModeFlags.Write)) file.StoreString(scene.ToString());
            using (var file = FileAccess.Open(paths[2], FileAccess.ModeFlags.Write)) file.StoreString("[gd_resource broken");
            ulong before = Engine.GetProcessFrames();
            var first = AndroidResourcePreloader.LoadAsync(paths[0], ResourceLoader.CacheMode.Ignore);
            var second = AndroidResourcePreloader.LoadAsync(paths[1], ResourceLoader.CacheMode.Ignore);
            var loaded = await Task.WhenAll(first, second);
            foreach (var resource in loaded)
            {
                Require(resource is PackedScene packed && packed.GetState().GetNodeCount() == 12001, "Background preparation must deliver complete scene data.");
                resource.Dispose();
            }
            Require(_maxRequests == 1 && ActiveRequests.Count == 0, "Compat preparation must not overlap native requests or leave accepted requests unbalanced.");
            Require(!_retrievedInProgress && !_wrongThread, "Retrieval must be ready and all API calls must stay on the Godot thread.");
            GD.Print($"Two real scene loads: responsive_frames={Engine.GetProcessFrames() - before}; max_in_flight={_maxRequests}");

            bool failed = false;
            int retrievedBeforeFailure = _retrieved;
            try { await AndroidResourcePreloader.LoadAsync(paths[2], ResourceLoader.CacheMode.Ignore); }
            catch (InvalidOperationException) { failed = true; }
            Require(failed && ActiveRequests.Count == 0 && _retrieved > retrievedBeforeFailure, "Failed accepted requests must report failure and be consumed.");
            using var recovered = await AndroidResourcePreloader.LoadAsync(paths[0], ResourceLoader.CacheMode.Ignore);
            Require(recovered is PackedScene retry && retry.GetState().GetNodeCount() == 12001, "A failed request must release preparation for the next load.");
        }
        finally
        {
            foreach (string path in paths) DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(path));
        }
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
