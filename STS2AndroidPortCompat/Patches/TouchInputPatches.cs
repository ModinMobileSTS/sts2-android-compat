using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using STS2Mobile.Android;

namespace STS2Mobile.Patches;

public static class TouchInputPatches
{
    private static readonly ConditionalWeakTable<Type, InputMembers> Members = new();
    private static readonly ConditionalWeakTable<object, TargetState> TargetStates = new ConditionalWeakTable<object, TargetState>();
    private static readonly AccessTools.FieldRef<NCursorManager, Image> LastCursorImage = AccessTools.FieldRefAccess<NCursorManager, Image>("_lastSetCursor");
    private static readonly AccessTools.FieldRef<NCursorManager, bool> CursorUsesController = AccessTools.FieldRefAccess<NCursorManager, bool>("_isUsingController");
    private static readonly AccessTools.FieldRef<NCursorManager, bool> CursorShouldShow = AccessTools.FieldRefAccess<NCursorManager, bool>("_shouldShowCursor");
    private static readonly Action<NCursorManager> UpdateCursor = (Action<NCursorManager>)AccessTools.Method(typeof(NCursorManager), "UpdateCursor").CreateDelegate(typeof(Action<NCursorManager>));

    public static void Apply(Harmony harmony)
    {
        var mouseCardPlayType = typeof(NMouseCardPlay);
        PatchHelper.Patch(harmony, mouseCardPlayType, "_Input", postfix: PatchHelper.Method(typeof(TouchInputPatches), nameof(MouseCardPlayInputPostfix)));
        PatchHelper.Patch(harmony, mouseCardPlayType, "OnCancelPlayCard", postfix: PatchHelper.Method(typeof(TouchInputPatches), nameof(MouseCardPlayCancelPostfix)));

        var targetManagerType = typeof(NTargetManager);
        GetMembers(mouseCardPlayType);
        GetMembers(targetManagerType);
        foreach (var method in targetManagerType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (method.Name == "StartTargeting")
                harmony.Patch(method, postfix: new HarmonyMethod(PatchHelper.Method(typeof(TouchInputPatches), nameof(TargetManagerStartPostfix))));
        }
        PatchHelper.Patch(harmony, targetManagerType, "_Input", prefix: PatchHelper.Method(typeof(TouchInputPatches), nameof(TargetManagerInputPrefix)));

        var cursorPrefix = PatchHelper.Method(typeof(TouchInputPatches), nameof(TargetingCursorFinishPrefix));
        var cursorPostfix = PatchHelper.Method(typeof(TouchInputPatches), nameof(TargetingCursorFinishPostfix));
        PatchHelper.Patch(harmony, typeof(NTargetingArrow), "StopDrawing", prefix: cursorPrefix, postfix: cursorPostfix);
        // Older targets have no declared exit callback; never patch Godot's inherited method.
        var arrowExit = typeof(NTargetingArrow).GetMethod("_ExitTree", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        if (arrowExit != null)
            harmony.Patch(arrowExit, prefix: new HarmonyMethod(cursorPrefix), postfix: new HarmonyMethod(cursorPostfix));
    }

    public static void ResetForInputModeChange()
    {
        TargetStates.Clear();
    }

    public static void TargetingCursorFinishPrefix(bool ____followMouse, out bool __state)
    {
        __state = ____followMouse && !MobileInputPolicyPatches.UseTouchControls
            && Input.MouseMode == Input.MouseModeEnum.Hidden
            && OS.GetName().Equals("Android", StringComparison.OrdinalIgnoreCase);
    }

    public static void TargetingCursorFinishPostfix(bool __state)
    {
        if (!__state || MobileInputPolicyPatches.UseTouchControls || Input.MouseMode != Input.MouseModeEnum.Visible)
            return;
        try
        {
            var game = NGame.Instance;
            if (game == null || !GodotObject.IsInstanceValid(game))
                return;
            var cursor = game.CursorManager;
            if (cursor == null || !GodotObject.IsInstanceValid(cursor) || !cursor.IsInsideTree()
                || CursorUsesController(cursor) || !CursorShouldShow(cursor))
                return;
            // Android 4.5.1 can retain TYPE_NULL after Hidden -> Visible when the
            // shape is unchanged. Reapply the game's current image/hotspot once;
            // do not change MouseMode or bypass an intentional hide.
            LastCursorImage(cursor) = null;
            UpdateCursor(cursor);
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Targeting cursor refresh failed: {exception.Message}");
        }
    }


    public static void MouseCardPlayInputPostfix(object __instance, InputEvent inputEvent)
    {
        try
        {
            if (!IsLeftRelease(inputEvent) || !MobileInputPolicyPatches.IsTouchPointerEvent(inputEvent) || !IsTouchOptimized())
                return;

            var members = GetMembers(__instance.GetType());
            if (members.IsInPlayZone == null || !(bool)members.IsInPlayZone.Invoke(__instance, null))
            {
                members.CancelPlay?.Invoke(__instance, null);
                PatchHelper.Log("Touch input cancelled card play: released outside play zone.");
            }
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"MouseCardPlayInputPostfix failed: {exception.Message}");
        }
    }

