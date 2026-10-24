using System.Collections.Generic;

namespace STS2Mobile.Patches;

// Values are shared with the staged Android input-source bridge.
public enum MobileInputSource
{
    Unknown = 0,
    Touch = 1,
    HardwareMouse = 2,
    HardwareKeyboard = 3,
    Virtual = 4,
}

/// <summary>Event-driven policy for the game's single shared pointer.</summary>
public sealed class MobileInputPolicy
{
    private const float MouseSwitchDistanceSquared = 16f;
    private const float MaximumMouseDeltaSquared = 250000f;
    private readonly HashSet<int> _touches = new(10);
    private readonly HashSet<int> _suppressedTouches = new(10);
    private uint _touchMouseButtons;
    private uint _hardwareButtons;
    private uint _suppressedTouchMouseButtons;
    private uint _suppressedHardwareButtons;
    private PointerOwner _owner;
    private bool _releasePending;
    private float _mouseTravelX;
    private float _mouseTravelY;

    public bool UseTouchControls { get; private set; } = true;
    public bool RequestedTouchControls { get; private set; } = true;
    public bool HasActiveGesture => _owner != PointerOwner.None;
    public bool HasPendingRelease => _releasePending;

    public bool ObserveTouch(int index, bool pressed)
    {
        if (pressed)
        {
            Request(touch: true);
            _mouseTravelX = _mouseTravelY = 0f;
            if (_owner == PointerOwner.Mouse || _suppressedTouches.Count != 0)
            {
                _suppressedTouches.Add(index);
                return false;
            }
            _touches.Add(index);
            _owner = PointerOwner.Touch;
            _releasePending = false;
            return true;
        }
        if (_suppressedTouches.Remove(index))
            return false;
        if (!_touches.Remove(index))
            return false;
        MarkRelease();
        return true;
    }

    public bool ObserveTouchDrag(int index) => _touches.Contains(index);

    public bool ObserveMouseButton(bool touch, int button, bool pressed, bool wheel)
    {
        if (wheel)
        {
            if (pressed)
                Request(touch);
            return _owner == PointerOwner.None || (_owner == PointerOwner.Touch) == touch;
        }
        if (button < 1 || button > 32)
            return false;
        var bit = 1u << (button - 1);
        ref var suppressed = ref (touch ? ref _suppressedTouchMouseButtons : ref _suppressedHardwareButtons);
        ref var buttons = ref (touch ? ref _touchMouseButtons : ref _hardwareButtons);
        if (pressed)
        {
            Request(touch);
            if (suppressed != 0 || (touch && _suppressedTouches.Count != 0)
                || (_owner != PointerOwner.None && (_owner == PointerOwner.Touch) != touch))
            {
                suppressed |= bit;
                return false;
            }
            buttons |= bit;
            _owner = touch ? PointerOwner.Touch : PointerOwner.Mouse;
            _releasePending = false;
            return true;
        }
        if ((suppressed & bit) != 0)
        {
            suppressed &= ~bit;
            return false;
        }
        if ((buttons & bit) == 0)
            return false;
        buttons &= ~bit;
        MarkRelease();
        return true;
    }

    public bool ObserveMouseMotion(bool touch, float relativeX, float relativeY)
    {
        if (touch)
        {
            return _owner != PointerOwner.Mouse && _suppressedTouchMouseButtons == 0
                && _suppressedTouches.Count == 0;
        }
        if (UseTouchControls)
        {
            var distanceSquared = relativeX * relativeX + relativeY * relativeY;
            if (distanceSquared > 0f && distanceSquared <= MaximumMouseDeltaSquared)
            {
                _mouseTravelX += relativeX;
                _mouseTravelY += relativeY;
                if (_mouseTravelX * _mouseTravelX + _mouseTravelY * _mouseTravelY >= MouseSwitchDistanceSquared)
                {
                    Request(touch: false);
                    _mouseTravelX = _mouseTravelY = 0f;
                }
            }
        }
        return _owner != PointerOwner.Touch && _suppressedHardwareButtons == 0 && !UseTouchControls;
    }

    public void ObserveHardwareKey() => Request(touch: false);

    // Called after the complete viewport event, not in the key/mouse release prefix.
    public void FinishEvent()
    {
        if (!_releasePending)
            return;
        _releasePending = false;
        _owner = PointerOwner.None;
        UseTouchControls = RequestedTouchControls;
    }

    public void Reset()
    {
        _touches.Clear();
        _suppressedTouches.Clear();
        _touchMouseButtons = _hardwareButtons = 0;
        _suppressedTouchMouseButtons = _suppressedHardwareButtons = 0;
        _owner = PointerOwner.None;
        _releasePending = false;
        _mouseTravelX = _mouseTravelY = 0f;
        RequestedTouchControls = UseTouchControls;
    }

    private void Request(bool touch)
    {
        RequestedTouchControls = touch;
        if (_owner == PointerOwner.None)
            UseTouchControls = touch;
    }

    private void MarkRelease()
    {
        if (_touches.Count == 0 && _touchMouseButtons == 0 && _hardwareButtons == 0)
            _releasePending = true;
    }

    private enum PointerOwner { None, Touch, Mouse }
}
