using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using STS2Mobile.Android;

namespace STS2Mobile.Patches;

public static class TouchInputPatches
{
    private static readonly ConditionalWeakTable<Type, InputMembers> Members = new();
    private static readonly ConditionalWeakTable<object, TargetState> TargetStates = new ConditionalWeakTable<object, TargetState>();

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
    }


    public static void MouseCardPlayInputPostfix(object __instance, InputEvent inputEvent)
    {
        try
        {
            if (!IsLeftRelease(inputEvent) || !IsTouchOptimized())
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
            if (!IsLeftRelease(inputEvent) || !IsTouchOptimized())
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
        return OS.HasFeature("mobile") && (AndroidSettingsBridge.GetBool("touch_lift_preview", true)
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
