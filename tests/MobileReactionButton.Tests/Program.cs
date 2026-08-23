using STS2Mobile.Patches;

static void AssertEqual(string name, bool expected, bool actual)
{
    if (expected != actual)
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

Console.WriteLine("Mobile reaction button policy and pointer-state tests passed.");