    public static void MouseCardPlayCancelPostfix(object __instance)
    {
        try
        {
            var targetManager = NTargetManager.Instance;
            if (targetManager != null)
                ConsumeCancelledByUntargetedRelease(targetManager);
            var holder = GetMembers(__instance.GetType()).Holder?.GetValue(__instance);
            if (holder is NHandCardHolder handHolder)
                MobileTapPreviewPatches.RepinAfterCancelledCardPlay(handHolder);
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"MouseCardPlayCancelPostfix failed: {exception.Message}");
        }
    }

    public static void TargetManagerStartPostfix(object __instance)
    {
        TargetStates.GetOrCreateValue(__instance).CancelledByUntargetedRelease = false;
    }

    public static bool TargetManagerInputPrefix(object __instance, InputEvent inputEvent)
    {
        try
        {
            if (!IsLeftRelease(inputEvent) || !MobileInputPolicyPatches.IsTouchPointerEvent(inputEvent) || !IsTouchOptimized())
                return true;
            var members = GetMembers(__instance.GetType());
            var targetMode = members.TargetMode?.GetValue(__instance);
            if (targetMode == null || targetMode.ToString() != "ReleaseMouseToTarget")
                return true;
            var hoveredNode = members.HoveredNode?.GetValue(__instance);
            if (hoveredNode != null)
                return true;
            TargetStates.GetOrCreateValue(__instance).CancelledByUntargetedRelease = true;
            members.FinishTargeting?.Invoke(__instance, new object[] { true });
            return false;
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"TargetManagerInputPrefix failed: {exception.Message}");
            return true;
        }
    }

    public static bool ConsumeCancelledByUntargetedRelease(object targetManager)
    {
        if (!TargetStates.TryGetValue(targetManager, out var state))
            return false;
        var value = state.CancelledByUntargetedRelease;
        state.CancelledByUntargetedRelease = false;
        return value;
    }

    private static bool IsTouchOptimized()
    {
        return MobileInputPolicyPatches.UseTouchControls && (AndroidSettingsBridge.GetBool("touch_lift_preview", true)
            || AndroidSettingsBridge.GetBool("mobile_selection_confirmation", true));
    }

    private static bool IsLeftRelease(InputEvent inputEvent)
    {
        return inputEvent is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false }
            || inputEvent is InputEventScreenTouch { Pressed: false };
    }

    private static InputMembers GetMembers(Type type) => Members.GetValue(type, static key => new InputMembers(key));




    private sealed class TargetState
    {
        public bool CancelledByUntargetedRelease;
    }

    private sealed class InputMembers
    {
        public readonly MethodInfo IsInPlayZone;
        public readonly MethodInfo CancelPlay;
        public readonly MethodInfo FinishTargeting;
        public readonly FieldInfo Holder;
        public readonly FieldInfo TargetMode;
        public readonly PropertyInfo HoveredNode;

        public InputMembers(Type type)
        {
            const BindingFlags instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            IsInPlayZone = type.GetMethod("IsCardInPlayZone", BindingFlags.NonPublic | BindingFlags.Instance);
            CancelPlay = type.GetMethod("CancelPlayCard", BindingFlags.Public | BindingFlags.Instance);
            FinishTargeting = type.GetMethod("FinishTargeting", BindingFlags.NonPublic | BindingFlags.Instance);
            Holder = type.GetField("Holder", instance) ?? type.BaseType?.GetField("Holder", instance);
            TargetMode = type.GetField("_targetMode", instance);
            HoveredNode = type.GetProperty("HoveredNode", instance);
        }
    }
}
