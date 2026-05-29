using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.ControllerInput;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using STS2Mobile.Android;

namespace STS2Mobile.Patches;

/// <summary>
/// Adds the reference Android port's second-tap purchase confirmation to the
/// merchant screen.  The imported PC scene has no ConfirmButton node, so the
/// compat layer creates one at runtime and intercepts NMerchantSlot.OnSelected
/// before it immediately buys the selected item.
/// </summary>
public static class MerchantSelectionConfirmationPatches
{
    private const string ConfirmButtonName = "AndroidMerchantConfirmButton";
    private static readonly ConditionalWeakTable<NMerchantInventory, MerchantConfirmState> States = new();
    private static readonly HashSet<NMerchantSlot> AllowedPurchases = new();
    private static readonly object AllowedPurchasesLock = new();
    private static readonly BindingFlags InstanceFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    public static void Apply(Harmony harmony)
    {
        PatchHelper.Patch(harmony, typeof(NMerchantInventory), "_Ready", postfix: PatchHelper.Method(typeof(MerchantSelectionConfirmationPatches), nameof(InventoryReadyPostfix)));
        PatchHelper.Patch(harmony, typeof(NMerchantInventory), "Open", prefix: PatchHelper.Method(typeof(MerchantSelectionConfirmationPatches), nameof(ClearInventorySelectionPrefix)));
        PatchHelper.Patch(harmony, typeof(NMerchantInventory), "Close", prefix: PatchHelper.Method(typeof(MerchantSelectionConfirmationPatches), nameof(ClearInventorySelectionPrefix)));
        PatchHelper.Patch(harmony, typeof(NMerchantInventory), "OnPurchaseCompleted", prefix: PatchHelper.Method(typeof(MerchantSelectionConfirmationPatches), nameof(ClearInventorySelectionPrefix)));
        PatchHelper.Patch(harmony, typeof(NMerchantInventory), "BlockInput", postfix: PatchHelper.Method(typeof(MerchantSelectionConfirmationPatches), nameof(RefreshInventoryPostfix)));
        PatchHelper.Patch(harmony, typeof(NMerchantInventory), "UnblockInput", postfix: PatchHelper.Method(typeof(MerchantSelectionConfirmationPatches), nameof(RefreshInventoryPostfix)));
        PatchHelper.Patch(harmony, typeof(NMerchantInventory), "OnActiveScreenUpdated", postfix: PatchHelper.Method(typeof(MerchantSelectionConfirmationPatches), nameof(RefreshInventoryPostfix)));
        PatchHelper.Patch(harmony, typeof(NMerchantSlot), "_GuiInput", prefix: PatchHelper.Method(typeof(MerchantSelectionConfirmationPatches), nameof(MerchantSlotGuiInputPrefix)));
        PatchHelper.Patch(harmony, typeof(NMerchantSlot), "OnMouseReleased", prefix: PatchHelper.Method(typeof(MerchantSelectionConfirmationPatches), nameof(MerchantSlotMouseReleasedPrefix)));
        PatchHelper.Patch(harmony, typeof(NMerchantSlot), "OnSelected", prefix: PatchHelper.Method(typeof(MerchantSelectionConfirmationPatches), nameof(MerchantSlotSelectedPrefix)));
        PatchMerchantPurchaseMethod(harmony, typeof(NMerchantCard));
        PatchMerchantPurchaseMethod(harmony, typeof(NMerchantRelic));
        PatchMerchantPurchaseMethod(harmony, typeof(NMerchantPotion));
        PatchMerchantPurchaseMethod(harmony, typeof(NMerchantCardRemoval));
    }

    private static void PatchMerchantPurchaseMethod(Harmony harmony, Type slotType)
    {
        PatchHelper.Patch(harmony, slotType, "OnTryPurchase", prefix: PatchHelper.Method(typeof(MerchantSelectionConfirmationPatches), nameof(MerchantSlotTryPurchasePrefix)));
    }

    public static void InventoryReadyPostfix(NMerchantInventory __instance)
    {
        EnsureConfirmButton(__instance);
    }

    public static void ClearInventorySelectionPrefix(NMerchantInventory __instance)
    {
        ClearPendingSelection(__instance);
    }

    public static void RefreshInventoryPostfix(NMerchantInventory __instance)
    {
        RefreshConfirmButtonState(__instance);
    }

