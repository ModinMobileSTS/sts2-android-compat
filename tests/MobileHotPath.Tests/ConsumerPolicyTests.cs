using System;
using Godot;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using STS2Mobile.Android;
using STS2Mobile.Patches;

internal static class ConsumerPolicyTests
{
    public static void Run()
    {
        HardwareAndDesktopBypass(mobile: true);
        HardwareAndDesktopBypass(mobile: false);
        OS.MobileFeature = true;
        SetTouchControls(true);
        ConfirmationOnExistingScreens();
        PreviewPointerOwnershipAndReset();
        RewardConfirmationAndReset();
        MerchantConfirmationAndReset();
        TooltipPointerOwnershipAndReset();
        TouchReleaseOwnershipAndReset();
    }

    private static void HardwareAndDesktopBypass(bool mobile)
    {
        OS.MobileFeature = mobile;
        SetTouchControls(!mobile);
        var hand = NewHand(out var holder);
        hand.OnHolderPressed(holder);
        Require(hand.CardPlays == 1 && !MobileTapPreviewPatches.IsPinned(holder), "hardware/desktop card press uses vanilla play");

        var reward = NewReward(out var rewardHolder);
        reward.SelectCard(rewardHolder);
        Require(reward.Completion.IsCompleted, "hardware/desktop reward selection completes immediately");
        Require(reward.GetNodeOrNull<NConfirmButton>("AndroidMobileConfirm") == null, "hardware/desktop ready does not inject a mobile reward button");
        var choose = new NChooseACardSelectionScreen();
        choose._Ready();
        choose.SelectHolder(new NCardHolder());
        Require(choose.Completion.IsCompleted, "hardware/desktop choose-card bypasses touch confirmation and debounce");

        var inventory = new NMerchantInventory();
        inventory._Ready();
        var slot = new NMerchantCard(inventory);
        slot.OnMouseReleased(new InputEventMouseButton { ButtonIndex = MouseButton.Left });
        Require(slot.Purchases == 1, "hardware/desktop merchant release purchases immediately");
        slot._GuiInput(new InputEvent { SelectPressed = true });
        Require(slot.Purchases == 2, "hardware/desktop select action uses vanilla purchase");
        Require(inventory.GetNodeOrNull<NConfirmButton>("AndroidMerchantConfirmButton") == null, "hardware/desktop ready does not inject a mobile merchant button");

        AndroidSettingsBridge.Mode = "hidden";
        MobileTooltipPatches.RefreshModeFromSettings();
        var tip = NHoverTipSet.CreateAndShow(new Control());
        Require(tip != null && tip.Visible, "hardware/desktop hidden setting does not suppress ordinary hover tips");
        AndroidSettingsBridge.Mode = "long_press";
        MobileTooltipPatches.RefreshModeFromSettings();
        Require(tip.Valid && tip.Visible, "hardware/desktop settings refresh preserves vanilla tooltip visibility");
        var play = new NMouseCardPlay();
        play._Input(new InputEventScreenTouch { Pressed = false });
        Require(!play.Cancelled, "hardware/desktop bypasses touch-only outside-zone cancellation");
        NHoverTipSet.Clear();
        AndroidSettingsBridge.Mode = "immediate";
        MobileTooltipPatches.RefreshModeFromSettings();
        Require(AndroidSettingsBridge.TouchPreviewEnabled && AndroidSettingsBridge.SelectionConfirmationEnabled,
            "input policy never rewrites stored touch settings");
    }

    private static void ConfirmationOnExistingScreens()
    {
        SetTouchControls(false);
        var reward = NewReward(out var rewardHolder);
        var choose = new NChooseACardSelectionScreen();
        choose._Ready();
        var inventory = new NMerchantInventory();
        inventory._Ready();
        SetTouchControls(true);
        reward.SelectCard(rewardHolder);
        var rewardButton = reward.GetNodeOrNull<NConfirmButton>("AndroidMobileConfirm");
        Require(rewardButton?.Enabled == true && !reward.Completion.IsCompleted, "touch entry creates confirmation on an already-open reward screen");
        Time.Msec += 351;
        choose.SelectHolder(new NCardHolder());
        var chooseButton = choose.GetNodeOrNull<NConfirmButton>("AndroidMobileConfirm");
        Require(chooseButton?.Enabled == true && !choose.Completion.IsCompleted, "touch entry creates confirmation on an already-open choose-card screen");
        var slot = new NMerchantCard(inventory);
        slot.OnMouseReleased(new InputEventMouseButton { Device = -1, ButtonIndex = MouseButton.Left });
        var merchantButton = inventory.GetNodeOrNull<NConfirmButton>("AndroidMerchantConfirmButton");
        Require(merchantButton?.Enabled == true && slot.Purchases == 0, "touch entry creates confirmation on an already-open merchant screen");
        SetTouchControls(false);
        rewardButton.Release();
        chooseButton.Release();
        merchantButton.Release();
        Require(!reward.Completion.IsCompleted && !choose.Completion.IsCompleted && slot.Purchases == 0,
            "returning to hardware drops all open-screen pending confirmations without committing them");
        SetTouchControls(true);
    }

