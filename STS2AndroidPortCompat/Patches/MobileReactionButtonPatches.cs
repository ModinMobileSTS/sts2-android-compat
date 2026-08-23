using System;
using System.Reflection;
using NumericsMatrix3x2 = System.Numerics.Matrix3x2;
using NumericsVector2 = System.Numerics.Vector2;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Reaction;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using STS2Mobile.Android;

namespace STS2Mobile.Patches;

/// <summary>
/// Restores the mobile reaction affordance that is present in the reference
/// game scene but not in the imported PC payload scene. The payload still owns
/// the reaction wheel, reaction assets, and network synchronizer; this patch
/// only supplies the missing touch button and drives the original wheel state.
/// </summary>
public static class MobileReactionButtonPatches
{
    private const string ButtonName = "AndroidMobileReactionButton";
    private const string WaitingPanelName = "ReadyAndWaitingPanel";
    private const string WaitingOverlayName = "WaitingForOtherPlayers";

    public static void Apply(Harmony harmony)
    {
        PatchHelper.Patch(
            harmony,
            typeof(NGame),
            "_Ready",
            postfix: PatchHelper.Method(typeof(MobileReactionButtonPatches), nameof(GameReadyPostfix)));
        PatchHelper.Patch(
            harmony,
            typeof(NReactionContainer),
            "InitializeNetworking",
            postfix: PatchHelper.Method(typeof(MobileReactionButtonPatches), nameof(ReactionNetworkingChangedPostfix)));
        PatchHelper.Patch(
            harmony,
            typeof(NReactionContainer),
            "DeinitializeNetworking",
            postfix: PatchHelper.Method(typeof(MobileReactionButtonPatches), nameof(ReactionNetworkingChangedPostfix)));
        PatchHelper.Patch(
            harmony,
            typeof(NGame),
            "_Input",
            postfix: PatchHelper.Method(typeof(MobileReactionButtonPatches), nameof(GameInputPostfix)));
    }

    public static void GameReadyPostfix(NGame __instance)
    {
        try
        {
            var existing = __instance.GetNodeOrNull<AndroidMobileReactionButton>(ButtonName);
            if (existing != null)
            {
                existing.InitializeRuntime();
                existing.RefreshVisibility("game-ready-existing", forceLog: true);
                return;
            }

            var button = new AndroidMobileReactionButton
            {
                Name = ButtonName,
                LayoutMode = 1,
                AnchorLeft = 0.5f,
                AnchorTop = 0.5f,
                AnchorRight = 0.5f,
                AnchorBottom = 0.5f,
                OffsetLeft = 648f,
                OffsetTop = 322f,
                OffsetRight = 744f,
                OffsetBottom = 418f,
                GrowHorizontal = Control.GrowDirection.Both,
                GrowVertical = Control.GrowDirection.Both,
                ZIndex = 10,
                MouseFilter = Control.MouseFilterEnum.Pass,
                Visible = false,
            };
            __instance.AddChild(button);
            button.InitializeRuntime();
            button.RefreshVisibility("game-ready", forceLog: true);
            PatchHelper.Log("Installed Android mobile reaction button on NGame.");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Mobile reaction button installation failed: {exception}");
        }
    }

    public static void ReactionNetworkingChangedPostfix()
    {
        try
        {
            var game = NGame.Instance;
            var button = game?.GetNodeOrNull<AndroidMobileReactionButton>(ButtonName);
            button?.RefreshVisibility("reaction-networking-changed", forceLog: true);
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Mobile reaction button network refresh failed: {exception.Message}");
        }
    }

    public static void GameInputPostfix(NGame __instance, InputEvent inputEvent)
    {
        try
        {
            var button = __instance?.GetNodeOrNull<AndroidMobileReactionButton>(ButtonName);
            button?.HandleGlobalInput(inputEvent);
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Mobile reaction button global input bridge failed: {exception.Message}");
        }
    }



    internal static bool IsReactionAvailable(NGame game)
    {
        if (game == null)
            return false;

        if (game.ReactionContainer?.InMultiplayer == true)
            return true;

        // The multiplayer lobby initializes the same network bridge, but the
        // bridge can briefly be unavailable while a lobby screen is opening
        // or replacing its service. The visible remote-player container is
        // deliberately hidden in single-player screens, so it is the stable
        // UI-level multiplayer signal for this transition window.
        return HasVisibleMultiplayerLobby(game.RootSceneContainer?.CurrentScene)
            || HasVisibleWaitingSurface(game.RootSceneContainer?.CurrentScene)
            || HasVisibleWaitingSurface(NOverlayStack.Instance);
    }

