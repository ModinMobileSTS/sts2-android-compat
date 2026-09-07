using System;
using System.Reflection;
using Godot;
using HarmonyLib;

// Synthetic STS2/settings shapes; scene lifecycle and ResourceLoader are real Godot.
namespace MegaCrit.Sts2.Core.Nodes
{
    public partial class NGame : Node
    {
        public override void _Ready() { }
    }
}
namespace STS2Mobile.Android
{
    public static class AndroidSettingsBridge
    {
        public static bool Enabled = true;
        public static bool GetBool(string key, bool fallback) => key == "shader_compatibility_mode" ? Enabled : fallback;
    }
}
namespace STS2Mobile
{
    public static class PatchHelper
    {
        public static void Log(string message) => GD.Print(message);
        public static MethodInfo Method(Type type, string name) => AccessTools.Method(type, name);
        public static void Patch(Harmony harmony, Type type, string name, MethodInfo prefix = null, MethodInfo postfix = null) =>
            harmony.Patch(AccessTools.Method(type, name), prefix: prefix == null ? null : new HarmonyMethod(prefix), postfix: postfix == null ? null : new HarmonyMethod(postfix));
    }
}
