using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Saves;

// Minimal API shapes, not copied game code. No frames are advanced: this models
// Android suspending rendering immediately after delivering lifecycle notifications.
namespace Godot
{
    public class Node
    {
        public const int NotificationWMWindowFocusIn = 1004;
        public const int NotificationWMWindowFocusOut = 1005;
        public const int NotificationApplicationResumed = 2014;
        public const int NotificationApplicationPaused = 2015;
        public const int NotificationApplicationFocusIn = 2016;
        public const int NotificationApplicationFocusOut = 2017;
    }
    public class Tween { public void Kill() { } }
    public static class OS
    {
        public static string Platform = "Android";
        public static string GetName() => Platform;
    }
    public static class Engine
    {
        public static FmodServer GetSingleton(string name) => name == "FmodServer" ? new FmodServer() : null;
    }
    public class FmodServer
    {
        public void Call(string method)
        {
            if (method != "update") throw new MissingMethodException(method);
            var output = MegaCrit.Sts2.Core.Nodes.NGame.Instance.AudioManager;
            output.Gain = output.PendingGain;
        }
    }
}
namespace MegaCrit.Sts2.Core.Saves
{
    public class Prefs { public bool MuteInBackground = true; }
    public class Settings { public float VolumeMaster = 0.6f; }
    public class SaveManager
    {
        public static SaveManager Instance = new();
        public Prefs PrefsSave = new();
        public Settings SettingsSave = new();
    }
}
namespace MegaCrit.Sts2.Core.Nodes
{
    public class AudioOutput
    {
        public float Gain = 0.6f;
        public float PendingGain = 0.6f;
        public void SetMasterVol(float gain) => PendingGain = gain;
        public void SetMasterAudioVolume(float gain) => Gain = gain;
    }
    public class NGame : Node
    {
        public static NGame Instance = new();
        public AudioOutput AudioManager = new();
        public AudioOutput DebugAudio = new();
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void _Ready() { }
    }
    public class NMuteInBackgroundHandler : Node
    {
        private Tween _tween;
        public int OtherNotifications;
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void _Notification(int what)
        {
            if (what == NotificationWMWindowFocusOut && SaveManager.Instance.PrefsSave.MuteInBackground)
                _tween = new Tween(); // A pending fade has not changed the output yet.
            else if (what == NotificationWMWindowFocusIn && _tween != null)
            {
                _tween.Kill();
                _tween = null;
                NGame.Instance.AudioManager.SetMasterVol(SaveManager.Instance.SettingsSave.VolumeMaster);
                NGame.Instance.DebugAudio.SetMasterAudioVolume(SaveManager.Instance.SettingsSave.VolumeMaster);
            }
            else
                OtherNotifications++;
        }
    }
}
namespace STS2Mobile
{
    public static class PatchHelper
    {
        public static void Log(string message) => Console.WriteLine(message);
        public static MethodInfo Method(Type type, string name) => AccessTools.Method(type, name);
        public static void Patch(Harmony harmony, Type type, string name, MethodInfo postfix) =>
            harmony.Patch(AccessTools.Method(type, name), postfix: new HarmonyMethod(postfix));
    }
}
