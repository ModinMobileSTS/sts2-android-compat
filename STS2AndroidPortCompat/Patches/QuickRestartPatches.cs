using System;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Audio;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.PauseMenu;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using STS2Mobile.Android;

namespace STS2Mobile.Patches;

public static class QuickRestartPatches
{
    private const string QuickRestartModId = "quickRestart2";

    public static void Apply(Harmony harmony)
    {
        PatchHelper.Patch(harmony, typeof(NPauseMenu), "_Ready", postfix: PatchHelper.Method(typeof(QuickRestartPatches), nameof(PauseMenuReadyPostfix)));
    }

    public static void PauseMenuReadyPostfix(NPauseMenu __instance)
    {
        try
        {
            if (!OS.GetName().Equals("Android", StringComparison.OrdinalIgnoreCase))
                return;
            if (!AndroidSettingsBridge.GetBool("quick_sl_enabled", true))
                return;
            if (RunManager.Instance.NetService.Type != NetGameType.Singleplayer)
                return;
            if (IsQuickRestartModAlreadyProvidingUi())
                return;
            var buttonContainer = __instance.GetNodeOrNull<Control>("%ButtonContainer");
            if (buttonContainer == null || buttonContainer.GetNodeOrNull<NPauseMenuButton>("Retry") != null)
                return;
            var saveAndQuitButton = buttonContainer.GetNodeOrNull<NPauseMenuButton>("SaveAndQuit");
            var giveUpButton = buttonContainer.GetNodeOrNull<NPauseMenuButton>("GiveUp");
            if (saveAndQuitButton == null || giveUpButton == null)
                return;
            var retryButton = (NPauseMenuButton)saveAndQuitButton.Duplicate();
            retryButton.Name = "Retry";
            MakeButtonVisualsUnique(retryButton);
            retryButton.GetNode<MegaLabel>("Label").SetTextAutoSize(GetRetryLabel());
            buttonContainer.AddChildSafely(retryButton);
            buttonContainer.MoveChild(retryButton, giveUpButton.GetIndex());
            retryButton.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ => OnQuickRestartPressed(buttonContainer)));
            if (!SaveManager.Instance.HasRunSave)
                retryButton.Disable();
            RebuildPauseMenuFocusNeighbors(buttonContainer);
            PatchHelper.Log("Applied built-in Android quick restart pause-menu button.");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Quick restart pause-menu patch failed: {exception}");
        }
    }

    private static bool IsQuickRestartModAlreadyProvidingUi()
    {
        try
        {
            var getLoadedMods = typeof(ModManager).GetMethod("GetLoadedMods", BindingFlags.Public | BindingFlags.Static);
            var loadedMods = getLoadedMods?.Invoke(null, null) as System.Collections.IEnumerable;
            if (loadedMods == null)
                return false;
            foreach (var mod in loadedMods)
            {
                var manifest = mod?.GetType().GetField("manifest", BindingFlags.Public | BindingFlags.Instance)?.GetValue(mod);
                var id = manifest?.GetType().GetField("id", BindingFlags.Public | BindingFlags.Instance)?.GetValue(manifest) as string;
                var assemblyLoaded = mod?.GetType().GetField("assemblyLoadedSuccessfully", BindingFlags.Public | BindingFlags.Instance)?.GetValue(mod) as bool?;
                if (string.Equals(id, QuickRestartModId, StringComparison.OrdinalIgnoreCase) && assemblyLoaded == true)
                    return true;
            }
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Quick restart mod detection failed: {exception.Message}");
        }
        return false;
    }

    private static string GetRetryLabel() => "zhs".Equals(GetCurrentLanguage(), StringComparison.OrdinalIgnoreCase) ? "重打" : "Retry";

    private static string GetCurrentLanguage()
    {
        try
        {
            var locManagerType = typeof(NGame).Assembly.GetType("MegaCrit.Sts2.Core.Localization.LocManager");
            var instance = locManagerType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            return locManagerType?.GetProperty("Language", BindingFlags.Public | BindingFlags.Instance)?.GetValue(instance) as string ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void OnQuickRestartPressed(Control buttonContainer)
    {
        foreach (Node child in buttonContainer.GetChildren())
        {
            if (child is NPauseMenuButton button)
                button.Disable();
        }
        TaskHelper.RunSafely(QuickLoadInternalAsync());
    }

    private static async Task QuickLoadInternalAsync()
    {
        var fadeOutCompleted = false;
        try
        {
            await WaitForCurrentRunSaveTaskAsync();
            var loadResult = SaveManager.Instance.LoadRunSave();
            if (!loadResult.Success || loadResult.SaveData == null)
            {
                ShowError($"Quick restart failed: could not read autosave. Status={loadResult.Status}");
                return;
            }
            var saveData = loadResult.SaveData;
            var runState = RunState.FromSerializable(saveData);
            RunManager.Instance.ActionQueueSet.Reset();
            NRunMusicController.Instance?.StopMusic();
            await NGame.Instance.Transition.FadeOut();
            fadeOutCompleted = true;
            RunManager.Instance.CleanUp();
            await SetUpSavedSinglePlayerAsync(runState, saveData);
            InitializeReactionNetworking();
            await NGame.Instance.LoadRun(runState, saveData.PreFinishedRoom);
            await NGame.Instance.Transition.FadeIn();
            fadeOutCompleted = false;
            PatchHelper.Log("Built-in Android quick restart completed.");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Built-in Android quick restart failed: {exception}");
            if (fadeOutCompleted)
                await TryFadeInAfterQuickRestartFailureAsync();
            ShowError("Quick restart failed: " + exception.Message);
        }
    }

    private static async Task SetUpSavedSinglePlayerAsync(RunState runState, SerializableRun saveData)
    {
        foreach (var methodName in new[] { "SetUpSavedSingleplayer", "SetUpSavedSinglePlayer" })
        {
            var method = typeof(RunManager).GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(RunState), typeof(SerializableRun) }, null);
            if (method == null)
                continue;
            PatchHelper.Log($"Built-in Android quick restart invoking RunManager.{method.Name}.");
            object result;
            try
            {
                result = method.Invoke(RunManager.Instance, new object[] { runState, saveData });
            }
            catch (TargetInvocationException exception) when (exception.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
            if (result is Task task)
                await task;
            return;
        }
        throw new MissingMethodException(typeof(RunManager).FullName, "SetUpSavedSingleplayer/SetUpSavedSinglePlayer(RunState, SerializableRun)");
    }

    private static async Task WaitForCurrentRunSaveTaskAsync()
    {
        try
        {
            var saveTask = SaveManager.Instance.CurrentRunSaveTask;
            if (saveTask == null)
                return;
            PatchHelper.Log("Built-in Android quick restart waiting for current run save task.");
            await saveTask;
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Built-in Android quick restart save wait failed; continuing with latest readable save: {exception.Message}");
        }
    }

    private static async Task TryFadeInAfterQuickRestartFailureAsync()
    {
        try
        {
            var transition = NGame.Instance?.Transition;
            if (transition != null)
                await transition.FadeIn();
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Quick restart failure fade-in recovery failed: {exception.Message}");
        }
    }

    private static void InitializeReactionNetworking()
    {
        try
        {
            var serviceType = typeof(NetGameType).Assembly.GetType("MegaCrit.Sts2.Core.Multiplayer.NetSingleplayerGameService");
            var service = serviceType == null ? null : Activator.CreateInstance(serviceType);
            var reactionContainer = NGame.Instance.GetType().GetProperty("ReactionContainer", BindingFlags.Public | BindingFlags.Instance)?.GetValue(NGame.Instance);
            reactionContainer?.GetType().GetMethod("InitializeNetworking", BindingFlags.Public | BindingFlags.Instance)?.Invoke(reactionContainer, new[] { service });
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Quick restart reaction networking init failed: {exception.Message}");
        }
    }

    private static void ShowError(string message)
    {
        try
        {
            var popup = NErrorPopup.Create("Quick restart", message, false);
            if (popup != null)
                NModalContainer.Instance?.Add(popup);
        }
        catch
        {
        }
    }

    private static void MakeButtonVisualsUnique(NPauseMenuButton retryButton)
    {
        var image = retryButton.GetNodeOrNull<TextureRect>("ButtonImage");
        if (image?.Material is ShaderMaterial shaderMaterial)
            image.Material = (ShaderMaterial)shaderMaterial.Duplicate();
    }

    private static void RebuildPauseMenuFocusNeighbors(Control buttonContainer)
    {
        var buttons = buttonContainer.GetChildren().OfType<NPauseMenuButton>().Where(button => button.Visible).ToArray();
        for (var i = 0; i < buttons.Length; i++)
        {
            var button = buttons[i];
            button.FocusNeighborLeft = button.GetPath();
            button.FocusNeighborRight = button.GetPath();
            button.FocusNeighborTop = i > 0 ? buttons[i - 1].GetPath() : button.GetPath();
            button.FocusNeighborBottom = i < buttons.Length - 1 ? buttons[i + 1].GetPath() : button.GetPath();
        }
    }
}