    private static void PreviewPointerOwnershipAndReset()
    {
        var hand = NewHand(out var holder);
        hand.OnHolderPressed(holder);
        Require(hand.CardPlays == 0 && MobileTapPreviewPatches.IsPinned(holder), "touch press pins preview without playing");
        NGame.Instance._Input(new InputEventMouseMotion { Position = new Vector2(0, -80) });
        NGame.Instance._Input(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false });
        Require(hand.CardPlays == 0 && MobileTapPreviewPatches.IsPinned(holder), "hardware motion/release cannot consume an armed touch drag");
        NGame.Instance._Input(new InputEventScreenDrag { Position = new Vector2(0, -80) });
        Require(hand.CardPlays == 1 && hand.InCardPlay, "touch drag still starts original card play after competing hardware input");
        SetTouchControls(false);
        Require(hand.InCardPlay && hand.CardPlays == 1, "mode reset never cancels an already-started original card play");
        SetTouchControls(true);

        hand = NewHand(out holder);
        hand.OnHolderPressed(holder);
        hand.OnHolderPressed(holder);
        holder.Hitbox.Emit(NClickableControl.SignalName.MouseReleased,
            new InputEventMouseButton { ButtonIndex = MouseButton.Left });
        Require(MobileTapPreviewPatches.IsPinned(holder), "hardware holder release cannot finish pending touch put-down");
        NGame.Instance._Input(new InputEventMouseButton { Device = -1, ButtonIndex = MouseButton.Left });
        Require(!MobileTapPreviewPatches.IsPinned(holder), "emulated touch release completes pending put-down");
        holder.SetMouseHovered(true);
        hand.OnHolderFocused(holder);
        Require(!holder.IsFocused && hand.FocusedHolder == null, "put-down suppresses touch hover until exit");
        SetTouchControls(false);
        Require(holder.IsHovered && holder.IsFocused, "switch restores PC hover for a previously suppressed holder");
        hand.OnHolderFocused(holder);
        Require(ReferenceEquals(hand.FocusedHolder, holder), "PC hand focus is no longer suppressed");
        SetTouchControls(true);

        hand = NewHand(out holder);
        hand.OnHolderPressed(holder);
        hand.OnHolderPressed(holder);
        SetTouchControls(false);
        Require(!holder.IsFocused || holder.IsHovered, "reset releases artificial preview focus");
        Require(hand.CardPlays == 0 && holder.CardModel.ManualPlays == 0, "pending put-down cleanup never plays a card");
        SetTouchControls(true);
        NGame.Instance._Input(new InputEventScreenTouch { Pressed = false });
        NGame.Instance._Input(new InputEventScreenDrag { Position = new Vector2(0, -80) });
        Require(hand.CardPlays == 0 && MobileTapPreviewPatches.GetPinnedHolder(hand) == null,
            "stale release/drag cannot revive reset pending put-down or direct-drag state");

        hand.OnHolderPressed(holder);
        NGame.Instance._Input(new InputEventMouseButton { Pressed = true, ButtonIndex = MouseButton.Left, Position = new Vector2(300, 100) });
        Require(holder.CardModel.ManualPlays == 0, "hardware press cannot play a pinned touch card");
        NGame.Instance._Input(new InputEventScreenTouch { Pressed = true, Position = new Vector2(300, 100) });
        Require(holder.CardModel.ManualPlays == 1, "touch play-zone tap retains existing manual play behavior");

