using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using STS2Mobile.Android;

namespace STS2Mobile.Patches;

/// <summary>
/// Mobile-only tooltip gate. PC hover shows tooltips immediately, which is
/// noisy on touch screens because a normal tap first becomes a hover. In
/// long-press mode we let the original game create and align the tooltip, hide
/// it immediately, then reveal it only while the same touch/hover owner is held
/// long enough.
/// </summary>
public static class MobileTooltipPatches
{
    private const string SettingTooltipMode = "mobile_tooltip_mode";
    private const string SettingLongPressMs = "mobile_tooltip_long_press_ms";
    private const string ModeImmediate = "immediate";
    private const string ModeLongPress = "long_press";
    private const string ModeHidden = "hidden";
    private const int DefaultLongPressMs = 1000;
    private const int MinLongPressMs = 500;
    private const int MaxLongPressMs = 10000;
    private const float MoveCancelThreshold = 42f;

    private static readonly Dictionary<ulong, WeakReference<Control>> Owners = new();
    private static readonly Dictionary<ulong, WeakReference<NHoverTipSet>> Tips = new();
    private static readonly ConditionalWeakTable<Control, ConnectedOwnerState> ConnectedOwnerStates = new();
    private static readonly FieldInfo ActiveHoverTipsField = typeof(NHoverTipSet).GetField("_activeHoverTips", BindingFlags.NonPublic | BindingFlags.Static);
    private static readonly FieldInfo TipOwnerField = typeof(NHoverTipSet).GetField("_owner", BindingFlags.NonPublic | BindingFlags.Instance);

    private static string _lastAppliedMode;
    private static string _cachedMode = ModeImmediate;
    private static ulong _nextModePollMsec;
    private static bool _pressActive;
    private static bool _longPressElapsed;
    private static ulong _pressSerial;
    private static ulong _pressStartMsec;
    private static Vector2 _pressStartPosition;
    private static Vector2 _lastPressPosition;
    private static WeakReference<Control> _pendingOwner;
    private static WeakReference<Control> _revealedOwner;
    private static int _clearDepth;
    private static bool _forceRemoving;

    public static void Apply(Harmony harmony)
    {
        try
        {
            var prefix = new HarmonyMethod(PatchHelper.Method(typeof(MobileTooltipPatches), nameof(CreateAndShowPrefix)));
            var postfix = new HarmonyMethod(PatchHelper.Method(typeof(MobileTooltipPatches), nameof(CreateAndShowPostfix)));
            foreach (var method in typeof(NHoverTipSet).GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (method.Name == "CreateAndShow" || method.Name == "CreateAndShowMapPointHistory")
                {
                    harmony.Patch(method, prefix: prefix, postfix: postfix);
                    PatchHelper.Log($"Patched {typeof(NHoverTipSet).FullName}.{method.Name} for mobile tooltip mode");
                }
            }
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"FAILED {typeof(NHoverTipSet).FullName}.CreateAndShow mobile tooltip patch: {exception}");
        }