    private static bool HasVisibleMultiplayerLobby(Node node)
    {
        if (node == null || (node is CanvasItem canvasItem && !canvasItem.Visible))
            return false;

        if (node.Name == "RemotePlayerContainer"
            || node.Name == "RemotePlayerLoadContainer")
        {
            return true;
        }

        foreach (Node child in node.GetChildren())
        {
            if (HasVisibleMultiplayerLobby(child))
                return true;
        }
        return false;
    }

    private static bool HasVisibleWaitingSurface(Node node)
    {
        if (node == null || (node is CanvasItem canvasItem && !canvasItem.Visible))
            return false;

        if (node.Name == WaitingPanelName
            || node.Name == WaitingOverlayName
            || node.Name == "WaitingForPlayers"
            || node.Name == "WaitingForReady")
        {
            return true;
        }

        foreach (Node child in node.GetChildren())
        {
            if (HasVisibleWaitingSurface(child))
                return true;
        }
        return false;
    }

}

/// <summary>
/// Touch-and-drag reaction-wheel launcher. The reference source added public
/// mobile helpers to NReactionWheel, while imported PC assemblies retain the
/// desktop-only private wheel methods. This node reproduces the reference
/// selection math and uses reflection only for those unchanged private wheel
/// fields/methods, preserving the original reaction network path.
/// </summary>
public partial class AndroidMobileReactionButton : Control
{
    private const int MousePointerId = -1;
    private const float MovementThresholdSquared = 400f;
    private const float CenterRadius = 70f;
    private const float SelectionDeadzone = 8f;
    private const string IconPath = "res://images/ui/emote/happy_cultist.png";
    private const ulong VisibilityPollIntervalMsec = 250;

    private static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    private readonly MobileReactionPointerState _pointerState = new();
    private bool _runtimeInitialized;
    private bool _hasReportedVisibility;
    private bool _lastReportedVisibility;
    private Vector2 _wheelCenter;
    private Control _visuals;
    private Tween _pressTween;

    private bool HasActivePointer => _pointerState.HasActivePointer;


    internal void InitializeRuntime()
    {
        if (_runtimeInitialized)
            return;

        _runtimeInitialized = true;
        BuildVisuals();
        Visible = false;
        // STS2Mobile uses the plain .NET SDK, so this dynamic subclass has no
        // Godot source-generated virtual callback dispatcher. Native signals
        // and the patched NGame callback are the runtime input boundary.
        Connect(Control.SignalName.GuiInput, Callable.From<InputEvent>(HandleGuiInput));
        Connect(Node.SignalName.TreeExiting, Callable.From(CancelInteraction));

        var visibilityTimer = new Timer
        {
            Name = "VisibilityTimer",
            WaitTime = VisibilityPollIntervalMsec / 1000.0,
            OneShot = false,
            Autostart = true,
        };
        visibilityTimer.Connect(Timer.SignalName.Timeout, Callable.From(OnVisibilityPollTimeout));
        AddChild(visibilityTimer);
    }


    private void OnVisibilityPollTimeout()
    {
        RefreshVisibility("timer");

        if (HasActivePointer)
        {
            var game = NGame.Instance;
            if (game == null || game.ReactionWheel == null || !game.ReactionWheel.Visible)
                CancelInteraction();
        }
    }

