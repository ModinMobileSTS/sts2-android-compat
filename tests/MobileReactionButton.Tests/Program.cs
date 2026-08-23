using System.Numerics;
using STS2Mobile.Patches;

static void AssertEqual(string name, bool expected, bool actual)
{
    if (expected != actual)
        throw new InvalidOperationException($"{name}: expected={expected}, actual={actual}");
}

static void AssertNear(string name, float expected, float actual)
{
    if (MathF.Abs(expected - actual) > 0.001f)
        throw new InvalidOperationException($"{name}: expected={expected}, actual={actual}");
}

AssertEqual(
    "Android OS remains mobile when imported PC payload lacks mobile feature",
    true,
    MobileReactionVisibilityPolicy.ShouldDisplay(
        hasMobileFeature: false,
        osName: "Android",
        settingEnabled: true,
        reactionAvailable: true));
AssertEqual(
    "Android OS comparison is case-insensitive",
    true,
    MobileReactionVisibilityPolicy.ShouldDisplay(false, "android", true, true));
AssertEqual(
    "mobile feature remains sufficient on supported exports",
    true,
    MobileReactionVisibilityPolicy.ShouldDisplay(true, "Linux", true, true));
AssertEqual(
    "desktop runtime stays hidden",
    false,
    MobileReactionVisibilityPolicy.ShouldDisplay(false, "Linux", true, true));
AssertEqual(
    "disabled setting stays hidden",
    false,
    MobileReactionVisibilityPolicy.ShouldDisplay(false, "Android", false, true));
AssertEqual(
    "single-player state stays hidden",
    false,
    MobileReactionVisibilityPolicy.ShouldDisplay(false, "Android", true, false));

var pointerState = new MobileReactionPointerState();
AssertEqual("pointer state starts idle", false, pointerState.HasActivePointer);
AssertEqual("first GUI press starts interaction", true, pointerState.TryBegin(3));
AssertEqual("second press cannot replace active pointer", false, pointerState.TryBegin(4));
AssertEqual("small drag remains a tap", false, pointerState.ShouldUpdateSelection(400f, 400f));
AssertEqual("drag beyond threshold selects a reaction", true, pointerState.ShouldUpdateSelection(401f, 400f));
AssertEqual("selection remains active while dragging back", true, pointerState.ShouldUpdateSelection(0f, 400f));
AssertEqual("release contract reacts after a qualifying drag", true, pointerState.HasMovedSincePress);
pointerState.Cancel();
AssertEqual("cancel clears the active pointer", false, pointerState.HasActivePointer);
AssertEqual("cancel clears the reaction decision", false, pointerState.HasMovedSincePress);

var buttonToViewport = new Matrix3x2(1.5f, 0f, 0f, 2f, 100f, 50f);
var transformedButtonCenter = MobileReactionWheelPlacement.GetTransformedCenter(
    buttonToViewport,
    new Vector2(96f, 96f));
AssertNear("button center includes canvas X transform", 172f, transformedButtonCenter.X);
AssertNear("button center includes canvas Y transform", 146f, transformedButtonCenter.Y);

var parentToViewport = new Matrix3x2(2f, 0f, 0f, 2f, 100f, 50f);
var wheelToParent = new Matrix3x2(0.75f, 0f, 0f, 0.75f, 800f, 500f);
var desiredWheelCenter = new Vector2(1700f, 1000f);
AssertEqual(
    "invertible canvas transform produces a placement correction",
    true,
    MobileReactionWheelPlacement.TryGetParentPositionAdjustment(
        parentToViewport,
        wheelToParent,
        new Vector2(500f, 500f),
        desiredWheelCenter,
        out var wheelAdjustment));
AssertNear("misplaced wheel moves left in parent coordinates", -187.5f, wheelAdjustment.X);
AssertNear("misplaced wheel moves up in parent coordinates", -212.5f, wheelAdjustment.Y);
wheelToParent.M31 += wheelAdjustment.X;
wheelToParent.M32 += wheelAdjustment.Y;
var alignedWheelCenter = MobileReactionWheelPlacement.GetTransformedCenter(
    wheelToParent * parentToViewport,
    new Vector2(500f, 500f));
AssertNear("wheel center aligns with button X in viewport", desiredWheelCenter.X, alignedWheelCenter.X);
AssertNear("wheel center aligns with button Y in viewport", desiredWheelCenter.Y, alignedWheelCenter.Y);
AssertEqual(
    "singular parent transform fails closed",
    false,
    MobileReactionWheelPlacement.TryGetParentPositionAdjustment(
        default,
        wheelToParent,
        new Vector2(500f, 500f),
        desiredWheelCenter,
        out _));

Console.WriteLine("Mobile reaction button policy, pointer-state, and viewport-placement tests passed.");