        hand = NewHand(out holder);
        hand.OnHolderPressed(holder);
        NGame.Instance._Input(new InputEventMouseMotion { NativeTouchSource = true, Position = new Vector2(0, -80) });
        Require(hand.CardPlays == 1, "native source-marked mouse motion also keeps touch drag behavior");
        hand = NewHand(out holder);
        hand.OnHolderPressed(holder);
        AndroidSettingsBridge.TouchPreviewEnabled = false;
        MobileTapPreviewPatches.ClearAllPinned();
        Require(!MobileTapPreviewPatches.IsPinned(holder) && holder.IsHovered && holder.IsFocused,
            "preview setting hot-disable clears pin and restores ordinary hover");
        AndroidSettingsBridge.TouchPreviewEnabled = true;
    }

    private static void RewardConfirmationAndReset()
    {
        var screen = NewReward(out var holder);
        var button = screen.GetNodeOrNull<NConfirmButton>("AndroidMobileConfirm");
        screen.SelectCard(holder);
        Require(!screen.Completion.IsCompleted && button.Enabled && holder.IsFocused, "touch reward waits for confirmation with pinned preview");
        SetTouchControls(false);
        Require(!button.Enabled && !holder.IsFocused && !screen.Completion.IsCompleted,
            "reward reset clears selection/button without awarding a card");
        button.Release();
        SetTouchControls(true);
        button.Release();
        Require(!screen.Completion.IsCompleted, "late reward confirmation cannot consume the previous selection");
        screen.SelectCard(holder);
        button.Release();
        Require(screen.Completion.IsCompleted && screen.Completion.Result == 0, "fresh touch reward still confirms normally");

        var choose = new NChooseACardSelectionScreen();
        choose._Ready();
        Time.Msec += 351;
        holder = new NCardHolder();
        choose.SelectHolder(holder);
        button = choose.GetNodeOrNull<NConfirmButton>("AndroidMobileConfirm");
        Require(button.Enabled && !choose.Completion.IsCompleted, "touch choose-card waits for confirmation");
        SetTouchControls(false);
        button.Release();
        Require(!button.Enabled && !holder.IsFocused && !choose.Completion.IsCompleted, "choose-card reset never completes its pending task");
        SetTouchControls(true);
        button.Release();
        Require(!choose.Completion.IsCompleted, "stale choose-card callback remains inert on touch reentry");
        choose.SelectHolder(holder);
        AndroidSettingsBridge.SelectionConfirmationEnabled = false;
        button.Release();
        Require(!choose.Completion.IsCompleted, "confirmation hot-disable blocks an already-wired callback");
        AndroidSettingsBridge.SelectionConfirmationEnabled = true;
        button.Release();
        Require(choose.Completion.IsCompleted, "confirmation setting reenable retains ordinary fresh selection behavior");
    }

    private static void MerchantConfirmationAndReset()
    {
        var inventory = new NMerchantInventory();
        inventory._Ready();
        var slot = new NMerchantCard(inventory);
        var button = inventory.GetNodeOrNull<NConfirmButton>("AndroidMerchantConfirmButton");
        slot.OnMouseReleased(new InputEventMouseButton { Device = -1, ButtonIndex = MouseButton.Left });
        Require(slot.Purchases == 0 && button.Enabled, "touch merchant release waits for confirmation");
        SetTouchControls(false);
        button.Release();
        Require(slot.Purchases == 0 && !button.Enabled, "merchant reset clears pending purchase without buying");
        SetTouchControls(true);
        button.Release();
        Require(slot.Purchases == 0, "late merchant callback cannot buy the previous pending item");
        slot._GuiInput(new InputEventScreenTouch { Pressed = true, SelectPressed = true });
        Require(slot.Purchases == 0 && button.Enabled, "touch select action arms confirmation");
        button.Release();
        Require(slot.Purchases == 1 && !button.Enabled, "fresh merchant confirmation purchases exactly once");
        slot._GuiInput(new InputEvent { SelectPressed = true });
        Require(slot.Purchases == 1 && button.Enabled, "nonpointer select action follows current touch event policy");
        AndroidSettingsBridge.SelectionConfirmationEnabled = false;
        button.Release();
        Require(slot.Purchases == 1, "merchant setting hot-disable blocks pending callback");
        AndroidSettingsBridge.SelectionConfirmationEnabled = true;
        MerchantSelectionConfirmationPatches.ResetForInputModeChange();
        button.Release();
        Require(slot.Purchases == 1, "merchant reset also clears a selection left by hot-disable");
    }

    private static void TooltipPointerOwnershipAndReset()
    {
        AndroidSettingsBridge.Mode = "long_press";
        MobileTooltipPatches.RefreshModeFromSettings();
        Time.Msec = 20000;
        var owner = new Control();
        var tip = NHoverTipSet.CreateAndShow(owner);
        Require(!tip.Visible, "touch tooltip starts hidden for a fresh hold");
        NGame.Instance._Input(new InputEventMouseMotion { Position = new Vector2(200, 0) });
        NGame.Instance._Input(new InputEventMouseButton { ButtonIndex = MouseButton.Left });
        owner.Emit(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.Left });
        Time.Msec = 21001;
        tip._Process(0.1);
        Require(tip.Visible, "hardware global/owner motion and release cannot cancel the touch tooltip hold");
        NGame.Instance._Input(new InputEventMouseButton { Device = -1, ButtonIndex = MouseButton.Left });
        Require(!tip.Visible, "emulated touch release still hides the revealed tooltip");

        NHoverTipSet.Remove(owner);
        Time.Msec = 22000;
        tip = NHoverTipSet.CreateAndShow(owner);
        var staleTimer = SceneTree.Shared.Timers[^1];
        SetTouchControls(false);
        Require(tip.Valid && tip.Visible, "switch to hardware restores an existing hidden tooltip without recreating it");
        staleTimer.Emit(SceneTreeTimer.SignalName.Timeout);
        Require(tip.Visible, "late touch timer cannot rehide restored PC tooltip");
        SetTouchControls(true);
        Time.Msec = 22001;
        tip = NHoverTipSet.CreateAndShow(owner);
        var freshTimer = SceneTree.Shared.Timers[^1];
        staleTimer.Emit(SceneTreeTimer.SignalName.Timeout);
        Require(!tip.Visible, "old timer cannot reveal the next touch hold after mode reentry");
        Time.Msec = 23002;
        freshTimer.Emit(SceneTreeTimer.SignalName.Timeout);
        Require(tip.Visible, "fresh hold timer still reveals normally after reset");
        SetTouchControls(false);
        AndroidSettingsBridge.Mode = "hidden";
        MobileTooltipPatches.RefreshModeFromSettings();
        Require(tip.Valid && tip.Visible, "hardware hot-setting change does not reinstate touch hidden state");
        SetTouchControls(true);
        Require(NHoverTipSet.CreateAndShow(new Control()) == null, "touch reentry reapplies saved hidden tooltip mode");
        AndroidSettingsBridge.Mode = "immediate";
        MobileTooltipPatches.RefreshModeFromSettings();
        NHoverTipSet.Clear();
    }

    private static void TouchReleaseOwnershipAndReset()
    {
        var play = new NMouseCardPlay();
        play._Input(new InputEventMouseButton { ButtonIndex = MouseButton.Left });
        Require(!play.Cancelled, "hardware mouse release cannot cancel touch-owned card play");
        play._Input(new InputEventMouseButton { Device = -1, ButtonIndex = MouseButton.Left });
        Require(play.Cancelled, "touch-emulated release retains outside-zone cancellation");
        var target = new NTargetManager();
        target.StartTargeting();
        target._Input(new InputEventMouseButton { ButtonIndex = MouseButton.Left });
        Require(!target.Cancelled, "hardware release cannot finish touch targeting");
        target._Input(new InputEventScreenTouch { Pressed = false });
        Require(target.Cancelled, "native touch release retains untargeted cancellation");
        TouchInputPatches.ResetForInputModeChange();
        Require(!TouchInputPatches.ConsumeCancelledByUntargetedRelease(target), "release reset drops stale cancellation metadata");
    }

    private static NPlayerHand NewHand(out NHandCardHolder holder)
    {
        MobileTapPreviewPatches.ResetForInputModeChange();
        var hand = NPlayerHand.Instance = new NPlayerHand();
        holder = new NHandCardHolder();
        hand.AddChild(holder);
        hand.ActiveHolders.Add(holder);
        holder._Ready();
        Viewport.Shared.MousePosition = Vector2.Zero;
        return hand;
    }

    private static NCardRewardSelectionScreen NewReward(out NCardHolder holder)
    {
        var screen = new NCardRewardSelectionScreen();
        holder = new NCardHolder();
        screen.AddOption(holder);
        screen._Ready();
        return screen;
    }

    private static void SetTouchControls(bool enabled)
    {
        MobileInputPolicyPatches.TouchControls = enabled;
        TouchInputPatches.ResetForInputModeChange();
        MobileTapPreviewPatches.ResetForInputModeChange();
        MobileSelectionConfirmationPatches.ResetForInputModeChange();
        MerchantSelectionConfirmationPatches.ResetForInputModeChange();
        MobileTooltipPatches.ResetForInputModeChange();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