    internal void RefreshVisibility(string reason, bool forceLog = false)
    {
        var game = NGame.Instance;
        string osName;
        bool mobileFeature;
        try
        {
            osName = OS.GetName();
            mobileFeature = OS.HasFeature("mobile");
        }
        catch
        {
            osName = "<unknown>";
            mobileFeature = false;
        }
        bool mobileRuntime = MobileReactionVisibilityPolicy.IsMobileRuntime(mobileFeature, osName);
        bool settingEnabled = AndroidSettingsBridge.GetBool("show_mobile_emoji_button", true);
        bool networkReady = game?.ReactionContainer?.InMultiplayer == true;
        bool reactionAvailable = game != null && MobileReactionButtonPatches.IsReactionAvailable(game);
        bool shouldDisplay = MobileReactionVisibilityPolicy.ShouldDisplay(
            mobileFeature,
            osName,
            settingEnabled,
            reactionAvailable);

        if (Visible != shouldDisplay)
        {
            Visible = shouldDisplay;
            if (!shouldDisplay)
                CancelInteraction();
        }

        if (!forceLog && _hasReportedVisibility && _lastReportedVisibility == shouldDisplay)
            return;

        _hasReportedVisibility = true;
        _lastReportedVisibility = shouldDisplay;
        PatchHelper.Log(
            $"Mobile reaction button state: visible={shouldDisplay}; reason={reason}; os={osName}; "
            + $"mobile_feature={mobileFeature}; mobile_runtime={mobileRuntime}; setting={settingEnabled}; "
            + $"network_ready={networkReady}; reaction_available={reactionAvailable}; "
            + $"scene={game?.RootSceneContainer?.CurrentScene?.GetType().Name ?? "<none>"}; "
            + $"position={GlobalPosition}; size={Size}");
    }



    private void HandleGuiInput(InputEvent inputEvent)
    {
        try
        {
            if (!Visible || HasActivePointer)
                return;

            if (!TryGetPress(inputEvent, out var pointerId))
                return;

            var wheelCenter = GetButtonCenterInViewport();
            var wheel = NGame.Instance?.ReactionWheel;
            if (wheel == null || !TryShowWheel(wheel, wheelCenter))
                return;
            _wheelCenter = wheelCenter;

            _pointerState.TryBegin(pointerId);
            AnimatePressed(true);
            GetViewport()?.SetInputAsHandled();
            PatchHelper.Log($"Mobile reaction button press accepted: pointer={pointerId}; center={wheelCenter}");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Mobile reaction button gui input failed: {exception.Message}");
            CancelInteraction();
        }
    }

    internal void HandleGlobalInput(InputEvent inputEvent)
    {
        if (!HasActivePointer)
            return;

        try
        {
            if (TryGetMove(inputEvent, out var screenPosition))
            {
                if (_pointerState.ShouldUpdateSelection(
                    screenPosition.DistanceSquaredTo(_wheelCenter),
                    MovementThresholdSquared))
                {
                    UpdateSelectionFromScreenPosition(NGame.Instance?.ReactionWheel, screenPosition);
                }
                GetViewport()?.SetInputAsHandled();
                return;
            }

            if (!TryGetRelease(inputEvent, out var releasePosition))
                return;

            var react = _pointerState.HasMovedSincePress;
            var pointerId = _pointerState.ActivePointerId;
            var wheel = NGame.Instance?.ReactionWheel;
            if (wheel != null && wheel.Visible)
            {
                if (react)
                {
                    UpdateSelectionFromScreenPosition(wheel, releasePosition);
                    HideWheel(wheel, react: true);
                }
                else
                {
                    HideWheel(wheel, react: false);
                }
            }
            CancelInteraction();
            GetViewport()?.SetInputAsHandled();
            PatchHelper.Log($"Mobile reaction button released: pointer={pointerId}; react={react}; position={releasePosition}");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Mobile reaction button drag/release failed: {exception.Message}");
            CancelInteraction();
        }
    }


    private Vector2 GetButtonCenterInViewport()
    {
        return GetViewportCenter(this);
    }

    private bool TryGetPress(InputEvent inputEvent, out int pointerId)
    {
        pointerId = MobileReactionPointerState.NoPointerId;
        if (inputEvent is InputEventScreenTouch { Pressed: true } screenTouch)
        {
            pointerId = screenTouch.Index;
            return true;
        }

        if (inputEvent is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
        {
            pointerId = MousePointerId;
            return true;
        }

        return false;
    }

    private bool TryGetMove(InputEvent inputEvent, out Vector2 screenPosition)
    {
        screenPosition = Vector2.Zero;
        if (inputEvent is InputEventScreenDrag screenDrag && screenDrag.Index == _pointerState.ActivePointerId)
        {
            screenPosition = screenDrag.Position;
            return true;
        }

        if (_pointerState.ActivePointerId == MousePointerId && inputEvent is InputEventMouseMotion mouseMotion)
        {
            screenPosition = mouseMotion.Position;
            return true;
        }

        return false;
    }

    private bool TryGetRelease(InputEvent inputEvent, out Vector2 screenPosition)
    {
        screenPosition = Vector2.Zero;
        if (inputEvent is InputEventScreenTouch { Pressed: false } screenTouch && screenTouch.Index == _pointerState.ActivePointerId)
        {
            screenPosition = screenTouch.Position;
            return true;
        }

        if (_pointerState.ActivePointerId == MousePointerId
            && inputEvent is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } mouseButton)
        {
            screenPosition = mouseButton.Position;
            return true;
        }

        return false;
    }

