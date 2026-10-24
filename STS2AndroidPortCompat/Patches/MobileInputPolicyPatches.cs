using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;

namespace STS2Mobile.Patches;

/// <summary>Changes touch affordances without replacing Godot's input delivery.</summary>
public static class MobileInputPolicyPatches
{
    private const int EmulatedMouseDevice = -1;
    private const string HostName = "GodotInputSourceHost";
    private const string SourceSignal = "android_input_source";
    private const string InputSignal = "policy_input";
    private static readonly MobileInputPolicy Policy = new();
    private static bool _mobile;
    private static MobileInputSource _nativeSource;
    private static Node _observer;
    private static Node _game;
    private static GDScript _observerScript;
    private static GodotObject _host;
    private static Callable _sourceCallback;
    private static Callable _finishCallback;
    private static bool _finishQueued;
    private static bool _attachQueued;

    public static bool UseTouchControls => _mobile && Policy.UseTouchControls;

    public static void Apply(Harmony harmony)
    {
        PatchHelper.Patch(harmony, typeof(NGame), "_EnterTree", postfix: PatchHelper.Method(typeof(MobileInputPolicyPatches), nameof(GameEnterTreePostfix)));
        PatchHelper.Patch(harmony, typeof(NGame), "_Ready", postfix: PatchHelper.Method(typeof(MobileInputPolicyPatches), nameof(GameReadyPostfix)));
        PatchHelper.Patch(harmony, typeof(NGame), "_Notification", postfix: PatchHelper.Method(typeof(MobileInputPolicyPatches), nameof(GameNotificationPostfix)));
    }

    public static void GameEnterTreePostfix()
    {
        _mobile = OS.HasFeature("mobile") || OS.GetName().Equals("Android", StringComparison.OrdinalIgnoreCase);
        Policy.Reset();
    }

    public static void GameReadyPostfix()
    {
        if (!_mobile || _attachQueued || IsAlive(_observer))
            return;
        _attachQueued = true;
        Callable.From(Attach).CallDeferred();
    }

    private static void Attach()
    {
        _attachQueued = false;
        if (!_mobile || IsAlive(_observer) || NGame.Instance?.GetTree() == null)
            return;

        // A last root sibling sees input before the game's descendants and GUI.
        // This project does not generate Godot C# virtual-callback glue.
        var script = new GDScript
        {
            SourceCode = "extends Node\nsignal policy_input(event)\nfunc _input(event):\n\tpolicy_input.emit(event)\n",
        };
        if (script.Reload() != Error.Ok)
        {
            script.Dispose();
            throw new InvalidOperationException("Unable to compile the mobile input policy observer.");
        }
        var observer = new Node { Name = "AndroidInputPolicy" };
        observer.SetScript(script);
        observer.Connect(InputSignal, Callable.From<InputEvent>(ObserveInput));
        observer.TreeExiting += Detach;
        _observerScript = script;
        _observer = observer;
        _finishCallback = Callable.From(FinishEvent);
        NGame.Instance.GetTree().Root.AddChild(observer);
        _game = NGame.Instance;
        _game.TreeExiting += GameTreeExiting;

        if (Engine.HasSingleton(HostName))
        {
            _host = Engine.GetSingleton(HostName);
            _sourceCallback = Callable.From<int>(NativeSourceChanged);
            _host.Connect(SourceSignal, _sourceCallback);
            _host.Call("bindInputSource", (long)_host.GetInstanceId());
        }
        PatchHelper.Log("Automatic touch/keyboard-mouse input policy attached.");
    }

    private static void Detach()
    {
        if (IsAlive(_game))
            _game.TreeExiting -= GameTreeExiting;
        _game = null;
        if (IsAlive(_host) && _host.IsConnected(SourceSignal, _sourceCallback))
            _host.Disconnect(SourceSignal, _sourceCallback);
        _host = null;
        _observer = null;
        _observerScript?.Dispose();
        _observerScript = null;
        _finishQueued = false;
        _nativeSource = MobileInputSource.Unknown;
        Reset();
    }

