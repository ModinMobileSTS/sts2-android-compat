using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.Timeline;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using STS2Mobile.Android;

namespace STS2Mobile.Patches;

/// <summary>
/// Installs opt-in Android shader variants without mutating the game's shared
/// resources.  Independent shaders use exact built-in resource paths.  Inline
/// VisualShader resources have no stable resource path, so they use the
/// audited generated-code SHA-256 plus a built-in scene prefix.  User MOD
/// resources under res://mods are never eligible for an inline replacement.
/// </summary>
public static class ShaderCompatibilityPatches
{
    private const string OverlayPackFileName = "port_compat.pck";
    private const string IroncladEnergyCounterScene = "res://scenes/combat/energy_counters/ironclad_energy_counter.tscn";
    private const string IroncladEnergyFrontScene = "res://scenes/vfx/energy/ironclad/ironclad_energy_vfx_front.tscn";
    private const string FireEmbersScene = "res://scenes/vfx/fire_impact/vfx_fire_embers_straight.tscn";

    private static readonly Dictionary<string, string> ShaderOverrides = new(StringComparer.Ordinal)
    {
        // Existing screen effects: keep the original parameter protocol and
        // restore scene content instead of the old white/black placeholders.
        { "res://shaders/dark_blur.gdshader", "res://shaders/mobile_compat/dark_blur_compat.gdshader" },
        { "res://shaders/radial_blur.gdshader", "res://shaders/mobile_compat/radial_blur_compat.gdshader" },
        { "res://shaders/doom_overlay.gdshader", "res://shaders/mobile_compat/doom_overlay_compat.gdshader" },
        { "res://shaders/vfx/distortion/vfx_screen_distortion_outward_shader.gdshader", "res://shaders/mobile_compat/screen_distortion_compat.gdshader" },
        { "res://shaders/vfx/scream/vfx_scream_distortion_polar_shader.gdshader", "res://shaders/mobile_compat/scream_distortion_compat.gdshader" },
        { "res://shaders/vfx/vfx_water_reflection_post.gdshader", "res://shaders/mobile_compat/water_reflection_post_compat.gdshader" },
        { "res://shaders/vfx/the_insatiable_sand_fall_2.gdshader", "res://shaders/mobile_compat/sand_fall_post_compat.gdshader" },
        { "res://shaders/overlay_blend.gdshader", "res://shaders/mobile_compat/overlay_blend_compat.gdshader" },

        // Public high-coverage variants.
        { "res://shaders/blur/Blur.gdshader", "res://shaders/mobile_compat/card_portrait_blur_compat.gdshader" },
        { "res://shaders/vfx/vfx_water_reflection.gdshader", "res://shaders/mobile_compat/water_reflection_compat.gdshader" },
        { "res://shaders/vfx/common/vfx_flipbook_shader.gdshader", "res://shaders/mobile_compat/flipbook_compat.gdshader" },
        { "res://shaders/vfx/common/vfx_row_flipbook_shader.gdshader", "res://shaders/mobile_compat/row_flipbook_compat.gdshader" },
        { "res://shaders/vfx/common/vfx_screen_chromatic_aberration_shader.gdshader", "res://shaders/mobile_compat/screen_chromatic_aberration_compat.gdshader" },
        { "res://shaders/hsv.gdshader", "res://shaders/mobile_compat/hsv_compat.gdshader" },
        { "res://shaders/scry_reveal.gdshader", "res://shaders/mobile_compat/scry_reveal_compat.gdshader" },
        { "res://scenes/rest_site/rest_site_light_shader.gdshader", "res://shaders/mobile_compat/rest_site_light_compat.gdshader" },
        { "res://scenes/backgrounds/vantom_boss/oil.gdshader", "res://shaders/mobile_compat/oil_compat.gdshader" },
    };