    private void CancelInteraction()
    {
        _pointerState.Cancel();
        var wheel = NGame.Instance?.ReactionWheel;
        if (wheel != null && wheel.Visible)
            HideWheel(wheel, react: false);
        AnimatePressed(false);
    }

    private void AnimatePressed(bool pressed)
    {
        if (_visuals == null || !GodotObject.IsInstanceValid(_visuals))
            return;

        _pressTween?.Kill();
        _pressTween = CreateTween().SetParallel();
        _pressTween.TweenProperty(
            _visuals,
            "scale",
            pressed ? Vector2.One * 0.92f : Vector2.One,
            pressed ? 0.05 : 0.15);
        _pressTween.TweenProperty(
            _visuals,
            "modulate:a",
            pressed ? 1f : 0.9f,
            pressed ? 0.05 : 0.15);
    }

    private static Vector2 GetViewportCenter(Control control)
    {
        var center = MobileReactionWheelPlacement.GetTransformedCenter(
            ToNumerics(control.GetGlobalTransformWithCanvas()),
            new NumericsVector2(control.Size.X, control.Size.Y));
        return new Vector2(center.X, center.Y);
    }

    private static bool TryCenterWheelOnViewportPoint(Control wheel, Vector2 desiredCenter, out Vector2 actualCenter)
    {
        actualCenter = Vector2.Zero;
        if (wheel.GetParent() is not CanvasItem parent)
            return false;

        if (!MobileReactionWheelPlacement.TryGetParentPositionAdjustment(
                ToNumerics(parent.GetGlobalTransformWithCanvas()),
                ToNumerics(wheel.GetTransform()),
                new NumericsVector2(wheel.Size.X, wheel.Size.Y),
                new NumericsVector2(desiredCenter.X, desiredCenter.Y),
                out var adjustment))
        {
            return false;
        }

        wheel.Position += new Vector2(adjustment.X, adjustment.Y);
        actualCenter = GetViewportCenter(wheel);
        return true;
    }

    private static NumericsMatrix3x2 ToNumerics(Transform2D transform)
    {
        return new NumericsMatrix3x2(
            transform.X.X,
            transform.X.Y,
            transform.Y.X,
            transform.Y.Y,
            transform.Origin.X,
            transform.Origin.Y);
    }

    private static bool TryShowWheel(Control wheel, Vector2 center)
    {
        try
        {
            var marker = GetField(wheel, "_marker") as TextureRect;
            if (marker == null)
            {
                PatchHelper.Log("Mobile reaction wheel show failed: _marker was not resolved.");
                return false;
            }

            ClearSelection(wheel);
            SetField(wheel, "_centerPosition", center);
            SetLocalPlayerMarker(wheel, marker);
            marker.Position = (wheel.Size - marker.Size) * 0.5f;
            marker.Rotation = 0f;
            if (!TryCenterWheelOnViewportPoint(wheel, center, out var actualCenter))
            {
                PatchHelper.Log("Mobile reaction wheel show failed: viewport placement could not be resolved.");
                return false;
            }
            wheel.Visible = true;
            Input.MouseMode = Input.MouseModeEnum.Hidden;
            PatchHelper.Log(
                $"Mobile reaction wheel shown: requested_center={center}; actual_center={actualCenter}; "
                + $"alignment_error={actualCenter.DistanceTo(center)}; size={wheel.Size}; scale={wheel.Scale}");
            return true;
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Mobile reaction wheel show failed: {exception.Message}");
            return false;
        }
    }

