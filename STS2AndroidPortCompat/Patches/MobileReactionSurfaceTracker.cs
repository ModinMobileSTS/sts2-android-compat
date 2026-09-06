#nullable enable

using System.Collections.Generic;
using Godot;

namespace STS2Mobile.Patches;

// Owned by the reaction button, not a static scene cache. Seed once on attachment;
// thereafter node lifecycle events maintain only the known multiplayer UI surfaces.
internal sealed class MobileReactionSurfaceTracker
{
    private static readonly StringName RemotePlayers = new("RemotePlayerContainer");
    private static readonly StringName RemoteLoadPlayers = new("RemotePlayerLoadContainer");
    private static readonly StringName ReadyPanel = new("ReadyAndWaitingPanel");
    private static readonly StringName WaitingOverlay = new("WaitingForOtherPlayers");
    private static readonly StringName WaitingPlayers = new("WaitingForPlayers");
    private static readonly StringName WaitingReady = new("WaitingForReady");

    private readonly List<(CanvasItem Node, bool Waiting)> _surfaces = new();
    private SceneTree? _tree;

    internal void Start(SceneTree? tree)
    {
        if (ReferenceEquals(_tree, tree))
            return;
        Stop();
        if (tree == null)
            return;
        _tree = tree;
        tree.NodeAdded += OnNodeAdded;
        tree.NodeRemoved += OnNodeRemoved;
        Seed(tree.Root);
    }

    internal void Stop()
    {
        if (_tree != null && GodotObject.IsInstanceValid(_tree))
        {
            _tree.NodeAdded -= OnNodeAdded;
            _tree.NodeRemoved -= OnNodeRemoved;
        }
        _tree = null;
        _surfaces.Clear();
    }

    internal bool HasVisibleSurface(Node? currentScene, Node? overlayStack)
    {
        foreach (var surface in _surfaces)
        {
            var node = surface.Node;
            if (!GodotObject.IsInstanceValid(node) || !node.IsInsideTree())
                continue;
            if (!IsWithin(currentScene, node) && !(surface.Waiting && IsWithin(overlayStack, node)))
                continue;
            // Includes hidden ancestors: a waiting label in a hidden panel must
            // not turn the single-player reaction button on.
            if (node.IsVisibleInTree())
                return true;
        }
        return false;
    }

    private static bool IsWithin(Node? root, Node node)
    {
        return root != null && (ReferenceEquals(root, node) || root.IsAncestorOf(node));
    }

    private void Seed(Node node)
    {
        OnNodeAdded(node);
        int count = node.GetChildCount();
        for (int i = 0; i < count; i++)
            Seed(node.GetChild(i));
    }

    private void OnNodeAdded(Node node)
    {
        if (node is not CanvasItem canvasItem)
            return;
        // Name wrappers and native child-array snapshots must not accumulate on
        // every visibility poll. Names are inspected only when a node enters.
        using var name = node.Name;
        bool waiting = name == ReadyPanel || name == WaitingOverlay
            || name == WaitingPlayers || name == WaitingReady;
        if (!waiting && name != RemotePlayers && name != RemoteLoadPlayers)
            return;
        foreach (var surface in _surfaces)
        {
            if (ReferenceEquals(surface.Node, node))
                return;
        }
        _surfaces.Add((canvasItem, waiting));
    }

    private void OnNodeRemoved(Node node)
    {
        for (int i = _surfaces.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(_surfaces[i].Node, node))
                _surfaces.RemoveAt(i);
        }
    }
}