    public static bool MerchantSlotGuiInputPrefix(NMerchantSlot __instance, InputEvent inputEvent)
    {
        try
        {
            if (!ShouldConfirmSlotSelection(__instance, out var inventory))
                return true;
            if (!inputEvent.IsActionPressed(MegaInput.select))
                return true;
            SelectSlotForConfirmation(inventory, __instance);
            __instance.GetViewport()?.SetInputAsHandled();
            return false;
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Merchant mobile _GuiInput confirmation failed; falling back to vanilla input: {exception.Message}");
            return true;
        }
    }

    public static bool MerchantSlotMouseReleasedPrefix(NMerchantSlot __instance, InputEvent inputEvent)
    {
        try
        {
            if (!ShouldConfirmSlotSelection(__instance, out var inventory))
                return true;
            if (GetField(__instance, "_isHovered") is not true || GetField(__instance, "_ignoreMouseRelease") is true)
                return true;
            if (inputEvent is not InputEventMouseButton { ButtonIndex: MouseButton.Left })
                return true;
            SelectSlotForConfirmation(inventory, __instance);
            __instance.GetViewport()?.SetInputAsHandled();
            return false;
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Merchant mobile mouse confirmation failed; falling back to vanilla input: {exception.Message}");
            return true;
        }
    }

    public static bool MerchantSlotSelectedPrefix(NMerchantSlot __instance, ref Task __result)
    {
        try
        {
            if (!ShouldConfirmSlotSelection(__instance, out var inventory))
                return true;
            if (ConsumePurchaseAllowance(__instance))
                return true;

            SelectSlotForConfirmation(inventory, __instance);
            __result = Task.CompletedTask;
            return false;
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Merchant mobile selection confirmation failed; falling back to vanilla purchase: {exception.Message}");
            return true;
        }
    }

    public static bool MerchantSlotTryPurchasePrefix(NMerchantSlot __instance, ref Task __result)
    {
        try
        {
            if (!ShouldConfirmSlotSelection(__instance, out var inventory))
                return true;
            if (ConsumePurchaseAllowance(__instance))
                return true;

            SelectSlotForConfirmation(inventory, __instance);
            __result = Task.CompletedTask;
            PatchHelper.Log($"Merchant purchase intercepted for mobile confirmation: {__instance.GetType().Name}");
            return false;
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Merchant mobile purchase confirmation failed; falling back to vanilla purchase: {exception.Message}");
            return true;
        }
    }