    private void UpdateSelectionFromScreenPosition(Control wheel, Vector2 screenPosition)
    {
        if (wheel == null || !wheel.Visible)
            return;

        try
        {
            var marker = GetField(wheel, "_marker") as TextureRect;
            if (marker == null)
                return;

            var offset = screenPosition - _wheelCenter;
            var limitedOffset = offset.LimitLength(CenterRadius);
            var markerCenter = (wheel.Size - marker.Size) * 0.5f;
            marker.Position = markerCenter + limitedOffset;

            if (limitedOffset.LengthSquared() <= SelectionDeadzone * SelectionDeadzone)
            {
                marker.Rotation = 0f;
                ClearSelection(wheel);
                return;
            }

            float angle = Mathf.Atan2(limitedOffset.Y, limitedOffset.X);
            marker.Rotation = angle - Mathf.Pi / 2f;
            var selected = Invoke(wheel, "GetSelectedWedge", angle);
            var previous = GetField(wheel, "_selectedWedge");
            if (ReferenceEquals(previous, selected))
                return;

            Invoke(previous, "OnDeselected");
            SetField(wheel, "_selectedWedge", selected);
            Invoke(selected, "OnSelected");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Mobile reaction wheel selection failed: {exception.Message}");
        }
    }

    private static void HideWheel(Control wheel, bool react)
    {
        try
        {
            if (react)
                Invoke(wheel, "React");
            Input.MouseMode = Input.MouseModeEnum.Visible;
            wheel.Visible = false;
            ClearSelection(wheel);
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Mobile reaction wheel hide failed: {exception.Message}");
            if (wheel != null)
                wheel.Visible = false;
        }
    }

    private static void ClearSelection(Control wheel)
    {
        if (wheel == null)
            return;
        var selected = GetField(wheel, "_selectedWedge");
        if (selected != null)
            Invoke(selected, "OnDeselected");
        SetField(wheel, "_selectedWedge", null);
    }

    private static void SetLocalPlayerMarker(Control wheel, TextureRect marker)
    {
        var localPlayer = GetField(wheel, "_localPlayer");
        var character = GetProperty(localPlayer, "Character");
        if (GetProperty(character, "MapMarker") is Texture2D mapMarker)
            marker.Texture = mapMarker;
    }

    private static object GetField(object target, string name)
    {
        if (target == null)
            return null;
        return target.GetType().GetField(name, PrivateInstance)?.GetValue(target);
    }

    private static void SetField(object target, string name, object value)
    {
        if (target == null)
            return;
        target.GetType().GetField(name, PrivateInstance)?.SetValue(target, value);
    }

    private static object GetProperty(object target, string name)
    {
        if (target == null)
            return null;
        return target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(target);
    }

    private static object Invoke(object target, string methodName, params object[] arguments)
    {
        if (target == null)
            return null;
        var methods = target.GetType().GetMethods(PrivateInstance | BindingFlags.Public);
        foreach (var method in methods)
        {
            if (method.Name != methodName || method.GetParameters().Length != arguments.Length)
                continue;
            return method.Invoke(target, arguments);
        }
        return null;
    }

    private void BuildVisuals()
    {
        _visuals = new Control
        {
            Name = "Visuals",
            LayoutMode = 1,
            AnchorRight = 1f,
            AnchorBottom = 1f,
            GrowHorizontal = Control.GrowDirection.Both,
            GrowVertical = Control.GrowDirection.Both,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Modulate = new Color(1f, 1f, 1f, 1f),
        };
        AddChild(_visuals);

        _visuals.AddChild(new ColorRect
        {
            Name = "Background",
            LayoutMode = 1,
            AnchorRight = 1f,
            AnchorBottom = 1f,
            GrowHorizontal = Control.GrowDirection.Both,
            GrowVertical = Control.GrowDirection.Both,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Color = new Color(0f, 0f, 0f, 0.45f),
        });

        var icon = new TextureRect
        {
            Name = "Icon",
            LayoutMode = 1,
            AnchorRight = 1f,
            AnchorBottom = 1f,
            OffsetLeft = 12f,
            OffsetTop = 12f,
            OffsetRight = -12f,
            OffsetBottom = -12f,
            GrowHorizontal = Control.GrowDirection.Both,
            GrowVertical = Control.GrowDirection.Both,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            Texture = ResourceLoader.Load<Texture2D>(IconPath, null, ResourceLoader.CacheMode.Reuse),
        };
        _visuals.AddChild(icon);

        if (icon.Texture == null)
        {
            _visuals.AddChild(new Label
            {
                Name = "FallbackIcon",
                Text = "!",
                LayoutMode = 1,
                AnchorRight = 1f,
                AnchorBottom = 1f,
                GrowHorizontal = Control.GrowDirection.Both,
                GrowVertical = Control.GrowDirection.Both,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            });
        }
    }
}