    // The ordinary canvas-group mask blur is intentionally excluded.  It is
    // the Ancient-card rotated CanvasGroup/mask/alpha path; replacing it with
    // a generic blur loses the card and was observed as a solid white face.
    private static readonly Dictionary<string, string> MaterialOverrides = new(StringComparer.Ordinal)
    {
        { "res://scenes/cards/card_portrait_blur_material.tres", "res://shaders/mobile_compat/card_portrait_blur_compat.gdshader" },
        { "res://scenes/cards/card_canvas_group_blur_material.tres", "res://shaders/mobile_compat/card_canvas_group_blur_compat.gdshader" },
    };

    // Hashes are generated shader code bodies from the v0.111.0 payload audit.
    // The path check below is deliberately separate: a user MOD must not get a
    // game VisualShader replacement merely because its code happens to match.
    private static readonly Dictionary<string, string> EmbeddedShaderOverrides = new(StringComparer.OrdinalIgnoreCase)
    {
        { "10365f5ad4c7cf285634a92558b35b4c13f1c2aa188502cef3859a4493dc7198", "res://shaders/mobile_compat/stepped_fire_flat_compat.gdshader" },
        { "020ed35c9ac3f2b3c5312d36c4fbe98d5d456023270a94c6fd44e2c40aaef721", "res://shaders/mobile_compat/stepped_fire_add_compat.gdshader" },
        { "6fae0954ba6d4ae43947d8ad19be2a2f8a04252e9969b6d5faa0cc0bc16ed537", "res://shaders/mobile_compat/stepped_fire_dark_compat.gdshader" },
        { "50f860bed946a5b3403cfde1bf3f1ec57fa34f7be95b5da48f5a355d423af55e", "res://shaders/mobile_compat/blood_wall_compat.gdshader" },
        { "26fed431691f6b33f2dc53ee181a83d043e9fed697dc168ab172767606636b2e", "res://shaders/mobile_compat/molten_fist_compat.gdshader" },
        { "077573061b6ca57f86aa02cc19f0bcd7bd141017dd6a0fa9a46da212c22a8bce", "res://shaders/mobile_compat/aeonglass_ray_compat.gdshader" },
        { "fc34286df4b9e0354b49bfc886890613c1402ab376df43e9e889773475f04fab", "res://shaders/mobile_compat/slash_compat.gdshader" },
        { "d9dc65a610bdc75a0a7cf45a4a06cafc75630555048448da2e8030332eecbc90", "res://shaders/mobile_compat/fire_shader_1_compat.gdshader" },
        { "74722e307dca888796f2ee1f6a4bdd7816f8d44564c842d4e71572347d9d18db", "res://shaders/mobile_compat/2d_sphere_compat.gdshader" },
    };

    private static bool _loadedOverlayPack;
    private static bool _loggedDisabled;
    private static bool _loggedEmbeddedPath;
    private static SceneTree _subscribedTree;
    private static bool _enabled;
    private static readonly List<CanvasItem> PendingMaterials = new();
    private static readonly HashSet<ulong> PendingIds = new();
    private static bool _materialApplyQueued;
    private static readonly Dictionary<string, Shader> ReplacementShaders = new(StringComparer.Ordinal);
    private static readonly HashSet<string> LoggedReplacements = new(StringComparer.Ordinal);

