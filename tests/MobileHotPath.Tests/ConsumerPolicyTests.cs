using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Entities.Merchant;
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
        MerchantProductPurchases();
        MerchantSelectionLifecycle();
        MerchantRejectedPurchases();
        MerchantCancellationAndExceptions();
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
        slot.Entry.RestockAfterPurchase = true;
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
        slot.Entry.RestockAfterPurchase = true;
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

    private static void MerchantProductPurchases()
    {
        var inventory = new NMerchantInventory();
        inventory._Ready();
        var button = inventory.GetNodeOrNull<NConfirmButton>("AndroidMerchantConfirmButton");
        NMerchantSlot[] slots = { new NMerchantCard(inventory), new NMerchantRelic(inventory), new NMerchantPotion(inventory), new NMerchantCardRemoval(inventory) };
        foreach (var slot in slots)
        {
            int beforeGold = inventory.Inventory.Gold;
            int beforeItems = inventory.Inventory.PurchasedItems.Count;
            slot.OnMouseReleased(new InputEventMouseButton { Device = -1, ButtonIndex = MouseButton.Left });
            Require(button.Enabled && slot.Purchases == 0 && inventory.Inventory.Gold == beforeGold && inventory.Inventory.PurchasedItems.Count == beforeItems,
                $"first touch only selects {slot.Entry.Item}, without charging or granting it");
            button.Release();
            Require(!button.Enabled && slot.Purchases == 1 && inventory.Inventory.Gold == beforeGold - slot.Entry.Cost
                && inventory.Inventory.PurchasedItems.Count == beforeItems + 1 && inventory.Inventory.PurchasedItems[^1] == slot.Entry.Item && !slot.Entry.IsStocked,
                $"confirmation buys exactly the selected {slot.Entry.Item} once, at its normal price");
            button.Release();
            Require(slot.Purchases == 1 && inventory.Inventory.Gold == beforeGold - slot.Entry.Cost,
                $"a repeated confirmation cannot repurchase {slot.Entry.Item}");
        }
    }

    private static void MerchantSelectionLifecycle()
    {
        var inventory = new NMerchantInventory();
        inventory._Ready();
        var card = new NMerchantCard(inventory);
        var relic = new NMerchantRelic(inventory);
        var button = inventory.GetNodeOrNull<NConfirmButton>("AndroidMerchantConfirmButton");
        card._GuiInput(new InputEventScreenTouch { Pressed = true, SelectPressed = true });
        relic._GuiInput(new InputEventScreenTouch { Pressed = true, SelectPressed = true });
        button.Release();
        Require(card.Purchases == 0 && relic.Purchases == 1 && inventory.Inventory.Gold == 100 - relic.Entry.Cost,
            "changing the pending slot purchases only the most recently selected item");

        card._GuiInput(new InputEventScreenTouch { Pressed = true, SelectPressed = true });
        inventory.OnPurchaseCompleted();
        button.Release();
        Require(card.Purchases == 0 && !button.Enabled, "another completed purchase invalidates the pending slot");
        card._GuiInput(new InputEventScreenTouch { Pressed = true, SelectPressed = true });
        inventory.Close();
        inventory.Open();
        button.Release();
        Require(card.Purchases == 0 && !button.Enabled, "closing and reopening cannot revive a pending purchase");
        card._GuiInput(new InputEventScreenTouch { Pressed = true, SelectPressed = true });
        inventory.Open();
        button.Release();
        Require(card.Purchases == 0 && !button.Enabled, "opening an inventory clears its previous selection");

        card._GuiInput(new InputEventScreenTouch { Pressed = true, SelectPressed = true });
        inventory.BlockInput();
        Require(!button.Enabled, "blocked merchant input disables confirmation without purchasing");
        inventory.UnblockInput();
        inventory.OnActiveScreenUpdated();
        Require(button.Enabled && card.Purchases == 0, "unblocking and focus refresh retain the current pending slot");
        button.Release();
        Require(card.Purchases == 1 && !button.Enabled, "the retained pending slot still requires an explicit confirmation");

        var potion = new NMerchantPotion(inventory);
        potion._GuiInput(new InputEventScreenTouch { Pressed = true, SelectPressed = true });
        potion.Entry.IsStocked = false;
        button.Release();
        Require(potion.Purchases == 0 && !button.Enabled, "a pending item that went out of stock cannot be bought");
    }

    private static void MerchantRejectedPurchases()
    {
        foreach (var item in new[] { MerchantItem.Card, MerchantItem.Relic, MerchantItem.Potion, MerchantItem.CardRemoval })
        {
            var inventory = new NMerchantInventory();
            inventory._Ready();
            NMerchantSlot slot = item switch
            {
                MerchantItem.Card => new NMerchantCard(inventory),
                MerchantItem.Relic => new NMerchantRelic(inventory),
                MerchantItem.Potion => new NMerchantPotion(inventory),
                _ => new NMerchantCardRemoval(inventory)
            };
            var button = inventory.GetNodeOrNull<NConfirmButton>("AndroidMerchantConfirmButton");
            inventory.Inventory.Gold = slot.Entry.Cost - 1;
            slot._GuiInput(new InputEventScreenTouch { Pressed = true, SelectPressed = true });
            button.Release();
            Require(slot.Purchases == 0 && slot.Entry.IsStocked && inventory.Inventory.PurchasedItems.Count == 0
                && inventory.Inventory.Gold == slot.Entry.Cost - 1 && !button.Enabled,
                $"an unaffordable {item} preserves vanilla failure, stock and wallet state");
            inventory.Inventory.Gold = 100;
            button.Release();
            Require(slot.Purchases == 0, $"adding gold after a failed {item} purchase cannot commit a stale confirmation");
        }

        var potionInventory = new NMerchantInventory();
        potionInventory._Ready();
        potionInventory.Inventory.PotionBeltFull = true;
        var potion = new NMerchantPotion(potionInventory);
        potion._GuiInput(new InputEventScreenTouch { Pressed = true, SelectPressed = true });
        potionInventory.GetNodeOrNull<NConfirmButton>("AndroidMerchantConfirmButton").Release();
        Require(potion.Purchases == 0 && potion.Entry.IsStocked && potionInventory.Inventory.Gold == 100 && potionInventory.Inventory.PurchasedItems.Count == 0,
            "a full potion belt rejects confirmation without charging or granting a potion");
    }

    private static void MerchantCancellationAndExceptions()
    {
        var previousContext = SynchronizationContext.Current;
        var context = new MerchantCallbackContext();
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            var inventory = new NMerchantInventory();
            inventory._Ready();
            var button = inventory.GetNodeOrNull<NConfirmButton>("AndroidMerchantConfirmButton");
            var removal = new NMerchantCardRemoval(inventory);
            removal.Entry.PurchaseDecision = new TaskCompletionSource<bool>();
            removal._GuiInput(new InputEventScreenTouch { Pressed = true, SelectPressed = true });
            button.Release();
            Require(removal.Purchases == 0 && inventory.Inventory.Gold == 100 && !button.Enabled,
                "confirmed card removal awaits the original card choice before charging or removing");
            SetTouchControls(false);
            removal.Entry.PurchaseDecision.SetResult(false);
            context.Drain();
            Require(removal.Purchases == 0 && removal.Entry.IsStocked && inventory.Inventory.Gold == 100 && inventory.Inventory.PurchasedItems.Count == 0,
                "canceling card removal preserves stock, gold and deck even across an input-mode reset");
            SetTouchControls(true);
            button.Release();
            Require(removal.Purchases == 0, "a canceled removal cannot be revived by a stale confirmation");
            removal.Entry.PurchaseDecision = new TaskCompletionSource<bool>();
            removal._GuiInput(new InputEventScreenTouch { Pressed = true, SelectPressed = true });
            button.Release();
            SetTouchControls(false);
            removal.Entry.PurchaseDecision.SetResult(true);
            context.Drain();
            Require(removal.Purchases == 1 && !removal.Entry.IsStocked && inventory.Inventory.Gold == 100 - removal.Entry.Cost
                && inventory.Inventory.PurchasedItems.Count == 1 && inventory.Inventory.PurchasedItems[0] == MerchantItem.CardRemoval,
                "mode reset does not cancel an already-started original card removal or duplicate its charge");
            SetTouchControls(true);

            var card = new NMerchantCard(inventory);
            var failure = new InvalidOperationException("merchant purchase failure");
            card.Entry.PurchaseException = failure;
            card._GuiInput(new InputEventScreenTouch { Pressed = true, SelectPressed = true });
            button.Release();
            context.Drain();
            Require(context.Errors.Count == 1 && ReferenceEquals(context.Errors[0], failure) && card.Purchases == 0
                && inventory.Inventory.Gold == 100 - removal.Entry.Cost && !button.Enabled,
                "the actual purchase exception reaches the caller instead of being swallowed or reported as success");
            card.Entry.PurchaseException = null;
            card._GuiInput(new InputEventScreenTouch { Pressed = true, SelectPressed = true });
            Require(card.Purchases == 0 && button.Enabled, "failed confirmation leaves no allowance for a later first selection");
            button.Release();
            context.Drain();
            Require(card.Purchases == 1 && context.Errors.Count == 1, "a fresh confirmation can buy normally after a purchase fault");
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    private sealed class MerchantCallbackContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object State)> _callbacks = new();
        public readonly List<Exception> Errors = new();
        public override void Post(SendOrPostCallback callback, object state) { _callbacks.Enqueue((callback, state)); }
        public void Drain()
        {
            while (_callbacks.Count > 0)
            {
                var callback = _callbacks.Dequeue();
                try { callback.Callback(callback.State); }
                catch (Exception exception) { Errors.Add(exception); }
            }
        }
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
