using System;
using Godot;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using STS2Mobile.Patches;

internal static class CursorRecoveryTests
{
    public static void Run()
    {
        var target = NewTarget(out var cursor, out var image);
        target.StartTargeting();
        Require(!Input.PointerVisible, "Mouse targeting must keep the cursor hidden.");
        target.CompleteTargeting();
        Require(Input.MouseMode == Input.MouseModeEnum.Visible && Input.PointerVisible,
            "Successful targeting must restore the Android pointer without another click.");
        Require(ReferenceEquals(Input.CursorImage, image) && Input.CursorHotSpot.X == 9 && Input.CursorHotSpot.Y == 7,
            "Recovery must preserve the current overridden cursor image and hotspot.");

        target = NewTarget(out cursor, out image);
        target.StartTargeting();
        target.CancelTargeting();
        Require(target.Cancelled && Input.PointerVisible, "Canceling targeting must restore the pointer too.");

        target = NewTarget(out cursor, out image);
        target.StartTargeting();
        target.TargetingArrow._ExitTree();
        Require(Input.PointerVisible, "Leaving the targeting scene must restore the pointer after the original Visible transition.");

        target = NewTarget(out cursor, out image);
        target.StartTargeting();
        target.TargetingArrow.RestoreOnFinish = false;
        target.CompleteTargeting();
        Require(Input.MouseMode == Input.MouseModeEnum.Hidden && !Input.PointerVisible,
            "Recovery cannot override a still-hidden cursor.");

        target = NewTarget(out cursor, out image);
        target.StartTargeting();
        cursor.UseController();
        target.TargetingArrow._ExitTree();
        Require(!Input.PointerVisible, "A stale mouse arrow cannot reveal a controller-owned cursor on exit.");

        target = NewTarget(out cursor, out image);
        target.StartTargeting();
        cursor.HideCursor();
        target.CompleteTargeting();
        Require(!Input.PointerVisible, "Explicit cursor hiding cannot be overridden by targeting cleanup.");

        target = NewTarget(out cursor, out image);
        MobileInputPolicyPatches.TouchControls = true;
        target.StartTargeting();
        target.CompleteTargeting();
        Require(!Input.PointerVisible, "Touch-only targeting must not enable the hardware cursor repair.");
        MobileInputPolicyPatches.TouchControls = false;

        target = NewTarget(out cursor, out image);
        target.TargetingArrow.StartDrawingFrom(true);
        target.TargetingArrow._ExitTree();
        Require(Input.MouseMode == Input.MouseModeEnum.Hidden && !Input.PointerVisible,
            "Controller targeting cannot enter the mouse restoration path.");
        Input.MouseMode = Input.MouseModeEnum.Visible;
        Console.WriteLine("PASS: targeting cursor success, cancel, exit, image/hotspot preservation and intentional-hidden/controller/touch isolation.");
    }

    private static NTargetManager NewTarget(out NCursorManager cursor, out Image image)
    {
        OS.Platform = "Android";
        OS.MobileFeature = true;
        MobileInputPolicyPatches.TouchControls = false;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        cursor = NGame.Instance.CursorManager = new NCursorManager();
        image = new Image();
        cursor.SetAppearance(image, new Vector2(9, 7));
        return new NTargetManager { TargetingArrow = new NTargetingArrow() };
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