        PatchHelper.Patch(harmony, typeof(NHoverTipSet), "Remove", prefix: PatchHelper.Method(typeof(MobileTooltipPatches), nameof(RemovePrefix)));
        PatchHelper.Patch(harmony, typeof(NHoverTipSet), "Clear", prefix: PatchHelper.Method(typeof(MobileTooltipPatches), nameof(ClearPrefix)), postfix: PatchHelper.Method(typeof(MobileTooltipPatches), nameof(ClearPostfix)));
        PatchHelper.Patch(harmony, typeof(NHoverTipSet), "_Process", postfix: PatchHelper.Method(typeof(MobileTooltipPatches), nameof(HoverTipProcessPostfix)));
        PatchHelper.Patch(harmony, typeof(NGame), "_Input", postfix: PatchHelper.Method(typeof(MobileTooltipPatches), nameof(GameInputPostfix)));
    }

    public static bool CreateAndShowPrefix(Control __0, ref NHoverTipSet __result)
    {
        try
        {
            var owner = __0;
            if (owner == null || IsExplicitDetailOwner(owner))
                return true;

            var mode = GetTooltipMode();
            if (!ShouldManage(mode))
                return true;

            if (mode == ModeHidden)
            {
                __result = null;
                return false;
            }

            EnsureLongPressTracking(owner);
            return true;
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Mobile tooltip CreateAndShow prefix failed: {exception.Message}");
            return true;
        }
    }

    public static void CreateAndShowPostfix(Control __0, NHoverTipSet __result)
    {
        try
        {
            var owner = __0;
            if (owner == null || __result == null)
                return;
            if (IsExplicitDetailOwner(owner))
                return;

            Track(owner, __result);

            var mode = GetTooltipMode();
            if (!ShouldManage(mode))
                return;

            if (mode == ModeHidden)
            {
                SetTipVisible(__result, false);
                return;
            }

            if (!_pressActive)
                BeginPress(GetCurrentPointerPosition(owner), owner);

            if (_pressActive && IsPressOwner(owner))
                SetWeak(ref _pendingOwner, owner);

            CheckLongPressElapsed();
            if (_pressActive && _longPressElapsed && IsPressOwner(owner))
            {
                RevealOwner(owner);
                return;
            }

            SetTipVisible(__result, false);
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Mobile tooltip CreateAndShow postfix failed: {exception.Message}");
        }
    }

    public static bool RemovePrefix(Control __0)
    {
        try
        {
            var owner = __0;
            var preservePressOwner = !_forceRemoving
                && _pressActive
                && GetTooltipMode() == ModeLongPress
                && IsPressOwner(owner);
            Untrack(owner, preservePressOwner);
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Mobile tooltip Remove prefix failed: {exception.Message}");
        }
        return true;
    }

    public static void ClearPrefix()
    {
        _clearDepth++;
    }

    public static void ClearPostfix()
    {
        try
        {
            if (_clearDepth > 0)
                _clearDepth--;

            // The PC UI can clear/recreate hover tips while the finger is still
            // held over the same card/relic. Do not treat that as touch release;
            // actual releases are handled by input events or direct owner removal.
            if (_pressActive)
            {
                SyncActiveHoverTips();
                return;
            }

            Owners.Clear();
            Tips.Clear();
            ClearPressState(incrementSerial: true);
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Mobile tooltip Clear postfix failed: {exception.Message}");
        }
    }

    public static void GameInputPostfix(InputEvent inputEvent)
    {
        try
        {
            if (GetTooltipMode() != ModeLongPress || !IsMobileRuntime())
            {
                if (_pressActive)
                    EndPress(hideTips: true);
                return;
            }

            HandlePointerInput(null, inputEvent, ownerEvent: false);
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Mobile tooltip input failed: {exception.Message}");
        }
    }

    public static void HoverTipProcessPostfix(NHoverTipSet __instance)
    {
        try
        {
            if (__instance == null || !IsMobileRuntime())
                return;

            var owner = TrackActiveTip(__instance);
            var mode = GetTooltipMode();
            if (!ShouldManage(mode))
                return;

            if (mode == ModeLongPress)
            {
                CheckLongPressElapsed();
                if (_pressActive && _longPressElapsed && owner != null && IsPressOwner(owner))
                    RevealOwner(owner);
            }

            if (mode == ModeHidden || !IsRevealedTip(__instance))
                SetTipVisible(__instance, false);
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Mobile tooltip process visibility failed: {exception.Message}");
        }
    }

    public static void RefreshModeFromSettings()
    {
        try
        {
            AndroidSettingsBridge.InvalidateCache();
            var mode = ReadTooltipMode();
            _cachedMode = mode;
            _lastAppliedMode = mode;
            _nextModePollMsec = 0;
            ApplyModeVisibility(mode);
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Mobile tooltip settings refresh failed: {exception.Message}");
        }
    }

    private static void OnOwnerGuiInput(Control owner, InputEvent inputEvent)
    {
        try
        {
            if (GetTooltipMode() != ModeLongPress || !IsMobileRuntime())
                return;
            HandlePointerInput(owner, inputEvent, ownerEvent: true);
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Mobile tooltip owner input failed: {exception.Message}");
        }
    }

    private static void OnOwnerMouseExited(Control owner)
    {
        // Do not cancel a touch long-press on MouseExited alone: card/slot hover
        // animations can move the Control under a stationary finger. Finger
        // movement and release still cancel/hide the tooltip.
    }

    private static void HandlePointerInput(Control owner, InputEvent inputEvent, bool ownerEvent)
    {
        if (TryGetPressPosition(inputEvent, out var pressPosition))
        {
            if (_pressActive)
            {
                if (owner != null && Resolve(_pendingOwner) == null)
                    SetWeak(ref _pendingOwner, owner);
                return;
            }
            var startPosition = ownerEvent && owner != null ? GetCurrentPointerPosition(owner) : pressPosition;
            BeginPress(startPosition, owner ?? FindOwnerAtPosition(startPosition));
            return;
        }

        if (TryGetReleasePosition(inputEvent, out _))
        {
            EndPress(hideTips: true);
            return;
        }

        // Control.GuiInput positions are local to the owner, while NGame._Input
        // positions are viewport/global. Avoid mixing coordinate spaces for the
        // drag-cancel threshold; global motion is enough to cancel real drags.
        if (!ownerEvent && TryGetMotionPosition(inputEvent, out var motionPosition))
            UpdatePressMotion(motionPosition);
    }

    private static void EnsureLongPressTracking(Control owner)
    {
        if (owner == null || !GodotObject.IsInstanceValid(owner))
            return;

        var current = Resolve(_pendingOwner) ?? Resolve(_revealedOwner);
        if (!_pressActive || (current != null && !IsSameOwner(owner, current)))
        {
            BeginPress(GetCurrentPointerPosition(owner), owner);
            return;
        }

        if (current == null)
            SetWeak(ref _pendingOwner, owner);
    }

    private static void BeginPress(Vector2 position, Control owner = null)
    {
        HideAllManagedTips();
        _pressActive = true;
        _longPressElapsed = false;
        _pressSerial++;
        _pressStartMsec = Time.GetTicksMsec();
        _pressStartPosition = position;
        _lastPressPosition = position;
        SetWeak(ref _pendingOwner, owner ?? FindOwnerAtPosition(position));
        _revealedOwner = null;
        ArmLongPressTimer(_pressSerial);
    }

    private static void UpdatePressMotion(Vector2 position)
    {
        if (!_pressActive)
            return;
        _lastPressPosition = position;
        var delta = position - _pressStartPosition;
        if (delta.LengthSquared() > MoveCancelThreshold * MoveCancelThreshold)
            EndPress(hideTips: true);
    }

    private static void EndPress(bool hideTips)
    {
        if (hideTips)
            HideAllManagedTips();
        ClearPressState(incrementSerial: true);
    }

    private static void ClearPressState(bool incrementSerial)
    {
        _pressActive = false;
        _longPressElapsed = false;
        if (incrementSerial)
            _pressSerial++;
        _pendingOwner = null;
        _revealedOwner = null;
        _pressStartMsec = 0;
        _pressStartPosition = Vector2.Zero;
        _lastPressPosition = Vector2.Zero;
    }

    private static void ArmLongPressTimer(ulong serial)
    {
        try
        {
            var tree = NGame.Instance?.GetTree();
            if (tree == null)
                return;
            var timer = tree.CreateTimer(GetLongPressSeconds());
            timer.Connect(SceneTreeTimer.SignalName.Timeout, Callable.From(() => OnLongPressTimerTimeout(serial)));
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Mobile tooltip long-press timer failed: {exception.Message}");
        }
    }

    private static void OnLongPressTimerTimeout(ulong serial)
    {
        try
        {
            if (!_pressActive || serial != _pressSerial || GetTooltipMode() != ModeLongPress || !IsMobileRuntime())
                return;
            RevealLongPressOwner();
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Mobile tooltip long-press reveal failed: {exception.Message}");
        }
    }

    private static void CheckLongPressElapsed()
    {
        if (!_pressActive || _longPressElapsed || _pressStartMsec == 0)
            return;
        var elapsed = Time.GetTicksMsec() - _pressStartMsec;
        if (elapsed >= (ulong)GetLongPressMs())
            RevealLongPressOwner();
    }

    private static void RevealLongPressOwner()
    {
        _longPressElapsed = true;
        SyncActiveHoverTips();
        var owner = Resolve(_pendingOwner) ?? FindOwnerAtPosition(_lastPressPosition);
        if (owner == null)
            return;
        RevealOwner(owner);
    }

    private static bool RevealOwner(Control owner)
    {
        if (owner == null || !TryGetTip(owner, out var tip))
            return false;
        HideAllManagedTips(exceptOwner: owner);
        SetTipVisible(tip, true);
        SetWeak(ref _pendingOwner, owner);
        SetWeak(ref _revealedOwner, owner);
        return true;
    }

    private static bool IsPressOwner(Control owner)
    {
        if (owner == null)
            return false;
        var current = Resolve(_pendingOwner) ?? Resolve(_revealedOwner);
        return current == null || IsSameOwner(owner, current);
    }

    private static bool IsRevealedTip(NHoverTipSet tip)
    {
        var owner = Resolve(_revealedOwner);
        return owner != null && TryGetTip(owner, out var revealedTip) && ReferenceEquals(revealedTip, tip);
    }

    private static void Track(Control owner, NHoverTipSet tip)
    {
        if (owner == null || tip == null)
            return;
        var key = owner.GetInstanceId();
        Owners[key] = new WeakReference<Control>(owner);
        Tips[key] = new WeakReference<NHoverTipSet>(tip);
        ConnectOwnerInput(owner);
        CleanupDeadEntries();
    }

    private static Control TrackActiveTip(NHoverTipSet tip)
    {
        if (tip == null || !GodotObject.IsInstanceValid(tip))
            return null;
        var owner = GetTipOwner(tip);
        if (owner != null && !IsExplicitDetailOwner(owner))
        {
            Track(owner, tip);
            return owner;
        }
        return null;
    }

    private static void SyncActiveHoverTips()
    {
        try
        {
            if (ActiveHoverTipsField?.GetValue(null) is not System.Collections.IDictionary active)
                return;
            foreach (System.Collections.DictionaryEntry entry in active)
            {
                if (entry.Key is Control owner && entry.Value is NHoverTipSet tip && !IsExplicitDetailOwner(owner))
                    Track(owner, tip);
            }
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Mobile tooltip active-tip sync failed: {exception.Message}");
        }
    }

    private static Control GetTipOwner(NHoverTipSet tip)
    {
        try
        {
            if (TipOwnerField?.GetValue(tip) is Control owner && GodotObject.IsInstanceValid(owner))
                return owner;
        }
        catch
        {
        }
        try
        {
            if (ActiveHoverTipsField?.GetValue(null) is System.Collections.IDictionary active)
            {
                foreach (System.Collections.DictionaryEntry entry in active)
                {
                    if (ReferenceEquals(entry.Value, tip) && entry.Key is Control owner && GodotObject.IsInstanceValid(owner))
                        return owner;
                }
            }
        }
        catch
        {
        }
        return null;
    }

    private static void Untrack(Control owner, bool preservePressOwner = false)
    {
        if (owner == null)
            return;
        var key = owner.GetInstanceId();
        Owners.Remove(key);
        Tips.Remove(key);
        if (!preservePressOwner && IsSameOwner(owner, Resolve(_pendingOwner)))
            _pendingOwner = null;
        if (!preservePressOwner && IsSameOwner(owner, Resolve(_revealedOwner)))
            _revealedOwner = null;
    }

    private static void ConnectOwnerInput(Control owner)
    {
        try
        {
            if (owner == null || !GodotObject.IsInstanceValid(owner) || ConnectedOwnerStates.TryGetValue(owner, out _))
                return;
            ConnectedOwnerStates.GetOrCreateValue(owner);
            owner.Connect(Control.SignalName.GuiInput, Callable.From<InputEvent>(inputEvent => OnOwnerGuiInput(owner, inputEvent)));
            owner.Connect(Control.SignalName.MouseExited, Callable.From(() => OnOwnerMouseExited(owner)));
            owner.Connect(Node.SignalName.TreeExiting, Callable.From(() => Untrack(owner)));
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Mobile tooltip owner signal bridge failed: {exception.Message}");
        }
    }

    private static Control FindOwnerAtPosition(Vector2 position)
    {
        CleanupDeadEntries();
        Control best = null;
        var bestArea = float.PositiveInfinity;
        foreach (var item in Owners)
        {
            if (!TryResolve(item.Value, out var owner) || !IsPointInside(owner, position))
                continue;
            var rect = owner.GetGlobalRect();
            var area = Math.Max(1f, rect.Size.X * rect.Size.Y);
            if (area <= bestArea)
            {
                best = owner;
                bestArea = area;
            }
        }
        return best;
    }

    private static bool TryGetTip(Control owner, out NHoverTipSet tip)
    {
        tip = null;
        if (owner == null)
            return false;
        if (Tips.TryGetValue(owner.GetInstanceId(), out var weak) && TryResolve(weak, out tip))
            return true;
        SyncActiveHoverTips();
        return Tips.TryGetValue(owner.GetInstanceId(), out weak) && TryResolve(weak, out tip);
    }

    private static void HideAllManagedTips(Control exceptOwner = null)
    {
        SyncActiveHoverTips();
        CleanupDeadEntries();
        var exceptKey = exceptOwner?.GetInstanceId();
        foreach (var item in Tips)
        {
            if (exceptKey.HasValue && item.Key == exceptKey.Value)
                continue;
            if (Owners.TryGetValue(item.Key, out var ownerRef) && TryResolve(ownerRef, out var owner) && IsExplicitDetailOwner(owner))
                continue;
            if (TryResolve(item.Value, out var tip))
                SetTipVisible(tip, false);
        }
        HideHoverTipNodes(exceptOwner);
    }

    private static void ApplyModeVisibility(string mode)
    {
        if (!IsMobileRuntime())
            return;
        if (mode == ModeImmediate)
        {
            ClearPressState(incrementSerial: true);
            ShowAllManagedTips();
            return;
        }
        ClearPressState(incrementSerial: true);
        RemoveAllManagedTips();
    }

    private static void ShowAllManagedTips()
    {
        SyncActiveHoverTips();
        CleanupDeadEntries();
        foreach (var item in Tips)
        {
            if (TryResolve(item.Value, out var tip))
                SetTipVisible(tip, true);
        }
        ShowHoverTipNodes();
    }

    private static void RemoveAllManagedTips()
    {
        SyncActiveHoverTips();
        var owners = new List<Control>();
        foreach (var item in Owners)
        {
            if (TryResolve(item.Value, out var owner) && !IsExplicitDetailOwner(owner))
                owners.Add(owner);
        }
        try
        {
            if (ActiveHoverTipsField?.GetValue(null) is System.Collections.IDictionary active)
            {
                foreach (System.Collections.DictionaryEntry entry in active)
                {
                    if (entry.Key is Control owner && !IsExplicitDetailOwner(owner))
                        owners.Add(owner);
                }
            }
        }
        catch
        {
        }

        _forceRemoving = true;
        try
        {
            var seen = new HashSet<ulong>();
            foreach (var owner in owners)
            {
                if (owner == null || !GodotObject.IsInstanceValid(owner) || !seen.Add(owner.GetInstanceId()))
                    continue;
                try
                {
                    NHoverTipSet.Remove(owner);
                }
                catch
                {
                }
            }
        }
        finally
        {
            _forceRemoving = false;
        }

        HideHoverTipNodes();
        Owners.Clear();
        Tips.Clear();
    }

    private static void SetTipVisible(NHoverTipSet tip, bool visible)
    {
        if (tip == null || !GodotObject.IsInstanceValid(tip))
            return;
        tip.Visible = visible;
    }

    private static void HideHoverTipNodes(Control exceptOwner = null)
    {
        var container = NGame.Instance?.HoverTipsContainer;
        if (container == null || !GodotObject.IsInstanceValid(container))
            return;
        var exceptTip = exceptOwner != null && TryGetTip(exceptOwner, out var tip) ? tip : null;
        foreach (var child in container.GetChildren())
        {
            if (child is not NHoverTipSet hoverTip || !GodotObject.IsInstanceValid(hoverTip))
                continue;
            if (exceptTip != null && ReferenceEquals(hoverTip, exceptTip))
                continue;
            if (IsExplicitDetailTip(hoverTip))
                continue;
            hoverTip.Visible = false;
        }
    }

    private static void ShowHoverTipNodes()
    {
        var container = NGame.Instance?.HoverTipsContainer;
        if (container == null || !GodotObject.IsInstanceValid(container))
            return;
        foreach (var child in container.GetChildren())
        {
            if (child is NHoverTipSet hoverTip && GodotObject.IsInstanceValid(hoverTip) && !IsExplicitDetailTip(hoverTip))
                hoverTip.Visible = true;
        }
    }

    private static bool IsExplicitDetailTip(NHoverTipSet tip)
    {
        var owner = GetTipOwner(tip);
        return owner != null && IsExplicitDetailOwner(owner);
    }

    private static bool IsPointInside(Control owner, Vector2 position)
    {
        try
        {
            return owner != null
                && GodotObject.IsInstanceValid(owner)
                && owner.IsInsideTree()
                && owner.GetGlobalRect().HasPoint(position);
        }
        catch
        {
            return false;
        }
    }

    private static void CleanupDeadEntries()
    {
        var dead = new List<ulong>();
        foreach (var item in Owners)
        {
            if (!TryResolve(item.Value, out _))
                dead.Add(item.Key);
        }
        foreach (var item in Tips)
        {
            if (!TryResolve(item.Value, out _))
                dead.Add(item.Key);
        }
        foreach (var key in dead)
        {
            Owners.Remove(key);
            Tips.Remove(key);
        }
    }

    private static bool TryResolve<T>(WeakReference<T> weak, out T value) where T : GodotObject
    {
        value = null;
        if (weak == null || !weak.TryGetTarget(out var target) || target == null || !GodotObject.IsInstanceValid(target))
            return false;
        value = target;
        return true;
    }

    private static Control Resolve(WeakReference<Control> weak)
    {
        return TryResolve(weak, out Control owner) ? owner : null;
    }

    private static void SetWeak(ref WeakReference<Control> weak, Control owner)
    {
        weak = owner == null ? null : new WeakReference<Control>(owner);
    }

    private static bool IsSameOwner(Control left, Control right)
    {
        return left != null
            && right != null
            && GodotObject.IsInstanceValid(left)
            && GodotObject.IsInstanceValid(right)
            && left.GetInstanceId() == right.GetInstanceId();
    }

    private static Vector2 GetCurrentPointerPosition(Control owner)
    {
        try
        {
            var viewport = owner?.GetViewport() ?? NGame.Instance?.GetViewport();
            return viewport?.GetMousePosition() ?? Vector2.Zero;
        }
        catch
        {
            return Vector2.Zero;
        }
    }

    private static bool ShouldManage(string mode)
    {
        return IsMobileRuntime() && (mode == ModeLongPress || mode == ModeHidden);
    }

    private static bool IsExplicitDetailOwner(Control owner)
    {
        try
        {
            for (Node node = owner; node != null && GodotObject.IsInstanceValid(node); node = node.GetParent())
            {
                var name = node.GetType().Name;
                if (name == "NInspectCardScreen"
                    || name == "NInspectRelicScreen"
                    || name == "NPotionPopup")
                    return true;
            }
        }
        catch
        {
        }
        return false;
    }

    private static string GetTooltipMode()
    {
        try
        {
            var now = Time.GetTicksMsec();
            if (now >= _nextModePollMsec)
            {
                _nextModePollMsec = now + 250;
                _cachedMode = ReadTooltipMode();
            }
        }
        catch
        {
            _cachedMode = ReadTooltipMode();
        }

        if (_lastAppliedMode == null)
        {
            _lastAppliedMode = _cachedMode;
            return _cachedMode;
        }
        if (!string.Equals(_lastAppliedMode, _cachedMode, StringComparison.Ordinal))
        {
            _lastAppliedMode = _cachedMode;
            ApplyModeVisibility(_cachedMode);
        }
        return _cachedMode;
    }

    private static string ReadTooltipMode()
    {
        try
        {
            var value = AndroidSettingsBridge.GetString(SettingTooltipMode, ModeImmediate)
                .Trim()
                .ToLowerInvariant()
                .Replace('-', '_');
            return value switch
            {
                "longpress" or "long_press" => ModeLongPress,
                "hide" or "hidden" or "off" or "disabled" => ModeHidden,
                _ => ModeImmediate,
            };
        }
        catch
        {
            return ModeImmediate;
        }
    }

    private static int GetLongPressMs()
    {
        var delayMs = AndroidSettingsBridge.GetInt(SettingLongPressMs, DefaultLongPressMs);
        // Earlier preview builds saved 3000 ms. There is no public custom-delay
        // UI, so treat that old default as the current 1 second default.
        if (delayMs == 3000 || delayMs <= 0)
            delayMs = DefaultLongPressMs;
        return Math.Max(MinLongPressMs, Math.Min(MaxLongPressMs, delayMs));
    }

    private static double GetLongPressSeconds()
    {
        return GetLongPressMs() / 1000.0;
    }

    private static bool IsMobileRuntime()
    {
        try
        {
            return OS.HasFeature("mobile") || OS.GetName().Equals("Android", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetPressPosition(InputEvent input, out Vector2 position)
    {
        if (input is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } mouseButton)
        {
            position = mouseButton.Position;
            return true;
        }
        if (input is InputEventScreenTouch { Pressed: true } screenTouch)
        {
            position = screenTouch.Position;
            return true;
        }
        position = Vector2.Zero;
        return false;
    }

    private static bool TryGetReleasePosition(InputEvent input, out Vector2 position)
    {
        if (input is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } mouseButton)
        {
            position = mouseButton.Position;
            return true;
        }
        if (input is InputEventScreenTouch { Pressed: false } screenTouch)
        {
            position = screenTouch.Position;
            return true;
        }
        position = Vector2.Zero;
        return false;
    }

    private static bool TryGetMotionPosition(InputEvent input, out Vector2 position)
    {
        if (input is InputEventMouseMotion mouseMotion)
        {
            position = mouseMotion.Position;
            return true;
        }
        if (input is InputEventScreenDrag screenDrag)
        {
            position = screenDrag.Position;
            return true;
        }
        position = Vector2.Zero;
        return false;
    }

    private sealed class ConnectedOwnerState
    {
    }
}
