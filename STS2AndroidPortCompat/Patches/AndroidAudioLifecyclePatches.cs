using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Saves;

namespace STS2Mobile.Patches;

public static class AndroidAudioLifecyclePatches
{
    private static Harmony _harmony;
    private static bool _installed;
    private static readonly ConditionalWeakTable<object, MuteState> States = new();

    private sealed class MuteState
    {
        public bool Paused;
        public bool Focused = true;
        public bool WindowFocused = true;
        public bool Muted;
    }

    public static void Apply(Harmony harmony)
    {
        _harmony = harmony;
        PatchHelper.Patch(harmony, typeof(NGame), "_Ready",
            postfix: PatchHelper.Method(typeof(AndroidAudioLifecyclePatches), nameof(GameReadyPostfix)));
    }

    public static void GameReadyPostfix()
    {
        if (_installed || OS.GetName() != "Android")
            return;
        try
        {
            // Resolve only the declared callback, after Godot and the game scene are ready.
            // Early inherited Godot method lookups can trigger unsafe MethodName .cctors.
            var handler = typeof(NGame).Assembly.GetType("MegaCrit.Sts2.Core.Nodes.NMuteInBackgroundHandler");
            var notification = handler?.GetMethod("_Notification", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly,
                null, new[] { typeof(int) }, null);
            if (notification == null)
                throw new MissingMethodException("NMuteInBackgroundHandler._Notification(int)");
            _harmony.Patch(notification, prefix: new HarmonyMethod(
                PatchHelper.Method(typeof(AndroidAudioLifecyclePatches), nameof(NotificationPrefix))));
            _installed = true;
            PatchHelper.Log("Android background audio: immediate mute lifecycle installed.");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Android background audio installation failed: {exception}");
        }
    }

    public static bool NotificationPrefix(object __instance, int what, ref Tween ____tween)
    {
        if (OS.GetName() != "Android")
            return true;
        if (what != Node.NotificationApplicationPaused && what != Node.NotificationApplicationResumed
            && what != Node.NotificationApplicationFocusOut && what != Node.NotificationApplicationFocusIn
            && what != Node.NotificationWMWindowFocusOut && what != Node.NotificationWMWindowFocusIn)
            return true;

        try
        {
            var state = States.GetOrCreateValue(__instance);
            switch (what)
            {
                case (int)Node.NotificationApplicationPaused:
                    state.Paused = true;
                    break;
                case (int)Node.NotificationApplicationResumed:
                    state.Paused = false;
                    break;
                case (int)Node.NotificationApplicationFocusOut:
                    state.Focused = false;
                    break;
                case (int)Node.NotificationApplicationFocusIn:
                    state.Focused = true;
                    break;
                case (int)Node.NotificationWMWindowFocusOut:
                    state.WindowFocused = false;
                    break;
                case (int)Node.NotificationWMWindowFocusIn:
                    state.WindowFocused = true;
                    break;
            }

            // A tween cannot finish once Android stops rendering. Never schedule one here.
            ____tween?.Kill();
            ____tween = null;
            var saves = SaveManager.Instance;
            var game = NGame.Instance;
            if (saves?.SettingsSave == null || game?.AudioManager == null || game.DebugAudio == null)
                return false;
            if (state.Paused || !state.Focused || !state.WindowFocused)
            {
                if (!state.Muted && saves.PrefsSave?.MuteInBackground == true)
                {
                    state.Muted = true;
                    game.AudioManager.SetMasterVol(0f);
                    game.DebugAudio.SetMasterAudioVolume(0f);
                    // Studio volume writes are queued; submit before Android stops frame updates.
                    Engine.GetSingleton("FmodServer").Call("update");
                    PatchHelper.Log("Android background audio: muted immediately.");
                }
            }
            else if (state.Muted)
            {
                // Restore the latest user setting, including edits made while in the launcher.
                float volume = saves.SettingsSave.VolumeMaster;
                game.AudioManager.SetMasterVol(volume);
                game.DebugAudio.SetMasterAudioVolume(volume);
                Engine.GetSingleton("FmodServer").Call("update");
                state.Muted = false;
                PatchHelper.Log($"Android background audio: restored master volume {volume}.");
            }
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Android background audio notification {what} failed: {exception}");
        }
        return false;
    }
}
