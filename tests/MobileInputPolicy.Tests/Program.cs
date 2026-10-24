using STS2Mobile.Patches;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

var policy = new MobileInputPolicy();
Require(policy.ObserveTouch(0, true), "A finger must acquire the shared pointer.");
policy.ObserveHardwareKey();
Require(policy.UseTouchControls && !policy.RequestedTouchControls, "A keyboard shortcut must not change an in-flight touch release rule.");
Require(policy.ObserveTouch(0, false) && policy.UseTouchControls, "The final release must retain touch rules throughout viewport delivery.");
policy.FinishEvent();
Require(!policy.UseTouchControls && !policy.HasActiveGesture, "The pending keyboard policy must apply after the touch gesture ends.");

Require(policy.ObserveTouch(1, true), "A fresh finger must switch back without reconnecting peripherals.");
Require(policy.ObserveMouseButton(true, 1, true, false), "The engine's emulated mouse belongs to the same touch gesture.");
Require(!policy.ObserveMouseMotion(false, 6, 0), "A competing mouse must not move a finger's card or steal its hover target.");
Require(policy.ObserveTouch(1, false), "The touch contact must receive its own release.");
policy.FinishEvent();
Require(policy.UseTouchControls, "The engine's matching emulated release is still part of this gesture.");
Require(policy.ObserveMouseButton(true, 1, false, false), "The emulated release must not be mistaken for a hardware release.");
Require(policy.UseTouchControls, "Release callbacks still need touch semantics.");
policy.FinishEvent();
Require(!policy.UseTouchControls, "A real mouse request must survive touch emulation without mode oscillation.");

Require(policy.ObserveMouseButton(false, 1, true, false), "A real mouse drag must acquire the pointer.");
Require(!policy.ObserveTouch(2, true) && !policy.ObserveTouchDrag(2), "A competing finger cannot hijack a mouse drag.");
Require(!policy.ObserveMouseButton(true, 1, true, false), "The competing finger's emulated mouse press must also be swallowed.");
Require(policy.ObserveMouseButton(false, 1, false, false), "Mouse up must reach the original drag.");
Require(!policy.UseTouchControls, "The mouse release cannot be reinterpreted as a touch confirmation.");
policy.FinishEvent();
Require(policy.UseTouchControls, "The finger's request takes effect only after the mouse drag ends.");
Require(!policy.ObserveTouch(2, false), "The residual finger release cannot click a newly switched UI.");
Require(!policy.ObserveMouseButton(true, 1, false, false), "The residual emulated release cannot complete an unrelated gesture.");

Require(policy.ObserveTouch(3, true), "A new touch gesture must work after the swallowed stream ends.");
Require(!policy.ObserveMouseButton(false, 3, true, false), "Middle-click cannot take over the touch gesture.");
Require(policy.ObserveTouch(3, false), "The original finger still releases normally.");
policy.FinishEvent();
Require(!policy.UseTouchControls, "A pending real mouse button is a meaningful switch request.");
Require(!policy.ObserveMouseMotion(false, 20, 0), "A mouse button whose down was swallowed cannot drag after the owner ends.");
Require(!policy.ObserveMouseButton(false, 3, false, false), "Its unmatched up must remain swallowed.");
Require(policy.ObserveMouseButton(false, 3, true, false), "The next complete middle-click must work.");
Require(policy.ObserveMouseButton(false, 3, false, false), "Its matching release must work.");
policy.FinishEvent();

Require(policy.ObserveTouch(4, true) && policy.ObserveTouch(5, true), "Multiple fingers share touch ownership.");
policy.ObserveHardwareKey();
Require(policy.ObserveTouch(4, false), "The first finger can leave.");
policy.FinishEvent();
Require(policy.UseTouchControls, "A remaining finger must keep the touch gesture locked.");
Require(policy.ObserveTouch(5, false), "The last finger can leave.");
policy.FinishEvent();
Require(!policy.UseTouchControls, "All fingers must be up before a pending keyboard switch commits.");

Require(policy.ObserveTouch(6, true) && policy.ObserveTouch(6, false), "Touch can reacquire the pointer.");
policy.FinishEvent();
for (var i = 0; i < 100; i++)
{
    Require(!policy.ObserveMouseMotion(false, 1, 0), "Subthreshold mouse jitter must not steal touch mode.");
    Require(!policy.ObserveMouseMotion(false, -1, 0), "Opposite jitter must not accumulate absolute travel forever.");
}
Require(policy.UseTouchControls, "Stationary pointer jitter must retain touch policy.");
Require(policy.ObserveMouseMotion(false, 4, 0) && !policy.UseTouchControls, "A deliberate mouse movement must switch immediately at the threshold.");
Require(policy.ObserveTouch(7, true) && policy.ObserveTouch(7, false), "Touch must remain available after moving the mouse.");
policy.FinishEvent();
Require(!policy.ObserveMouseMotion(false, 1000, 0) && policy.UseTouchControls, "A pointer warp must not masquerade as deliberate hardware movement.");
Require(policy.ObserveMouseButton(false, 4, true, true) && !policy.UseTouchControls, "A wheel tick must switch without requiring a click first.");
Require(!policy.HasActiveGesture, "Wheel ticks cannot leave a pointer lease held.");

Require(policy.ObserveMouseButton(false, 1, true, false), "A mouse drag starts.");
Require(policy.ObserveMouseButton(false, 2, true, false), "A second real mouse button can join the same gesture.");
Require(!policy.ObserveTouch(8, true), "Touch cannot steal a multibutton drag.");
Require(policy.ObserveMouseButton(false, 1, false, false), "The first real button releases.");
policy.FinishEvent();
Require(!policy.UseTouchControls && policy.HasActiveGesture, "A second held button must keep ownership.");
Require(policy.ObserveMouseButton(false, 2, false, false), "The final real button releases.");
policy.FinishEvent();
Require(policy.UseTouchControls, "The pending touch switch commits after all real buttons release.");
policy.Reset();
Require(!policy.ObserveTouch(8, false), "A stale release after pause/reset cannot click anything.");
Require(policy.ObserveMouseButton(false, 1, true, false), "A fresh mouse press must work after pause/reset.");
Require(!policy.UseTouchControls, "Reset cannot strand the policy behind stale touch ownership.");

Console.WriteLine("PASS: automatic input switching, complete touch/emulated/mouse gesture ownership, competing stream suppression, movement thresholds and lifecycle reset.");
