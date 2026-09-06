using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Saves;
using STS2Mobile.Patches;

internal static class Program
{
    private static void Main(string[] args)
    {
        if (Array.IndexOf(args, "--unpatched") < 0)
            AndroidAudioLifecyclePatches.Apply(new Harmony("tests.android.background-audio"));
        NGame.Instance._Ready();
        NGame.Instance._Ready();

        var handler = Reset();
        handler._Notification(Node.NotificationWMWindowFocusOut);
        handler._Notification(Node.NotificationApplicationPaused);
        Gain(0f, "background must be silent without advancing a tween frame");
        handler._Notification(Node.NotificationWMWindowFocusIn);
        Gain(0f, "focus arriving before resume must not leak background audio");
        SaveManager.Instance.SettingsSave.VolumeMaster = 0.35f;
        handler._Notification(Node.NotificationApplicationResumed);
        Gain(0.35f, "resume restores current preference, not cached launch volume");
        handler._Notification(Node.NotificationApplicationResumed);
        Gain(0.35f, "duplicate resume preserves volume");

        handler = Reset();
        handler._Notification(Node.NotificationApplicationFocusOut);
        handler._Notification(Node.NotificationApplicationPaused);
        handler._Notification(Node.NotificationApplicationResumed);
        Gain(0f, "resume before application focus must stay muted");
        handler._Notification(Node.NotificationApplicationFocusIn);
        Gain(0.6f, "application focus completes restoration");

        handler = Reset();
        handler._Notification(Node.NotificationApplicationPaused);
        Gain(0f, "pause without a window-focus notification must mute");
        SaveManager.Instance.PrefsSave.MuteInBackground = false;
        handler._Notification(Node.NotificationApplicationResumed);
        Gain(0.6f, "disabling preference while muted must not leave permanent silence");
        handler._Notification(Node.NotificationApplicationPaused);
        Gain(0.6f, "disabled background preference must allow continued playback");

        handler = Reset();
        SaveManager.Instance.SettingsSave.VolumeMaster = 0f;
        NGame.Instance.AudioManager.Gain = NGame.Instance.DebugAudio.Gain = 0f;
        handler._Notification(Node.NotificationApplicationPaused);
        handler._Notification(Node.NotificationApplicationResumed);
        Gain(0f, "user-muted volume stays muted on resume");
        handler._Notification(42);
        if (handler.OtherNotifications != 1)
            throw new Exception("unrelated notifications must retain original behavior");

        handler = Reset();
        SaveManager.Instance.PrefsSave = null;
        SaveManager.Instance.SettingsSave = null;
        handler._Notification(Node.NotificationApplicationPaused);
        Gain(0.6f, "early notification without saves must not corrupt output");
        SaveManager.Instance = new SaveManager();
        handler._Notification(Node.NotificationApplicationPaused);
        Gain(0f, "notification after saves become ready must mute");
        handler._Notification(Node.NotificationApplicationResumed);
        Gain(0.6f, "initialized output restores normally");

        handler = Reset();
        OS.Platform = "Windows";
        handler._Notification(Node.NotificationWMWindowFocusOut);
        Gain(0.6f, "non-Android must retain the original deferred fade");
        Console.WriteLine("PASS: immediate silence, notification ordering, preference changes, zero volume, startup and platform isolation.");
    }

    private static NMuteInBackgroundHandler Reset()
    {
        OS.Platform = "Android";
        SaveManager.Instance = new SaveManager();
        NGame.Instance = new NGame();
        return new NMuteInBackgroundHandler();
    }

    private static void Gain(float expected, string contract)
    {
        if (NGame.Instance.AudioManager.Gain != expected || NGame.Instance.DebugAudio.Gain != expected)
            throw new Exception(contract + $": FMOD={NGame.Instance.AudioManager.Gain}, Godot={NGame.Instance.DebugAudio.Gain}, expected={expected}");
    }
}
