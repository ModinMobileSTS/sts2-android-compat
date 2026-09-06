using System.Runtime.CompilerServices;
using Godot;
using STS2Mobile.Patches;

internal static class SurfaceTrackerTests
{
    internal static void Run()
    {
        var tree = new SceneTree();
        var scene = new CanvasItem();
        var overlays = new CanvasItem();
        var inactiveScene = new CanvasItem();
        tree.Root.AddChild(scene);
        tree.Root.AddChild(overlays);
        tree.Root.AddChild(inactiveScene);
        var panel = new CanvasItem { Visible = false };
        var remote = new CanvasItem { Name = "RemotePlayerContainer" };
        scene.AddChild(panel);
        panel.AddChild(remote);
        inactiveScene.AddChild(new CanvasItem { Name = "RemotePlayerLoadContainer" });

        var tracker = new MobileReactionSurfaceTracker();
        tracker.Start(tree);
        tracker.Start(tree);
        Check(!tracker.HasVisibleSurface(scene, overlays), "hidden parent or inactive scene enabled single-player reactions");
        panel.Visible = true;
        Check(tracker.HasVisibleSurface(scene, overlays), "existing lobby did not appear after parent became visible");
        panel.Visible = false;
        Check(!tracker.HasVisibleSurface(scene, overlays), "hidden lobby stayed available");
        Check(tracker.HasVisibleSurface(inactiveScene, overlays), "switching current scene lost its lobby");

        var waiting = new CanvasItem { Name = "ReadyAndWaitingPanel", Visible = false };
        waiting.AddChild(new CanvasItem { Name = "WaitingForReady" });
        overlays.AddChild(waiting);
        Check(!tracker.HasVisibleSurface(scene, overlays), "waiting label ignored hidden parent");
        waiting.Visible = true;
        Check(tracker.HasVisibleSurface(scene, overlays), "dynamically added waiting overlay was not indexed");
        overlays.Visible = false;
        Check(!tracker.HasVisibleSurface(scene, overlays), "hidden overlay stack stayed available");
        overlays.Visible = true;
        overlays.RemoveChild(waiting);
        Check(!tracker.HasVisibleSurface(scene, overlays), "detached waiting surface stayed available");
        overlays.AddChild(waiting);
        Check(tracker.HasVisibleSurface(scene, overlays), "reentered waiting surface was not restored");
        overlays.RemoveChild(waiting);

        var overlayLobby = new CanvasItem { Name = "RemotePlayerContainer" };
        overlays.AddChild(overlayLobby);
        Check(!tracker.HasVisibleSurface(scene, overlays), "lobby-only surface outside current scene was treated as waiting overlay");
        overlays.RemoveChild(overlayLobby);
        var removed = TrackThenRemove(tree, tracker);
        Collect();
        Check(!removed.IsAlive, "removed scene is retained by the surface index");

        tracker.Stop();
        Check(!tracker.HasVisibleSurface(inactiveScene, overlays), "stopped tracker retained old surfaces");
        tracker.Start(tree);
        Check(tracker.HasVisibleSurface(inactiveScene, overlays), "restart did not seed existing surfaces");
        var replacementTree = new SceneTree();
        tracker.Start(replacementTree);
        Check(!tracker.HasVisibleSurface(inactiveScene, overlays), "changing trees retained old surfaces");
        tracker.Stop();
        var stopped = StartThenStop(tree);
        Collect();
        Check(!stopped.IsAlive, "SceneTree callbacks retain a stopped tracker");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference TrackThenRemove(SceneTree tree, MobileReactionSurfaceTracker tracker)
    {
        var scene = new CanvasItem { Name = "WaitingForOtherPlayers" };
        tree.Root.AddChild(scene);
        Check(tracker.HasVisibleSurface(scene, null), "candidate scene root was not recognized");
        tree.Root.RemoveChild(scene);
        return new WeakReference(scene);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference StartThenStop(SceneTree tree)
    {
        var tracker = new MobileReactionSurfaceTracker();
        tracker.Start(tree);
        tracker.Stop();
        return new WeakReference(tracker);
    }

    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
