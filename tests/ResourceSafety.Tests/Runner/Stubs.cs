using System;
using System.Collections.Generic;
using System.Reflection;

namespace Godot
{
    public class GodotObject
    {
        public bool Freed;
        public static bool IsInstanceValid(GodotObject value) => value != null && !value.Freed;
        public object Call(string name, params object[] arguments) => null;
        public object Get(string name) => null;
        public void Set(string name, object value) { }
    }
    public class Node : GodotObject
    {
        public event Action TreeEntered;
        public event Action TreeExiting;
        private bool _inside;
        private readonly List<Node> _children = new();
        public string SceneFilePath = "";
        public int TreeReads;
        public void Enter() { _inside = true; TreeEntered?.Invoke(); }
        public void Exit() { TreeExiting?.Invoke(); _inside = false; }
        public bool IsInsideTree() => _inside;
        public SceneTree GetTree() { TreeReads++; return Engine.Tree; }
        public void AddChild(Node child) => _children.Add(child);
        public int GetChildCount() => _children.Count;
        public Node GetChild(int index) => _children[index];
        public IEnumerable<Node> GetChildren() => _children;
        public T GetNodeOrNull<T>(string path) where T : Node => null;
        public Node GetNodeOrNull(string path) => null;
        public Node GetNode(string path) => null;
        public Node GetParent() => null;
        public void Connect(string name, Callable callback) { }
    }
    public class Control : Node
    {
        public enum GrowDirection { Both }
        public enum SizeFlags { ShrinkCenter }
        public Vector2 Scale = Vector2.One, Size = new(1680, 1080), Position, CustomMinimumSize;
        public float AnchorLeft, AnchorRight, AnchorTop, AnchorBottom;
        public float OffsetLeft, OffsetRight, OffsetTop, OffsetBottom;
        public GrowDirection GrowHorizontal, GrowVertical;
        public SizeFlags SizeFlagsHorizontal, SizeFlagsVertical;
    }
    public class Node2D : Node { public Vector2 Position; }
    public struct Vector2
    {
        public float X, Y;
        public Vector2(float x, float y) { X = x; Y = y; }
        public static Vector2 One => new(1, 1);
    }
    public struct Vector2I
    {
        public int X, Y;
        public Vector2I(int x, int y) { X = x; Y = y; }
    }
    public struct Rect2 { public Vector2 Size; }
    public class Window : Node
    {
        public Vector2I ContentScaleSize = new(1680, 1080);
        public Rect2 GetVisibleRect() => new() { Size = new(1680, 1080) };
    }
    public class SceneTree : Node { public Window Root = new(); }
    public static class Engine
    {
        public static SceneTree Tree = new();
        public static object GetMainLoop() => Tree;
    }
    public static class OS { public static string GetName() => "Android"; }
    public static class ProjectSettings { public static string GlobalizePath(string path) => "/nonexistent-resource-safety/ui_scale.cfg"; }
    public class Callable
    {
        private Action _action;
        public Callable(GodotObject target, string name) { }
        private Callable(Action action) { _action = action; }
        public static Callable From(Action action) => new(action);
        public void CallDeferred() => _action?.Invoke();
    }
    public class PackedScene : GodotObject
    {
        public enum GenEditState { Disabled }
        public Node Instantiate(GenEditState state) => new();
    }
    public class Material : GodotObject { }
    public class ParticleProcessMaterial : Material
    {
        public enum SubEmitterModeEnum { Disabled, Constant }
        public SubEmitterModeEnum SubEmitterMode;
    }
    public class NodePath { public bool IsEmpty = true; }
    public class GpuParticles2D : Node2D
    {
        public bool OneShot, TrailEnabled;
        public NodePath SubEmitter = new();
        public float Explosiveness;
        public double Preprocess, Lifetime, SpeedScale = 1;
        public int Amount = 6;
        public Material ProcessMaterial = new ParticleProcessMaterial();
    }
}
namespace HarmonyLib
{
    public class Harmony { }
    public static class AccessTools
    {
        public static MethodInfo Method(Type type, string name) => type?.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
        public static FieldInfo Field(Type type, string name) => type?.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
    }
}
namespace MegaCrit.Sts2.Core.Nodes { public class NGame { } }
namespace MegaCrit.Sts2.Core.Settings { public enum AspectRatioSetting { Auto, Fixed } }
namespace MegaCrit.Sts2.Core.Saves
{
    public class SaveManager
    {
        public static SaveManager Instance = new();
        public Settings SettingsSave = new();
    }
    public class Settings { public MegaCrit.Sts2.Core.Settings.AspectRatioSetting AspectRatioSetting; }
}
namespace STS2Mobile.Patches
{
    public static class PatchHelper
    {
        public static void Patch(HarmonyLib.Harmony harmony, Type type, string name, MethodInfo prefix = null, MethodInfo postfix = null) { }
        public static MethodInfo Method(Type type, string name) => HarmonyLib.AccessTools.Method(type, name);
        public static void Log(string message) { }
    }
    public static class DisplaySettingsPatches
    {
        public static string CurrentContentScaleOwner => "test";
        public static void ApplyUiScaleContentScaleSettings() { }
        public static void RequestDeferredContentScaleApply(string reason) { }
    }
}
