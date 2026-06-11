using System;
using System.IO;
using Godot;
using HarmonyLib;
using STS2Mobile.Android;

namespace STS2Mobile.Patches;

public static class DebugMenuPatches
{
    private const string Tag = "[PerformanceOverlay]";
    private const string EnableFlagRelativePath = "launcher/enable_debug_menu.flag";
    private const string BootstrapScriptPath = "res://addons/sts2_mobile_debug_menu/debug_menu_bootstrap.gd";
    private const string NodeName = "STS2MobileDebugMenuBootstrap";
    private static bool _installed;

    public static void Apply(Harmony _)
    {
        if (!IsEnabled())
            return;

        try
        {
            Callable.From(InstallWhenReady).CallDeferred();
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"{Tag} schedule failed: {exception}");
        }
    }

    private static bool IsEnabled()
    {
        try
        {
            return File.Exists(Path.Combine(AppPaths.DataDir, EnableFlagRelativePath));
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"{Tag} flag check failed: {exception.Message}");
            return false;
        }
    }

    private static void InstallWhenReady()
    {
        if (_installed)
            return;

        if (Engine.GetMainLoop() is not SceneTree tree || tree.Root == null)
        {
            Callable.From(InstallWhenReady).CallDeferred();
            return;
        }

        try
        {
            ShaderCompatibilityPatches.EnsureOverlayPackLoadedForDiagnostics();

            if (tree.Root.GetNodeOrNull(NodeName) != null)
            {
                _installed = true;
                return;
            }

            var script = ResourceLoader.Load<Script>(BootstrapScriptPath, null, ResourceLoader.CacheMode.Ignore);
            if (script == null)
            {
                PatchHelper.Log($"{Tag} bootstrap script missing: {BootstrapScriptPath}");
                return;
            }

            var node = new Node { Name = NodeName, ProcessMode = Node.ProcessModeEnum.Always };
            node.SetScript(script);
            tree.Root.AddChild(node);
            _installed = true;
            PatchHelper.Log($"{Tag} installed debug menu bootstrap.");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"{Tag} install failed: {exception}");
        }
    }
}
