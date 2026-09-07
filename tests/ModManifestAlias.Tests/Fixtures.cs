using System;
using System.Collections.Generic;
using System.Reflection;

// Compile-only API shapes. Any accidental gameplay/patch initialization fails the fixture.
namespace HarmonyLib
{
    public sealed class Harmony { public void Patch(MethodInfo target, HarmonyMethod prefix) => throw new NotSupportedException(); }
    public sealed class HarmonyMethod { public HarmonyMethod(MethodInfo method) => throw new NotSupportedException(); }
}
namespace MegaCrit.Sts2.Core.Debug { }
namespace MegaCrit.Sts2.Core.Modding
{
    public interface IModManagerFileIo { bool DirectoryExists(string path); }
    public enum ModSource { ModsDirectory }
    public class ModManifest { public string id; }
    public class Mod { public ModManifest manifest; }
    public class ModSettings { public List<SettingsSaveMod> ModList; }
    public class SettingsSaveMod
    {
        public string Id;
        public bool IsEnabled;
        public SettingsSaveMod(Mod mod) => throw new NotSupportedException();
    }
    public static class ModManager
    {
        public static bool IsRunningModded() => throw new NotSupportedException();
        public static IEnumerable<Mod> GetLoadedMods() => throw new NotSupportedException();
    }
}
namespace STS2Mobile.Android { public static class AppPaths { public static string ModsDir; } }
namespace STS2Mobile.Patches
{
    public static class PatchHelper
    {
        public static void Log(string message) => Console.WriteLine(message);
        public static MethodInfo Method(Type type, string name) => throw new NotSupportedException();
    }
    public static class ModelDbInitPatch
    {
        public static void EnsureVanillaModelPlaceholdersPreRegistered() => throw new NotSupportedException();
        public static IDisposable BeginModInitializationContainsShield() => throw new NotSupportedException();
    }
    public static class DeferredModPatchQueue
    {
        public static IDisposable BeginModInitialization(string id) => throw new NotSupportedException();
    }
}