    public static void Apply(Harmony harmony)
    {
        Callable.From(LoadOverlayPackWhenReady).CallDeferred();
        PatchHelper.Patch(harmony, typeof(NGame), "_Ready", postfix: PatchHelper.Method(typeof(ShaderCompatibilityPatches), nameof(GameReadyPostfix)));
        PatchHelper.Patch(harmony, typeof(AssetCache), "GetMaterial", postfix: PatchHelper.Method(typeof(ShaderCompatibilityPatches), nameof(GetMaterialPostfix)));
        PatchHelper.Patch(harmony, typeof(NMainMenu), "_Ready", postfix: PatchHelper.Method(typeof(ShaderCompatibilityPatches), nameof(MainMenuReadyPostfix)));
        PatchHelper.Patch(harmony, typeof(NEpochSlot), "_Ready", postfix: PatchHelper.Method(typeof(ShaderCompatibilityPatches), nameof(EpochSlotReadyPostfix)));
        PatchHelper.Patch(harmony, typeof(NRadialBlurVfx), "_Ready", postfix: PatchHelper.Method(typeof(ShaderCompatibilityPatches), nameof(RadialBlurReadyPostfix)));
        PatchHelper.Patch(harmony, typeof(NCard), "Reload", postfix: PatchHelper.Method(typeof(ShaderCompatibilityPatches), nameof(CardReloadPostfix)));
        PatchHelper.Patch(harmony, typeof(NEnergyCounter), "Create", postfix: PatchHelper.Method(typeof(ShaderCompatibilityPatches), nameof(EnergyCounterCreatePostfix)));
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
            {
                if (enabled)
                    RepairKnownCachedMaterials(_subscribedTree.Root);
                return;
            }
            _enabled = enabled;
            if (enabled)
            {
                EnsureOverlayPackLoadedForDiagnostics();
                _subscribedTree.NodeAdded += OnNodeAdded;
                ApplyRecursive(_subscribedTree.Root);
                RepairKnownCachedMaterials(_subscribedTree.Root);
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

    public static void GetMaterialPostfix(string path, ref Material __result)
    {
        if (!_enabled || !IsEnabled() || __result is not ShaderMaterial shaderMaterial)
            return;
        try
        {
            EnsureOverlayPackLoadedForDiagnostics();
            if (MaterialOverrides.TryGetValue(path ?? string.Empty, out var materialReplacement))
            {
                // The same cached material serves the Ancient-card rotated mask
                // path.  Its mask/rotation contract is intentionally untouched.
                if (IsMaskedCanvasGroup(shaderMaterial))
                    return;
                if (TryCreateReplacement(shaderMaterial, materialReplacement, out var duplicate))
                    __result = duplicate;
                return;
            }
            if (TryResolveReplacement(shaderMaterial.Shader, shaderMaterial.ResourcePath, out var replacementPath)
                && TryCreateReplacement(shaderMaterial, replacementPath, out var replacement))
            {
                __result = replacement;
            }
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Shader compatibility material-cache replacement failed: {exception.Message}");
        }
    }

    public static void MainMenuReadyPostfix(NMainMenu __instance)
    {
        if (!IsEnabled())
            return;
        EnsureOverlayPackLoadedForDiagnostics();
        ReplaceCachedField(__instance, "_blur", "BlurBackstop");
    }

    public static void EpochSlotReadyPostfix(NEpochSlot __instance)
    {
        if (!IsEnabled())
            return;
        EnsureOverlayPackLoadedForDiagnostics();
        ReplaceCachedField(__instance, "_blurShader", "%Blur");
    }

    public static void RadialBlurReadyPostfix(NRadialBlurVfx __instance)
    {
        if (!IsEnabled())
            return;
        EnsureOverlayPackLoadedForDiagnostics();
        ReplaceCachedField(__instance, "_blurShader", "Rect");
    }

    public static void EnergyCounterCreatePostfix(NEnergyCounter __result)
    {
        if (__result == null || !IsEnabled())
            return;
        try
        {
            // Use the active renderer, not the requested project setting: a
            // Vulkan request can have fallen back to Compatibility at runtime.
            if (!OS.GetName().Equals("Android", StringComparison.OrdinalIgnoreCase)
                || __result.SceneFilePath != IroncladEnergyCounterScene
                || RenderingServer.GetCurrentRenderingMethod() != "gl_compatibility")
                return;
            var adapter = RenderingServer.GetVideoAdapterName();
            if (adapter.IndexOf("Maleoon", StringComparison.OrdinalIgnoreCase) < 0)
                return;

            var front = __result.GetNodeOrNull<Node2D>("EnergyVfxFront");
            if (front?.SceneFilePath != IroncladEnergyFrontScene)
                return;
            var particles = front.GetNodeOrNull<GpuParticles2D>("vfx_fire_embers_straight");
            if (particles == null || particles.SceneFilePath != FireEmbersScene
                || !particles.OneShot || particles.GetScript().VariantType != Variant.Type.Nil
                || particles.ProcessMaterial is not ParticleProcessMaterial original
                || !original.TurbulenceEnabled
                || !original.ResourcePath.StartsWith(IroncladEnergyFrontScene + "::", StringComparison.Ordinal))
                return;

            // Create returns a detached scene. Change the material before its
            // first tree entry/Restart can submit the problematic GPU program.
            // Shallow duplication retains every curve/texture without copying
            // or mutating those shared resources. Only the feature bit changes.
            var compatible = (ParticleProcessMaterial)original.Duplicate();
            compatible.TurbulenceEnabled = false;
            particles.ProcessMaterial = compatible;
            if (LoggedReplacements.Add("maleoon/ironclad-energy-turbulence"))
                PatchHelper.Log($"Shader compatibility Maleoon energy embers: turbulence disabled on per-instance material; adapter={adapter} renderer=gl_compatibility.");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Shader compatibility energy particle repair failed: {exception.Message}");
        }
    }

    public static void CardReloadPostfix(object __instance)
    {
        if (!_enabled || !IsEnabled() || __instance == null)
            return;
        EnsureOverlayPackLoadedForDiagnostics();
        try
        {
            ReplaceCardField(__instance, "_portraitBlurMaterial", "_portrait", "_ancientPortrait", "res://shaders/mobile_compat/card_portrait_blur_compat.gdshader");
            ReplaceCardField(__instance, "_canvasGroupBlurMaterial", "_portraitCanvasGroup", null, "res://shaders/mobile_compat/card_canvas_group_blur_compat.gdshader", skipMasked: true);
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Shader compatibility card material repair failed: {exception.Message}");
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
            var pckPath = Path.Combine(OS.GetDataDir(), OverlayPackFileName);
            if (File.Exists(pckPath))
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

    private static void RepairKnownCachedMaterials(Node node)
    {
        if (node == null)
            return;
        if (node is NMainMenu mainMenu)
            MainMenuReadyPostfix(mainMenu);
        else if (node is NEpochSlot epochSlot)
            EpochSlotReadyPostfix(epochSlot);
        else if (node is NRadialBlurVfx radialBlur)
            RadialBlurReadyPostfix(radialBlur);
        else if (node is NEnergyCounter energyCounter)
            EnergyCounterCreatePostfix(energyCounter);
        for (int i = 0; i < node.GetChildCount(); i++)
            RepairKnownCachedMaterials(node.GetChild(i));
    }

    private static void TryReplaceMaterial(CanvasItem canvasItem)
    {
        if (canvasItem.Material is not ShaderMaterial shaderMaterial)
            return;
        if (!TryResolveReplacement(shaderMaterial.Shader, shaderMaterial.ResourcePath, out var replacementPath))
            return;
        if (TryCreateReplacement(shaderMaterial, replacementPath, out var replacementMaterial))
            canvasItem.Material = replacementMaterial;
    }

    private static bool TryResolveReplacement(Shader shader, string ownerResourcePath, out string replacementPath)
    {
        replacementPath = null;
        if (shader == null)
            return false;
        var resourcePath = shader.ResourcePath ?? string.Empty;
        if (ShaderOverrides.TryGetValue(resourcePath, out replacementPath))
            return true;

        var provenancePath = resourcePath;
        if (string.IsNullOrWhiteSpace(provenancePath))
            provenancePath = ownerResourcePath ?? string.Empty;
        if (string.IsNullOrWhiteSpace(provenancePath) || !IsBuiltInEmbeddedResource(provenancePath))
            return false;
        var code = shader.Code;
        if (string.IsNullOrEmpty(code))
            return false;
        var hash = ComputeCodeHash(code);
        if (EmbeddedShaderOverrides.TryGetValue(hash, out replacementPath))
            return true;
        // Godot resources normally use LF, but accepting CRLF avoids making
        // the identity depend on the packer's text newline conversion.
        var normalized = code.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return !string.Equals(normalized, code, StringComparison.Ordinal)
            && EmbeddedShaderOverrides.TryGetValue(ComputeCodeHash(normalized), out replacementPath);
    }

    private static bool IsBuiltInEmbeddedResource(string resourcePath)
    {
        if (resourcePath.StartsWith("res://mods/", StringComparison.OrdinalIgnoreCase)
            || resourcePath.StartsWith("res://user/", StringComparison.OrdinalIgnoreCase))
            return false;
        bool allowed = resourcePath.StartsWith("res://scenes/", StringComparison.OrdinalIgnoreCase)
            || resourcePath.StartsWith("res://images/", StringComparison.OrdinalIgnoreCase)
            || resourcePath.StartsWith("res://shaders/", StringComparison.OrdinalIgnoreCase);
        if (!allowed && !_loggedEmbeddedPath)
        {
            _loggedEmbeddedPath = true;
            PatchHelper.Log($"Shader compatibility skipped embedded shader outside built-in roots: {resourcePath}");
        }
        return allowed;
    }

    private static string ComputeCodeHash(string code)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(code))).ToLowerInvariant();
    }

    private static bool TryCreateReplacement(ShaderMaterial source, string replacementPath, out ShaderMaterial replacement)
    {
        replacement = null;
        if (source == null || !GodotObject.IsInstanceValid(source) || string.IsNullOrEmpty(replacementPath))
            return false;
        if (string.Equals(source.Shader?.ResourcePath, replacementPath, StringComparison.Ordinal))
            return false;
        try
        {
            if (!ReplacementShaders.TryGetValue(replacementPath, out var loadedShader) || !GodotObject.IsInstanceValid(loadedShader))
            {
                loadedShader = ResourceLoader.Load<Shader>(replacementPath, null, ResourceLoader.CacheMode.Reuse);
                if (loadedShader == null)
                {
                    PatchHelper.Log($"Shader compatibility replacement missing: {replacementPath}");
                    return false;
                }
                ReplacementShaders[replacementPath] = loadedShader;
            }
            replacement = source.Duplicate(true) as ShaderMaterial;
            if (replacement == null)
                return false;
            replacement.Shader = loadedShader;
            if (LoggedReplacements.Add(replacementPath))
                PatchHelper.Log($"Shader compatibility variant active: {replacementPath}");
            return true;
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Shader compatibility material clone failed for {replacementPath}: {exception.Message}");
            return false;
        }
    }

    private static void ReplaceCachedField(object instance, string fieldName, string nodePath)
    {
        if (instance == null)
            return;
        try
        {
            var field = AccessTools.Field(instance.GetType(), fieldName);
            if (field?.GetValue(instance) is not ShaderMaterial original)
                return;
            if (!TryResolveReplacement(original.Shader, original.ResourcePath, out var replacementPath)
                || !TryCreateReplacement(original, replacementPath, out var replacement))
                return;
            field.SetValue(instance, replacement);
            if (instance is Node node && node.GetNodeOrNull<CanvasItem>(nodePath) is CanvasItem item)
                item.Material = replacement;
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Shader compatibility cached field repair failed ({fieldName}): {exception.Message}");
        }
    }

    private static void ReplaceCardField(object instance, string fieldName, string firstNodeFieldName, string secondNodeFieldName, string replacementPath, bool skipMasked = false)
    {
        var field = AccessTools.Field(instance.GetType(), fieldName);
        if (field?.GetValue(instance) is not ShaderMaterial original)
            return;
        if (skipMasked && IsMaskedCanvasGroup(original))
            return;
        if (!TryCreateReplacement(original, replacementPath, out var replacement))
            return;
        field.SetValue(instance, replacement);
        ReplaceCardNodeMaterial(instance, firstNodeFieldName, original, replacement);
        ReplaceCardNodeMaterial(instance, secondNodeFieldName, original, replacement);
    }

    private static bool IsMaskedCanvasGroup(ShaderMaterial material)
    {
        return material != null && material.GetShaderParameter("mask").AsBool();
    }

    private static void ReplaceCardNodeMaterial(object instance, string fieldName, ShaderMaterial original, ShaderMaterial replacement)
    {
        if (string.IsNullOrEmpty(fieldName))
            return;
        var nodeField = AccessTools.Field(instance.GetType(), fieldName);
        if (nodeField?.GetValue(instance) is CanvasItem item && ReferenceEquals(item.Material, original))
            item.Material = replacement;
    }
}