    private static void GameTreeExiting()
    {
        if (IsAlive(_observer))
            _observer.Free();
        _mobile = false;
    }

    // The Android bridge calls this synchronously on the Godot main thread,
    // immediately before native dispatch. Flush before changing provenance so
    // accumulated events from different producers cannot inherit a newer source.
    public static void NativeSourceChanged(int source)
    {
        if (source < (int)MobileInputSource.Unknown || source > (int)MobileInputSource.Virtual)
            return;
        Input.FlushBufferedEvents();
        FinishEvent();
        _nativeSource = (MobileInputSource)source;
        if (_nativeSource == MobileInputSource.Unknown)
            Reset();
    }

    public static void ObserveInput(InputEvent inputEvent)
    {
        if (!_mobile || inputEvent == null)
            return;
        FinishEvent();
        var wasTouch = Policy.UseTouchControls;
        var accepted = true;
        switch (inputEvent)
        {
            case InputEventScreenTouch touch:
                accepted = Policy.ObserveTouch(touch.Index, touch.Pressed && !touch.Canceled);
                break;
            case InputEventScreenDrag drag:
                accepted = Policy.ObserveTouchDrag(drag.Index);
                break;
            case InputEventMouseButton button when _nativeSource != MobileInputSource.Virtual || IsTouchPointerEvent(button):
                var wheel = button.ButtonIndex >= MouseButton.WheelUp && button.ButtonIndex <= MouseButton.WheelRight;
                accepted = Policy.ObserveMouseButton(IsTouchPointerEvent(button), (int)button.ButtonIndex, button.Pressed, wheel);
                break;
            case InputEventMouseMotion motion when _nativeSource != MobileInputSource.Virtual || IsTouchPointerEvent(motion):
                accepted = Policy.ObserveMouseMotion(IsTouchPointerEvent(motion), motion.Relative.X, motion.Relative.Y);
                break;
            case InputEventKey key when _nativeSource == MobileInputSource.HardwareKeyboard && key.Pressed && !key.Echo:
                Policy.ObserveHardwareKey();
                break;
        }
        ApplyTransition(wasTouch);
        if (!accepted)
            _observer?.GetViewport()?.SetInputAsHandled();
        if (Policy.HasPendingRelease && !_finishQueued && IsAlive(_observer))
        {
            _finishQueued = true;
            _finishCallback.CallDeferred();
        }
    }

    public static bool IsTouchPointerEvent(InputEvent inputEvent)
    {
        return inputEvent is InputEventScreenTouch or InputEventScreenDrag
            || (inputEvent is InputEventMouseButton or InputEventMouseMotion
                && (inputEvent.Device == EmulatedMouseDevice || _nativeSource == MobileInputSource.Touch));
    }

    private static void FinishEvent()
    {
        _finishQueued = false;
        var wasTouch = Policy.UseTouchControls;
        Policy.FinishEvent();
        ApplyTransition(wasTouch);
    }

    public static void GameNotificationPostfix(int what)
    {
        if (_mobile && (what == (int)Node.NotificationApplicationPaused
            || what == (int)Node.NotificationApplicationFocusOut
            || what == (int)Node.NotificationWMWindowFocusOut))
            Reset();
    }

    public static void Reset()
    {
        Policy.Reset();
        _finishQueued = false;
        ResetConsumers();
    }

    private static void ApplyTransition(bool wasTouch)
    {
        if (wasTouch == Policy.UseTouchControls)
            return;
        ResetConsumers();
        PatchHelper.Log(Policy.UseTouchControls ? "Input policy: touch." : "Input policy: keyboard-mouse.");
    }

    private static void ResetConsumers()
    {
        TouchInputPatches.ResetForInputModeChange();
        MobileTapPreviewPatches.ResetForInputModeChange();
        MobileSelectionConfirmationPatches.ResetForInputModeChange();
        MerchantSelectionConfirmationPatches.ResetForInputModeChange();
        MobileTooltipPatches.ResetForInputModeChange();
        AndroidInputCompatPatches.ResetForInputModeChange();
    }

    private static bool IsAlive(GodotObject value) => value != null && GodotObject.IsInstanceValid(value);
}