    private static void EnsureConfirmButton(NMerchantInventory inventory)
    {
        try
        {
            var state = States.GetOrCreateValue(inventory);
            if (state.ConfirmButton != null && GodotObject.IsInstanceValid(state.ConfirmButton))
                return;

            var button = inventory.GetNodeOrNull<NConfirmButton>("%ConfirmButton")
                ?? inventory.GetNodeOrNull<NConfirmButton>("ConfirmButton")
                ?? inventory.GetNodeOrNull<NConfirmButton>(ConfirmButtonName)
                ?? CreateConfirmButton();
            if (button == null)
                return;

            if (button.GetParent() == null)
                inventory.AddChild(button);
            button.Name = ConfirmButtonName;
            button.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ => OnConfirmButtonReleased(inventory)));
            button.Disable();
            state.ConfirmButton = button;
            PatchHelper.Log("Merchant mobile selection confirmation button ready.");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Inject merchant confirmation button failed: {exception.Message}");
        }
    }

    private static NConfirmButton CreateConfirmButton()
    {
        var packed = ResourceLoader.Load<PackedScene>("res://scenes/ui/confirm_button.tscn");
        return packed?.Instantiate<NConfirmButton>();
    }

    private static void SelectSlotForConfirmation(NMerchantInventory inventory, NMerchantSlot slot)
    {
        if (!IsEnabled() || inventory == null || slot == null || IsInputBlocked(inventory) || !inventory.IsOpen || slot.Entry == null || !slot.Entry.IsStocked)
            return;
        EnsureConfirmButton(inventory);
        var state = States.GetOrCreateValue(inventory);
        state.PendingPurchaseSlot = slot;
        slot.TryGrabFocus();
        RefreshConfirmButtonState(inventory);
        // Match the reference fix 004b63f3: do not force ActiveScreenContext.Update()
        // here, because it can move the virtual finger/focus away from the slot.
    }

    private static async void OnConfirmButtonReleased(NMerchantInventory inventory)
    {
        var state = GetState(inventory);
        var pendingPurchaseSlot = state?.PendingPurchaseSlot;
        ClearPendingSelection(inventory);
        if (pendingPurchaseSlot == null || !GodotObject.IsInstanceValid(pendingPurchaseSlot) || !pendingPurchaseSlot.IsInsideTree())
            return;
        var entry = pendingPurchaseSlot.Entry;
        if (entry == null || !entry.IsStocked)
            return;
        await TryPurchaseNow(pendingPurchaseSlot);
    }

    private static async Task TryPurchaseNow(NMerchantSlot slot)
    {
        var clearHoverTip = typeof(NMerchantSlot).GetMethod("ClearHoverTip", InstanceFlags);
        clearHoverTip?.Invoke(slot, null);
        var onTryPurchase = slot.GetType().GetMethod("OnTryPurchase", InstanceFlags)
            ?? typeof(NMerchantSlot).GetMethod("OnTryPurchase", InstanceFlags);
        var inventory = GetMerchantInventory(slot)?.Inventory;
        AllowNextPurchase(slot);
        try
        {
            if (onTryPurchase?.Invoke(slot, new object[] { inventory }) is Task task)
                await task;
        }
        finally
        {
            ClearPurchaseAllowance(slot);
        }
    }

    private static void ClearPendingSelection(NMerchantInventory inventory)
    {
        var state = GetState(inventory);
        if (state != null)
            state.PendingPurchaseSlot = null;
        RefreshConfirmButtonState(inventory);
    }

    private static void RefreshConfirmButtonState(NMerchantInventory inventory)
    {
        var state = GetState(inventory);
        var button = state?.ConfirmButton;
        if (button == null || !GodotObject.IsInstanceValid(button))
            return;
        if (IsEnabled()
            && !IsInputBlocked(inventory)
            && inventory.IsOpen
            && state.PendingPurchaseSlot != null
            && GodotObject.IsInstanceValid(state.PendingPurchaseSlot)
            && state.PendingPurchaseSlot.Entry?.IsStocked == true)
            button.Enable();
        else
            button.Disable();
    }

    private static bool ShouldConfirmSlotSelection(NMerchantSlot slot, out NMerchantInventory inventory)
    {
        inventory = GetMerchantInventory(slot);
        var entry = slot?.Entry;
        return IsEnabled()
            && inventory != null
            && entry != null
            && entry.IsStocked
            && !IsInputBlocked(inventory)
            && inventory.IsOpen;
    }

    private static bool IsEnabled()
    {
        return OS.HasFeature("mobile")
            && AndroidSettingsBridge.GetBool("mobile_selection_confirmation", true);
    }

    private static bool IsInputBlocked(NMerchantInventory inventory)
    {
        return GetField(inventory, "_isInputBlocked") is true;
    }

    private static void AllowNextPurchase(NMerchantSlot slot)
    {
        if (slot == null)
            return;
        lock (AllowedPurchasesLock)
            AllowedPurchases.Add(slot);
    }

    private static bool ConsumePurchaseAllowance(NMerchantSlot slot)
    {
        if (slot == null)
            return false;
        lock (AllowedPurchasesLock)
        {
            if (!AllowedPurchases.Remove(slot))
                return false;
            return true;
        }
    }

    private static void ClearPurchaseAllowance(NMerchantSlot slot)
    {
        if (slot == null)
            return;
        lock (AllowedPurchasesLock)
            AllowedPurchases.Remove(slot);
    }

    private static NMerchantInventory GetMerchantInventory(NMerchantSlot slot)
    {
        return GetField(slot, "_merchantRug") as NMerchantInventory;
    }

    private static object GetField(object target, string name)
    {
        return target?.GetType().GetField(name, InstanceFlags)?.GetValue(target);
    }

    private static MerchantConfirmState GetState(NMerchantInventory inventory)
    {
        return inventory != null && States.TryGetValue(inventory, out var state) ? state : null;
    }

    private sealed class MerchantConfirmState
    {
        public NMerchantSlot PendingPurchaseSlot;
        public NConfirmButton ConfirmButton;
    }
}
