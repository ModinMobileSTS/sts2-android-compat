using System;
using System.Collections.Generic;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;
using STS2Mobile.Android;

namespace STS2Mobile.Patches;

public static class ShaderCompatibilityPatches
{
    private const string OverlayPackFileName = "port_compat.pck";

    private static readonly Dictionary<string, string> ShaderOverrides = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        { "res://shaders/dark_blur.gdshader", "res://shaders/mobile_compat/dark_blur_compat.gdshader" },
        { "res://shaders/radial_blur.gdshader", "res://shaders/mobile_compat/radial_blur_compat.gdshader" },
        { "res://shaders/doom_overlay.gdshader", "res://shaders/mobile_compat/doom_overlay_compat.gdshader" },
        { "res://shaders/vfx/distortion/vfx_screen_distortion_outward_shader.gdshader", "res://shaders/mobile_compat/screen_distortion_compat.gdshader" },
        { "res://shaders/vfx/scream/vfx_scream_distortion_polar_shader.gdshader", "res://shaders/mobile_compat/scream_distortion_compat.gdshader" },
        { "res://shaders/vfx/vfx_water_reflection_post.gdshader", "res://shaders/mobile_compat/water_reflection_post_compat.gdshader" },
        { "res://shaders/vfx/the_insatiable_sand_fall_2.gdshader", "res://shaders/mobile_compat/sand_fall_post_compat.gdshader" },
        // Do not replace res://shaders/blur/canvas_group_mask_blur.gdshader.
        // It is used by NCard's PortraitCanvasGroup / Ancient-card mask path;
        // the mobile substitute can render Ancient card faces as solid white.
        { "res://shaders/overlay_blend.gdshader", "res://shaders/mobile_compat/overlay_blend_compat.gdshader" },
    };

    private static bool _loadedOverlayPack;
    private static bool _loggedDisabled;
    private static SceneTree _subscribedTree;
    private static bool _enabled;
    private static readonly List<CanvasItem> PendingMaterials = new();
    private static readonly HashSet<ulong> PendingIds = new();
    private static bool _materialApplyQueued;
    private static readonly Dictionary<string, Shader> ReplacementShaders = new(StringComparer.Ordinal);

    public static void Apply(Harmony harmony)
    {
        Callable.From(LoadOverlayPackWhenReady).CallDeferred();
        PatchHelper.Patch(harmony, typeof(NGame), "_Ready", postfix: PatchHelper.Method(typeof(ShaderCompatibilityPatches), nameof(GameReadyPostfix)));
    }

    public static void GameReadyPostfix(NGame __instance)
    {
        try
        {
            var tree = __instance?.GetTree();
            if (tree == null)
                return;
            if (!ReferenceEquals(_subscribedTree, tree))
            {
                if (_subscribedTree != null && GodotObject.IsInstanceValid(_subscribedTree))
                    _subscribedTree.NodeAdded -= OnNodeAdded;
                _subscribedTree = tree;
                _enabled = false;
            }
            EnsureOverlayPackLoadedForDiagnostics();
            RefreshSettings();
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Shader compatibility installation failed: {exception.Message}");
        }
    }

    internal static void RefreshSettings()
    {
        try
        {
            if (_subscribedTree == null || !GodotObject.IsInstanceValid(_subscribedTree))
                return;
            bool enabled = IsEnabled();
            if (_enabled == enabled)
                return;
            _enabled = enabled;
            if (enabled)
            {
                _subscribedTree.NodeAdded += OnNodeAdded;
                ApplyRecursive(_subscribedTree.Root);
            }
            else
            {
                _subscribedTree.NodeAdded -= OnNodeAdded;
                PendingMaterials.Clear();
                PendingIds.Clear();
            }
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Shader compatibility settings refresh failed: {exception.Message}");
        }
    }

    private static void OnNodeAdded(Node node)
    {
        if (!_enabled || node is not CanvasItem canvasItem)
            return;
        if (!PendingIds.Add(canvasItem.GetInstanceId()))
            return;
        PendingMaterials.Add(canvasItem);
        if (_materialApplyQueued)
            return;
        _materialApplyQueued = true;
        // Wait until the entire AddChild/_Ready stack finishes: a parent's
        // _Ready can still replace a child's material after the child's Ready.
        // One idle callback handles the batch; never rescan each added subtree.
        Callable.From(ApplyPendingMaterials).CallDeferred();
    }

    private static void ApplyPendingMaterials()
    {
        try
        {
            if (!_enabled)
                return;
            for (int i = 0; i < PendingMaterials.Count; i++)
            {
                var canvasItem = PendingMaterials[i];
                try
                {
                    if (GodotObject.IsInstanceValid(canvasItem) && canvasItem.IsInsideTree())
                        TryReplaceMaterial(canvasItem);
                }
                catch (Exception exception)
                {
                    PatchHelper.Log($"Shader compatibility replacement failed: {exception.Message}");
                }
            }
        }
        finally
        {
            PendingMaterials.Clear();
            PendingIds.Clear();
            _materialApplyQueued = false;
        }
    }

    private static void LoadOverlayPackWhenReady()
    {
        if (_loadedOverlayPack)
            return;
        if (Engine.GetMainLoop() is not SceneTree)
        {
            Callable.From(LoadOverlayPackWhenReady).CallDeferred();
            return;
        }
        EnsureOverlayPackLoadedForDiagnostics();
    }

    public static bool EnsureOverlayPackLoadedForDiagnostics()
    {
        if (_loadedOverlayPack)
            return true;
        try
        {
            var pckPath = System.IO.Path.Combine(OS.GetDataDir(), OverlayPackFileName);
            if (System.IO.File.Exists(pckPath))
            {
                _loadedOverlayPack = ProjectSettings.LoadResourcePack(pckPath);
                PatchHelper.Log($"Shader compatibility overlay pack load {(_loadedOverlayPack ? "succeeded" : "failed")}: {pckPath}");
            }
            else
            {
                PatchHelper.Log($"Shader compatibility overlay pack missing: {pckPath}");
            }
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Shader compatibility overlay load failed: {exception.Message}");
        }
        return _loadedOverlayPack;
    }

    private static bool IsEnabled()
    {
        if (!OS.GetName().Equals("Android", StringComparison.OrdinalIgnoreCase) && !OS.GetName().Equals("iOS", StringComparison.OrdinalIgnoreCase))
            return false;
        var enabled = AndroidSettingsBridge.GetBool("shader_compatibility_mode", false);
        if (!enabled && !_loggedDisabled)
        {
            _loggedDisabled = true;
            PatchHelper.Log("Shader compatibility mode disabled by companion settings.");
        }
        return enabled;
    }

    private static void ApplyRecursive(Node node)
    {
        if (node is CanvasItem canvasItem)
            OnNodeAdded(canvasItem);
        for (int i = 0; i < node.GetChildCount(); i++)
            ApplyRecursive(node.GetChild(i));
    }

    private static void TryReplaceMaterial(CanvasItem canvasItem)
    {
        if (canvasItem.Material is not ShaderMaterial shaderMaterial)
            return;
        var shader = shaderMaterial.Shader;
        var resourcePath = shader?.ResourcePath ?? string.Empty;
        if (string.IsNullOrWhiteSpace(resourcePath) || !ShaderOverrides.TryGetValue(resourcePath, out var replacementPath))
            return;
        if (!ReplacementShaders.TryGetValue(replacementPath, out var loadedShader) || !GodotObject.IsInstanceValid(loadedShader))
        {
            loadedShader = ResourceLoader.Load<Shader>(replacementPath, null, ResourceLoader.CacheMode.Reuse);
            if (loadedShader == null)
            {
                PatchHelper.Log($"Shader compatibility replacement missing: {replacementPath}");
                return;
            }
            ReplacementShaders[replacementPath] = loadedShader;
        }
        var replacementMaterial = (ShaderMaterial)shaderMaterial.Duplicate(true);
        replacementMaterial.Shader = loadedShader;
        canvasItem.Material = replacementMaterial;
        PatchHelper.Log($"Shader compatibility replaced {resourcePath} -> {replacementPath}");
    }
}
