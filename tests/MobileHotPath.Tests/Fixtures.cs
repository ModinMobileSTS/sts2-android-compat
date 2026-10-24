using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;

// Synthetic API shapes only. Clocks, scene lifecycle and resources are deterministic;
// the production patches are installed with the actual packaged Harmony runtime.
namespace Godot
{
    public class GodotObject
    {
        private static ulong _nextId;
        private readonly ulong _id = ++_nextId;
        public bool Valid = true;
        public ulong GetInstanceId() => _id;
        public static bool IsInstanceValid(GodotObject value) => value != null && value.Valid;
    }
    public class Node : GodotObject
    {
        public static class SignalName { public const string TreeExiting = "exit"; }
        private readonly Dictionary<string, List<Callable>> _signals = new();
        private readonly List<Node> _children = new();
        private Node _parent;
        public string Name;
        public int GetIndex() => _parent?._children.IndexOf(this) ?? 0;
        public T GetNodeOrNull<T>(string name) where T : Node
        {
            foreach (var child in _children)
                if (child is T typed && child.Name == name.TrimStart('%')) return typed;
            return null;
        }
        public void Connect(string signal, Callable callback)
        {
            if (!_signals.TryGetValue(signal, out var callbacks)) _signals[signal] = callbacks = new();
            callbacks.Add(callback);
        }
        public void Emit(string signal, object value = null)
        {
            if (_signals.TryGetValue(signal, out var callbacks))
                foreach (var callback in callbacks.ToArray()) callback.Invoke(value);
        }
        public Node GetParent() => _parent;
        public void AddChild(Node child) { child._parent = this; _children.Add(child); }
        public void Exit()
        {
            Emit(SignalName.TreeExiting);
            foreach (var child in _children.ToArray()) child.Exit();
        }
        public void Reparent(Node parent) { Exit(); _parent?._children.Remove(this); parent.AddChild(this); }
        public void Free() { Exit(); _parent?._children.Remove(this); Valid = false; }
        public IEnumerable<Node> GetChildren() => _children;
        public bool IsInsideTree() => Valid;
        public SceneTree GetTree() => SceneTree.Shared;
        public Viewport GetViewport() => Viewport.Shared;
        public Tween CreateTween() => new();
    }
    public class Control : Node
    {
        public new static class SignalName { public const string GuiInput = "input"; public const string MouseExited = "mouse-exited"; }
        private bool _visible = true;
        public int VisibilityWrites;
        public bool Visible { get => _visible; set { _visible = value; VisibilityWrites++; } }
        public Vector2 Position;
        public bool HasFocus;
        public void ReleaseFocus() { HasFocus = false; }
        public bool TryGrabFocus() { HasFocus = true; return true; }
        public Rect2 GetGlobalRect() => new() { Position = Position, Size = new Vector2(100, 100) };
    }
    public class Viewport
    {
        public static readonly Viewport Shared = new();
        public Vector2 MousePosition;
        public int HandledInputs;
        public Vector2 GetMousePosition() => MousePosition;
        public Rect2 GetVisibleRect() => new() { Size = new Vector2(1000, 1000) };
        public void SetInputAsHandled() { HandledInputs++; }
    }
    public class SceneTree
    {
        public static readonly SceneTree Shared = new();
        public readonly List<SceneTreeTimer> Timers = new();
        public SceneTreeTimer CreateTimer(double delay)
        {
            var timer = new SceneTreeTimer();
            Timers.Add(timer);
            return timer;
        }
    }
    public class SceneTreeTimer : Node { public new static class SignalName { public const string Timeout = "timeout"; } }
    public class Callable
    {
        private readonly Action<object> _action;
        private Callable(Action<object> action) { _action = action; }
        public static Callable From(Action action) => new(_ => action());
        public static Callable From<T>(Action<T> action) => new(value => action((T)value));
        public void Invoke(object value) { _action(value); }
    }
    public class Tween
    {
        public enum EaseType { InOut }
        public enum TransitionType { Sine }
        public bool Killed;
        public void Kill() { Killed = true; }
        public Tween SetLoops() => this;
        public Tween TweenProperty(object target, string property, float value, double duration) => this;
        public Tween SetEase(EaseType ease) => this;
        public Tween SetTrans(TransitionType transition) => this;
        public void TweenCallback(Callable callback) { }
    }
    public struct Vector2
    {
        public float X, Y;
        public Vector2(float x, float y) { X = x; Y = y; }
        public static Vector2 Zero => default;
        public static Vector2 operator -(Vector2 a, Vector2 b) => new(a.X - b.X, a.Y - b.Y);
        public float LengthSquared() => X * X + Y * Y;
    }
    public struct Rect2
    {
        public Vector2 Position, Size;
        public bool HasPoint(Vector2 point) => point.X >= Position.X && point.Y >= Position.Y
            && point.X <= Position.X + Size.X && point.Y <= Position.Y + Size.Y;
    }
    public static class Mathf
    {
        public const float Pi = MathF.PI, Tau = MathF.Tau;
        public static float Sin(float value) => MathF.Sin(value);
        public static float PosMod(float value, float modulus) => (value % modulus + modulus) % modulus;
    }
    public static class Time
    {
        public static ulong Msec = 100;
        public static ulong GetTicksMsec() => Msec;
        public static ulong GetTicksUsec() => Msec * 1000;
    }
    public static class OS
    {
        public static bool MobileFeature;
        public static string Platform = "Android";
        public static bool HasFeature(string feature) => MobileFeature;
        public static string GetName() => Platform;
    }
    public sealed class Image : GodotObject { }
    public static class Input
    {
        public enum MouseModeEnum { Visible, Hidden, Captured }
        public enum CursorShape { Arrow }
        private static MouseModeEnum _mouseMode;
        public static bool PointerVisible;
        public static Image CursorImage;
        public static Vector2 CursorHotSpot;
        public static MouseModeEnum MouseMode
        {
            get => _mouseMode;
            set
            {
                _mouseMode = value;
                if (value != MouseModeEnum.Visible) PointerVisible = false;
                // Android 4.5.1 keeps TYPE_NULL on a same-shape Visible restore.
                else if (OS.Platform != "Android") PointerVisible = true;
            }
        }
        public static void SetCustomMouseCursor(Image image, CursorShape shape, Vector2 hotSpot)
        {
            CursorImage = image;
            CursorHotSpot = hotSpot;
            PointerVisible = MouseMode == MouseModeEnum.Visible;
        }
    }
    public class Texture2D : GodotObject { public string Path; }
    public class Sprite2D : Node { public Texture2D Texture; }
    public class InputEvent
    {
        public int Device;
        public bool NativeTouchSource;
        public bool SelectPressed;
        public bool IsActionPressed(string action) => SelectPressed;
    }
    public enum MouseButton { Left, Right }
    public class InputEventMouseButton : InputEvent { public MouseButton ButtonIndex; public bool Pressed; public Vector2 Position; }
    public class InputEventScreenTouch : InputEvent { public bool Pressed; public Vector2 Position; }
    public class InputEventMouseMotion : InputEvent { public Vector2 Position; }
    public class InputEventScreenDrag : InputEvent { public Vector2 Position; }
}
namespace MegaCrit.Sts2.Core.Nodes
{
    public class NGame : Node
    {
        public static NGame Instance = new();
        public Node HoverTipsContainer = new();
        public CommonUi.NCursorManager CursorManager = new();
        public int OriginalInputs;
        [MethodImpl(MethodImplOptions.NoInlining)] public void _Input(InputEvent inputEvent) { OriginalInputs++; }
    }
    public class NInspectCardScreen : Control { }
}
namespace MegaCrit.Sts2.Core.Nodes.HoverTips
{
    public class NHoverTipSet : Control
    {
        private static readonly Dictionary<Control, NHoverTipSet> _activeHoverTips = new();
        private Control _owner;
        public int FollowFrames;
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static NHoverTipSet CreateAndShow(Control owner)
        {
            var tip = new NHoverTipSet { _owner = owner };
            _activeHoverTips[owner] = tip;
            MegaCrit.Sts2.Core.Nodes.NGame.Instance.HoverTipsContainer.AddChild(tip);
            return tip;
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Remove(Control owner)
        {
            if (_activeHoverTips.Remove(owner, out var tip)) tip.Free();
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Clear()
        {
            foreach (var owner in new List<Control>(_activeHoverTips.Keys)) Remove(owner);
        }
        [MethodImpl(MethodImplOptions.NoInlining)] public void _Process(double delta) { FollowFrames++; }
    }
}
namespace MegaCrit.Sts2.Core.Assets
{
    public static class PreloadManager { public static readonly AssetCache Cache = new(); }
    public class AssetCache
    {
        private readonly Dictionary<string, Texture2D> _textures = new();
        public int Lookups;
        public Texture2D GetTexture2D(string path)
        {
            Lookups++;
            if (!_textures.TryGetValue(path, out var texture)) _textures[path] = texture = new Texture2D { Path = path };
            return texture;
        }
    }
}
namespace MegaCrit.Sts2.Core.Entities.Intents
{
    public static class IntentAnimData
    {
        public static int GetAnimationFrameCount(string name) => 3;
        public static string GetAnimationFrame(string name, int frame) => name + frame;
    }
}
namespace MegaCrit.Sts2.Core.Nodes.Combat
{
    public class NIntent : Control
    {
        private Control _intentHolder = new();
        private Sprite2D _intentSprite = new();
        private float _timeOffset = 0;
        private string _animationName;
#if MODERN_INTENT
        private readonly List<Texture2D> _animationFrames = new();
#endif
        public Texture2D Shown => _intentSprite.Texture;
        [MethodImpl(MethodImplOptions.NoInlining)] public void _Ready() { }
        [MethodImpl(MethodImplOptions.NoInlining)] public void _ExitTree() { }
        [MethodImpl(MethodImplOptions.NoInlining)] public void UpdateIntent(string name) { ChangeVisuals(name); }
        // Models combat-state changes independently of UpdateIntent.
        public void ChangeVisuals(string name)
        {
            _animationName = name;
#if MODERN_INTENT
            _animationFrames.Clear();
            for (int i = 0; i < 3; i++) _animationFrames.Add(MegaCrit.Sts2.Core.Assets.PreloadManager.Cache.GetTexture2D(name + i));
#endif
        }
        [MethodImpl(MethodImplOptions.NoInlining)] public void _Process(double delta) { }
    }
    public class NMouseCardPlay
    {
        public bool InPlayZone;
        public bool Cancelled;
        public object Holder;
        private bool IsCardInPlayZone() => InPlayZone;
        public void CancelPlayCard() { Cancelled = true; }
        [MethodImpl(MethodImplOptions.NoInlining)] public void _Input(InputEvent inputEvent) { }
        [MethodImpl(MethodImplOptions.NoInlining)] public void Start() { }
        [MethodImpl(MethodImplOptions.NoInlining)] public void OnCancelPlayCard() { }
    }
    public class NTargetingArrow : Node
    {
        private bool _followMouse;
        public bool RestoreOnFinish = true;
        public void StartDrawingFrom(bool usingController)
        {
            _followMouse = !usingController;
            Input.MouseMode = Input.MouseModeEnum.Hidden;
        }
        [MethodImpl(MethodImplOptions.NoInlining)] public void StopDrawing()
        {
            if (_followMouse && RestoreOnFinish) Input.MouseMode = Input.MouseModeEnum.Visible;
        }
        [MethodImpl(MethodImplOptions.NoInlining)] public void _ExitTree()
        {
            if (_followMouse && RestoreOnFinish) Input.MouseMode = Input.MouseModeEnum.Visible;
        }
    }
    public class NTargetManager
    {
        public static NTargetManager Instance = new();
        private string _targetMode = "ReleaseMouseToTarget";
        public Node HoveredNode { get; set; }
        public bool Cancelled;
        public NTargetingArrow TargetingArrow;
        private void FinishTargeting(bool cancel) { Cancelled = cancel; TargetingArrow?.StopDrawing(); }
        public void CompleteTargeting() => FinishTargeting(false);
        public void CancelTargeting() => FinishTargeting(true);
        [MethodImpl(MethodImplOptions.NoInlining)] public void StartTargeting() { TargetingArrow?.StartDrawingFrom(false); }
        [MethodImpl(MethodImplOptions.NoInlining)] public void _Input(InputEvent inputEvent) { }
        public void OnNodeHovered(Node node) { HoveredNode = node; }
    }
}
namespace MegaCrit.Sts2.Core.Nodes.CommonUi
{
    public class NCursorManager : Node
    {
        private Image _lastSetCursor;
        private bool _isUsingController;
        private bool _shouldShowCursor = true;
        private Image _image;
        private Vector2 _hotSpot;
        public void SetAppearance(Image image, Vector2 hotSpot)
        {
            _image = image;
            _hotSpot = hotSpot;
            UpdateCursor();
        }
        public void UseController() { _isUsingController = true; Input.MouseMode = Input.MouseModeEnum.Hidden; }
        public void HideCursor() { _shouldShowCursor = false; Input.MouseMode = Input.MouseModeEnum.Hidden; }
        private void UpdateCursor()
        {
            if (Input.MouseMode != Input.MouseModeEnum.Hidden && _lastSetCursor != _image)
            {
                Input.SetCustomMouseCursor(_image, Input.CursorShape.Arrow, _hotSpot);
                _lastSetCursor = _image;
            }
        }
    }
}
namespace STS2Mobile.Android
{
    public static class AndroidSettingsBridge
    {
        public static string Mode = "immediate";
        public static string RetapAction = "put_down";
        public static bool TouchPreviewEnabled = true;
        public static bool SelectionConfirmationEnabled = true;
        public static int Reads;
        public static string GetString(string key, string fallback) { Reads++; return key == "touch_lift_retap_action" ? RetapAction : Mode; }
        public static int GetInt(string key, int fallback) { Reads++; return fallback; }
        public static bool GetBool(string key, bool fallback)
        {
            Reads++;
            return key switch
            {
                "touch_lift_preview" => TouchPreviewEnabled,
                "mobile_selection_confirmation" => SelectionConfirmationEnabled,
                _ => fallback,
            };
        }
        public static void InvalidateCache() { }
    }
}
namespace STS2Mobile.Patches
{
    // Consumer-only policy dependency: drive these gates independently of the
    // production state machine, including hardware input during touch ownership.
    public static class MobileInputPolicyPatches
    {
        public static bool TouchControls = true;
        public static bool UseTouchControls => OS.MobileFeature && TouchControls;
        public static bool IsTouchPointerEvent(InputEvent inputEvent) => inputEvent is InputEventScreenTouch or InputEventScreenDrag
            || (inputEvent is InputEventMouseButton or InputEventMouseMotion && (inputEvent.Device == -1 || inputEvent.NativeTouchSource));
    }
}
namespace STS2Mobile
{
    public static class PatchHelper
    {
        public static void Log(string message) => Console.WriteLine(message);
        public static MethodInfo Method(Type type, string name) => AccessTools.Method(type, name);
        public static void Patch(Harmony harmony, Type type, string name, MethodInfo prefix = null, MethodInfo postfix = null) =>
            harmony.Patch(AccessTools.Method(type, name), prefix: prefix == null ? null : new HarmonyMethod(prefix), postfix: postfix == null ? null : new HarmonyMethod(postfix));
    }
}
