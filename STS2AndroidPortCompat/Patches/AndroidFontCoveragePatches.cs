using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;

namespace STS2Mobile.Patches;

/// <summary>
/// Applies the Android font-size setting to UI created after the initial display
/// pass without patching Godot's hot AddChild path.
///
/// The previous implementation Harmony-patched Node.AddChild and recursively
/// rescanned every added Control tree, then queued a second deferred rescan.
/// Deck/compendium/combat screens create many card/UI nodes while opening,
/// closing, playing and selecting cards; rapid toggles would therefore build up
/// large recursive/deferred font work and could stall the game.  Match the source
/// Android port instead: subscribe to SceneTree.NodeAdded once from NGame._Ready
/// and scale only the node that Godot reports as newly added.  Full recursive
/// passes still happen when the font setting is initially applied or changed via
/// DisplaySettingsPatches.ApplyFontSizeSetting().
/// </summary>
public static class AndroidFontCoveragePatches
{
    private static SceneTree _subscribedTree;

    public static void Apply(Harmony harmony)
    {
        PatchHelper.Patch(harmony, typeof(NGame), "_Ready", postfix: PatchHelper.Method(typeof(AndroidFontCoveragePatches), nameof(GameReadyPostfix)));
    }

    public static void GameReadyPostfix(NGame __instance)
    {
        try
        {
            var tree = __instance?.GetTree();
            if (tree == null)
                return;
            if (ReferenceEquals(_subscribedTree, tree))
                return;
            if (_subscribedTree != null && GodotObject.IsInstanceValid(_subscribedTree))
            {
                try
                {
                    _subscribedTree.NodeAdded -= OnSceneTreeNodeAdded;
                }
                catch
                {
                }
            }
            _subscribedTree = tree;
            _subscribedTree.NodeAdded += OnSceneTreeNodeAdded;
            PatchHelper.Log("Android font coverage installed via SceneTree.NodeAdded.");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Android font coverage subscription failed: {exception.Message}");
        }
    }

    public static void RegisterTreePostfix(Node node)
    {
        try
        {
            if (node != null)
                DisplaySettingsPatches.ApplyFontSizeOverridesRecursive(node);
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Android font coverage explicit scan failed on {node?.GetType().Name}: {exception.Message}");
        }
    }

    private static void OnSceneTreeNodeAdded(Node node)
    {
        try
        {
            DisplaySettingsPatches.ApplyFontSizeOverridesToAddedNode(node);
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Android font coverage apply failed on {node?.GetType().Name}: {exception.Message}");
        }
    }
}
