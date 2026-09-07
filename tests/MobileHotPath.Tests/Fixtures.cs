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
        public Rect2 GetGlobalRect() => new() { Size = new Vector2(100, 100) };
    }
    public class Viewport { public static readonly Viewport Shared = new(); public Vector2 GetMousePosition() => Vector2.Zero; }
    public class SceneTree { public static readonly SceneTree Shared = new(); public SceneTreeTimer CreateTimer(double delay) => new(); }
    public class SceneTreeTimer : Node { public new static class SignalName { public const string Timeout = "timeout"; } }
    public class Callable
    {
        private readonly Action _action;
        private readonly Action<InputEvent> _input;
        private Callable(Action action) { _action = action; }
        private Callable(Action<InputEvent> input) { _input = input; }
        public static Callable From(Action action) => new(action);
        public static Callable From<T>(Action<T> action) => new(value => action((T)(object)value));
        public void Invoke(object value) { if (_action != null) _action(); else _input((InputEvent)value); }
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
    public struct Rect2 { public Vector2 Size; public bool HasPoint(Vector2 point) => point.X >= 0 && point.Y >= 0 && point.X <= Size.X && point.Y <= Size.Y; }
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
    public static class OS { public static bool MobileFeature; public static bool HasFeature(string feature) => MobileFeature; public static string GetName() => "Android"; }
    public class Texture2D : GodotObject { public string Path; }
    public class Sprite2D : Node { public Texture2D Texture; }
    public class InputEvent { }
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
        [MethodImpl(MethodImplOptions.NoInlining)] public void _Input(InputEvent inputEvent) { }
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
    public class NTargetManager
    {
        public static NTargetManager Instance = new();
        private string _targetMode = "ReleaseMouseToTarget";
        public Node HoveredNode { get; set; }
        public bool Cancelled;
        private void FinishTargeting(bool cancel) { Cancelled = cancel; }
        [MethodImpl(MethodImplOptions.NoInlining)] public void StartTargeting() { }
        [MethodImpl(MethodImplOptions.NoInlining)] public void _Input(InputEvent inputEvent) { }
    }
}
namespace MegaCrit.Sts2.Core.Nodes.Cards.Holders { public class NHandCardHolder { } }
namespace STS2Mobile.Android
{
    public static class AndroidSettingsBridge
    {
        public static string Mode = "immediate";
        public static int Reads;
        public static string GetString(string key, string fallback) { Reads++; return Mode; }
        public static int GetInt(string key, int fallback) { Reads++; return fallback; }
        public static bool GetBool(string key, bool fallback) { Reads++; return fallback; }
        public static void InvalidateCache() { }
    }
}
namespace STS2Mobile.Patches
{
    public static class MobileTapPreviewPatches { public static void RepinAfterCancelledCardPlay(object holder) { } }
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
