using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using STS2Mobile.Android;
using STS2Mobile.Patches;

internal static class Program
{
    private static void Main(string[] args)
    {
        var harmony = new Harmony("tests.mobile.hot-paths");
        MobileTooltipPatches.Apply(harmony);
        IntentAnimationPatches.Apply(harmony);
        TouchInputPatches.Apply(harmony);
        TooltipTransitions();
        IntentTransitions();
        InputTransitions();
        Console.WriteLine("PASS: tooltip mode/hold/rebuild/reparent/release, stable-frame allocation budget, intent visual changes and release-only input.");
    }

    private static void TooltipTransitions()
    {
        var owner = new Control();
        var tip = NHoverTipSet.CreateAndShow(owner);
        tip._Process(1.0 / 120);
        Require(tip.Visible && tip.FollowFrames == 1, "immediate tooltip keeps vanilla following");
        AssertSteadyFrames(tip, "immediate");

        AndroidSettingsBridge.Mode = "long_press";
        MobileTooltipPatches.RefreshModeFromSettings();
        Require(!tip.Valid, "mode switch removes existing ordinary tooltip");
        Time.Msec = 200;
        tip = NHoverTipSet.CreateAndShow(owner);
        Require(!tip.Visible, "long press starts hidden after original creation");
        Time.Msec = 800;
        tip._Process(0.1);
        Require(!tip.Visible, "long press must not reveal early");
        NHoverTipSet.Clear();
        tip = NHoverTipSet.CreateAndShow(owner);
        Time.Msec = 1201;
        tip._Process(0.1);
        Require(tip.Visible, "Clear/recreate while held must preserve original deadline");
        AssertSteadyFrames(tip, "revealed long press");
        NHoverTipSet.Clear();
        tip = NHoverTipSet.CreateAndShow(owner);
        Require(tip.Visible, "new tip for revealed owner must be revealed immediately");
        NGame.Instance._Input(new InputEventScreenDrag { Position = new Vector2(80, 0) });
        Require(!tip.Visible, "drag cancels revealed tooltip");
        NHoverTipSet.Clear();

        Time.Msec = 2000;
        tip = NHoverTipSet.CreateAndShow(owner);
        Time.Msec = 3001;
        tip._Process(0.1);
        NGame.Instance._Input(new InputEventScreenTouch { Pressed = false });
        Require(!tip.Visible, "release cancels revealed tooltip");

        var detail = new NInspectCardScreen();
        owner.Reparent(detail);
        NHoverTipSet.Remove(owner);
        tip = NHoverTipSet.CreateAndShow(owner);
        tip._Process(0.1);
        Require(tip.Visible, "detail owner remains exempt after reparent");
        owner.Reparent(new Control());
        NHoverTipSet.Remove(owner);
        tip = NHoverTipSet.CreateAndShow(owner);
        Require(!tip.Visible, "leaving detail subtree restores long-press policy");
        AndroidSettingsBridge.Mode = "hidden";
        MobileTooltipPatches.RefreshModeFromSettings();
        Require(NHoverTipSet.CreateAndShow(owner) == null, "hidden mode blocks ordinary tooltip");
        owner.Reparent(detail);
        tip = NHoverTipSet.CreateAndShow(owner);
        Require(tip != null && tip.Visible, "hidden mode does not suppress explicit details");
        NHoverTipSet.Clear();
        AndroidSettingsBridge.Mode = "immediate";
        MobileTooltipPatches.RefreshModeFromSettings();
    }

    private static void AssertSteadyFrames(NHoverTipSet tip, string mode)
    {
        for (int i = 0; i < 100; i++) tip._Process(1.0 / 120);
        var before = GC.GetAllocatedBytesForCurrentThread();
        int writes = tip.VisibilityWrites;
        for (int i = 0; i < 1000; i++) tip._Process(1.0 / 120);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"{mode}: 1000 steady frames allocated {allocated} bytes");
        Require(allocated < 1024, mode + " must not allocate fresh tracking objects every frame");
        Require(tip.VisibilityWrites == writes, mode + " must not repeatedly write unchanged visibility");
    }

    private static void IntentTransitions()
    {
        var intent = new NIntent();
        Time.Msec = 4000;
        intent._Ready();
        intent.UpdateIntent("attack");
        intent._Process(0);
        Require(intent.Shown?.Path == "attack0", "first intent frame");
        // Warm a complete cycle, then verify no repeat resource lookups.
        for (int i = 1; i <= 3; i++) { Time.Msec = (ulong)(4000 + i * 42); intent._Process(0.042); }
        int lookups = PreloadManager.Cache.Lookups;
        Time.Msec = 4168;
        intent._Process(0.042);
        Require(intent.Shown.Path == "attack1", "24 FPS animation progression");
        Require(PreloadManager.Cache.Lookups == lookups, "repeated animation cycles must reuse frame textures");
        intent.ChangeVisuals("defend");
        intent._Process(0);
        Require(intent.Shown.Path == "defend0", "combat-state change outside UpdateIntent refreshes animation");
        intent._ExitTree();
        Time.Msec = 5000;
        intent._Ready();
        intent._Process(0);
        Require(intent.Shown.Path == "defend0", "reentry starts a fresh animation state");
        intent._ExitTree();
    }

    private static void InputTransitions()
    {
        OS.MobileFeature = true;
        var play = new NMouseCardPlay();
        play.Start();
        int reads = AndroidSettingsBridge.Reads;
        play._Input(new InputEventScreenDrag());
        Require(!play.Cancelled && AndroidSettingsBridge.Reads == reads, "movement does not consult release-only settings or cancel play");
        play._Input(new InputEventScreenTouch { Pressed = false });
        Require(play.Cancelled, "release outside play zone still cancels");
        var target = new NTargetManager();
        target.StartTargeting();
        target._Input(new InputEventScreenTouch { Pressed = false });
        Require(target.Cancelled && TouchInputPatches.ConsumeCancelledByUntargetedRelease(target), "untargeted release remains consumable cancellation");
        target = new NTargetManager { HoveredNode = new Node() };
        target._Input(new InputEventScreenTouch { Pressed = false });
        Require(!target.Cancelled, "release over a target remains original targeting");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
