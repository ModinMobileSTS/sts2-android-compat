using System;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;
using STS2Mobile.Android;

namespace STS2Mobile.Patches;

public static class DisplaySettingsPatches
{
    internal enum ContentScaleOwner
    {
        Unknown,
        UiScaleAuto,
        FixedAspect,
    }

    private enum DeferredDisplayApplyKind
    {
        None,
        ContentScale,
        DisplaySettings,
        RuntimeSettings,
    }

    private readonly struct ContentScaleTarget
    {
        internal readonly ContentScaleOwner Owner;
        internal readonly Window.ContentScaleModeEnum Mode;
        internal readonly Window.ContentScaleAspectEnum Aspect;
        internal readonly Vector2I Size;
        internal readonly float Factor;

        internal ContentScaleTarget(
            ContentScaleOwner owner,
            Window.ContentScaleModeEnum mode,
            Window.ContentScaleAspectEnum aspect,
            Vector2I size,
            float factor)
        {
            Owner = owner;
            Mode = mode;
            Aspect = aspect;
            Size = size;
            Factor = factor;
        }
    }

    private readonly struct RenderTargetPlan
    {
        internal readonly Vector2I NativeSize;
        internal readonly Vector2I EffectiveSize;
        internal readonly Vector2 Scale;
        internal readonly bool IsCustom;
        internal readonly bool WasClamped;

        internal RenderTargetPlan(
            Vector2I nativeSize,
            Vector2I effectiveSize,
            Vector2 scale,
            bool isCustom,
            bool wasClamped)
        {
            NativeSize = nativeSize;
            EffectiveSize = effectiveSize;
            Scale = scale;
            IsCustom = isCustom;
            WasClamped = wasClamped;
        }
    }

    internal const string ScreenRotationAuto = "auto";
    internal const string ScreenRotationUserLandscape = "user_landscape";
    internal const string ScreenRotationLandscape = "landscape";
    internal const string ScreenRotationReverseLandscape = "reverse_landscape";

    private const int MinimumRenderTargetDimension = 2;
    private const int MaximumDynamicRenderTargetDimension = 4096;

    private static readonly StringName[] FontSizeOverrideNames =
    {
        "font_size",
        "normal_font_size",
        "bold_font_size",
        "italics_font_size",
        "bold_italics_font_size",
        "mono_font_size",
    };

    private static ContentScaleOwner _contentScaleOwner;
    private static ContentScaleOwner _lastContentScaleOwner;
    private static Vector2I _lastRenderSize = new(-1, -1);
    private static Vector2I _lastNativeRenderTargetSize = new(-1, -1);
    private static Vector2I _lastEffectiveRenderTargetSize = new(-1, -1);
    private static bool? _lastRenderTargetWasCustom;
    private static Vector2I _lastContentScaleSize = new(-1, -1);
    private static float _lastScaleFactor = -1f;
    private static Window.ContentScaleAspectEnum? _lastScaleAspect;
    private static AspectRatioSetting? _lastAspect;
    private static int _lastUiScalePercent = -1;
    private static bool? _hasSourcePortMegaTextScaling;
    private static bool _isApplyingDisplaySettings;
    private static bool _deferredDisplayApplyQueued;
    private static DeferredDisplayApplyKind _deferredDisplayApplyKind;
    private static string _deferredDisplayApplyReason = "unspecified";
    private static long _resumeGeneration;
    private static long _resumeValidationScheduledGeneration = -1;
    private static long _resumeRepairAttemptedGeneration = -1;
    private static long _contentScaleTargetRevision;
    private static bool _hasLatestContentScaleTarget;
    private static ContentScaleTarget _latestContentScaleTarget;
    private static Window _observedRootWindow;

    internal static ContentScaleOwner CurrentContentScaleOwner => _contentScaleOwner;

    public static void Apply(Harmony harmony)
    {
        PatchHelper.Patch(harmony, typeof(NGame), "ApplyDisplaySettings", postfix: PatchHelper.Method(typeof(DisplaySettingsPatches), nameof(ApplyDisplaySettingsPostfix)));
        PatchHelper.Patch(harmony, typeof(NGame), "InitializeGraphicsPreferences", postfix: PatchHelper.Method(typeof(DisplaySettingsPatches), nameof(InitializeGraphicsPreferencesPostfix)));
        PatchHelper.Patch(harmony, typeof(NGame), "_Notification", postfix: PatchHelper.Method(typeof(DisplaySettingsPatches), nameof(NotificationPostfix)));
        PatchHelper.Patch(harmony, typeof(NGame), "_Ready", postfix: PatchHelper.Method(typeof(DisplaySettingsPatches), nameof(ReadyPostfix)));
        PatchHelper.Patch(harmony, typeof(NGlobalUi), "_Ready", postfix: PatchHelper.Method(typeof(DisplaySettingsPatches), nameof(ReadyPostfix)));
        PatchHelper.Patch(harmony, typeof(NMainMenu), "_Ready", postfix: PatchHelper.Method(typeof(DisplaySettingsPatches), nameof(ReadyPostfix)));
    }

    public static void InitializeGraphicsPreferencesPostfix()
    {
        try
        {
            ApplyFontSizeSetting();
            ApplyAndroidScreenOrientationSetting();
            ApplyDisplaySettingsPostfix();
            RequestDeferredContentScaleApply("graphics-preferences-initialized");
            PatchHelper.Log($"Applied Android graphics bridge: fps(original)={Engine.MaxFps}, scale={GetGlobalScale():0.##}, font={GetUiFontScalePercent()}%, render={GetFullscreenRenderSize()}");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"InitializeGraphicsPreferencesPostfix failed: {exception}");
        }
    }

    public static void ApplyDisplaySettingsPostfix()
    {
        ApplyDisplaySettings(DeferredDisplayApplyKind.DisplaySettings, "NGame.ApplyDisplaySettings");
    }

    public static void ReadyPostfix()
    {
        try
        {
            var window = GetRootWindow();
            if (window != null)
                ApplyFontSizeOverridesRecursive(window);
            ApplyDisplaySettingsPostfix();
            RequestDeferredContentScaleApply("node-ready");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"ReadyPostfix font/display scaling failed: {exception.Message}");
        }
    }

    public static void NotificationPostfix(int what)
    {
        try
        {
            switch (what)
            {
                case (int)Node.NotificationApplicationResumed:
                    AndroidSettingsBridge.InvalidateCache();
                    var generation = ++_resumeGeneration;
                    RequestDeferredDisplayApply(DeferredDisplayApplyKind.RuntimeSettings, $"application-resumed#{generation}");
                    break;
            }
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"NotificationPostfix failed: {exception.Message}");
        }
    }

    public static void ApplyRuntimeDisplaySettings()
    {
        ApplyDisplaySettings(DeferredDisplayApplyKind.RuntimeSettings, "runtime-settings");
    }

    internal static void ApplyUiScaleContentScaleSettings()
    {
        ApplyDisplaySettings(DeferredDisplayApplyKind.ContentScale, "ui-scale-changed");
    }

    internal static void RequestDeferredContentScaleApply(string reason)
    {
        // ContentScale setters synchronously emit window-change callbacks. Ignore those
        // callbacks while this coordinator owns the mutation so they cannot form a loop.
        if (_isApplyingDisplaySettings)
            return;
        RequestDeferredDisplayApply(DeferredDisplayApplyKind.ContentScale, reason);
    }

    private static void ApplyDisplaySettings(DeferredDisplayApplyKind applyKind, string reason)
    {
        if (_isApplyingDisplaySettings)
        {
            // Preserve one follow-up for a genuine reentrant settings request. The
            // deferred queue is single-flight and compare-before-set makes it finite.
            RequestDeferredDisplayApply(applyKind, $"reentrant-{reason}");
            return;
        }

        _isApplyingDisplaySettings = true;
        try
        {
            if (applyKind is DeferredDisplayApplyKind.DisplaySettings or DeferredDisplayApplyKind.RuntimeSettings)
            {
                AndroidSettingsPatches.ApplyCompanionSettingsToRuntimeSave();
                PreloadManager.Enabled = AndroidSettingsBridge.GetBool("preload_enabled", true);
            }
            if (applyKind == DeferredDisplayApplyKind.RuntimeSettings)
            {
                ApplyAndroidScreenOrientationSetting();
                ApplyFontSizeSetting();
            }

            var settings = SaveManager.Instance?.SettingsSave;
            if ((applyKind is DeferredDisplayApplyKind.DisplaySettings or DeferredDisplayApplyKind.RuntimeSettings) && settings != null)
                NGame.ApplySyncSetting();
            ApplyContentScaleSettings(settings, reason);
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"ApplyDisplaySettings failed ({reason}): {exception}");
        }
        finally
        {
            _isApplyingDisplaySettings = false;
        }
    }

    private static void ApplyContentScaleSettings(SettingsSave settings, string reason)
    {
        var window = GetRootWindow();
        if (window == null)
            return;

        EnsureRootWindowObservation(window);

        var renderSize = GetFullscreenRenderSize();
        var aspect = settings?.AspectRatioSetting ?? AspectRatioSetting.Auto;
        var scaleFactor = GetGlobalScale();
        UiScalePatches.EnsureUiScaleLoaded();

        // Render resolution must never become the root Window's logical size: doing
        // so changes the relative size of fixed-pixel controls/cards whenever the
        // preset changes. Keep one stable CanvasItems layout target, then independently
        // resize only the renderer-side root viewport below.
        var owner = aspect != AspectRatioSetting.Auto
            ? ContentScaleOwner.FixedAspect
            : ContentScaleOwner.UiScaleAuto;
        var targetMode = Window.ContentScaleModeEnum.CanvasItems;
        var targetAspect = owner == ContentScaleOwner.FixedAspect
            ? Window.ContentScaleAspectEnum.Keep
            : Window.ContentScaleAspectEnum.Expand;
        var targetSize = owner == ContentScaleOwner.FixedAspect
            ? GetAspectContentScaleSize(aspect)
            : UiScalePatches.GetScaledContentSize();

        var target = new ContentScaleTarget(owner, targetMode, targetAspect, targetSize, scaleFactor);
        long targetRevision = TrackContentScaleTarget(target);
        int changedProperties = ApplyContentScaleTarget(window, target);
        ApplyDynamicRenderTarget(window, renderSize, reason);

        bool isResumeApply = reason.Contains("application-resumed", StringComparison.Ordinal);
        if (isResumeApply
            || _lastContentScaleOwner != owner
            || _lastRenderSize != renderSize
            || _lastContentScaleSize != targetSize
            || !Mathf.IsEqualApprox(_lastScaleFactor, scaleFactor)
            || _lastScaleAspect != targetAspect
            || _lastAspect != aspect
            || _lastUiScalePercent != UiScalePatches.UiScalePercent)
        {
            _lastContentScaleOwner = owner;
            _lastRenderSize = renderSize;
            _lastContentScaleSize = targetSize;
            _lastScaleFactor = scaleFactor;
            _lastScaleAspect = targetAspect;
            _lastAspect = aspect;
            _lastUiScalePercent = UiScalePatches.UiScalePercent;
            PatchHelper.Log($"[Display] ContentScale owner={owner}, reason={reason}, changed={changedProperties}, renderRequest={renderSize}, logicalContent={targetSize}, mode={targetMode}, aspect={targetAspect}, scale={scaleFactor:0.##}, uiScale={UiScalePatches.UiScalePercent}%, actualSurface={DisplayServer.WindowGetSize()}");
        }

        if (isResumeApply)
            ScheduleResumeConsistencyValidation(_resumeGeneration, targetRevision, target);
    }

    private static long TrackContentScaleTarget(ContentScaleTarget target)
    {
        if (!_hasLatestContentScaleTarget || !AreContentScaleTargetsEqual(_latestContentScaleTarget, target))
        {
            _latestContentScaleTarget = target;
            _hasLatestContentScaleTarget = true;
            _contentScaleTargetRevision++;
        }
        return _contentScaleTargetRevision;
    }

    private static int ApplyContentScaleTarget(Window window, ContentScaleTarget target)
    {
        // Publish ownership before any setter. Godot setters synchronously notify the
        // window tree, so callbacks must observe the new owner rather than stale state.
        _contentScaleOwner = target.Owner;
        int changedProperties = 0;
        if (SetContentScaleModeIfChanged(window, target.Mode))
            changedProperties++;
        if (SetContentScaleAspectIfChanged(window, target.Aspect))
            changedProperties++;
        if (SetContentScaleSizeIfChanged(window, target.Size))
            changedProperties++;
        if (SetContentScaleFactorIfChanged(window, target.Factor))
            changedProperties++;
        return changedProperties;
    }

    private static void EnsureRootWindowObservation(Window window)
    {
        if (ReferenceEquals(_observedRootWindow, window))
            return;

        if (_observedRootWindow != null && GodotObject.IsInstanceValid(_observedRootWindow))
        {
            try
            {
                _observedRootWindow.SizeChanged -= OnRootWindowSizeChanged;
            }
            catch
            {
                // A replaced root can already be tearing down; the new root still
                // needs its observer installed below.
            }
        }

        _observedRootWindow = window;
        window.SizeChanged += OnRootWindowSizeChanged;
    }

    private static void OnRootWindowSizeChanged()
    {
        // Android resize/rotation restores the renderer-side root viewport to its
        // native size. Reapply once after Godot finishes the current resize sequence.
        RequestDeferredContentScaleApply("root-window-size-changed");
    }

    private static void ApplyDynamicRenderTarget(Window window, Vector2I renderRequest, string reason)
    {
        var visibleSize = window.GetVisibleRect().Size;
        var stretchTransform = window.GetStretchTransform();
        var stretchScale = stretchTransform.Scale;
        var nativeSize = new Vector2I(
            Mathf.RoundToInt(Mathf.Abs(visibleSize.X * stretchScale.X)),
            Mathf.RoundToInt(Mathf.Abs(visibleSize.Y * stretchScale.Y)));
        if (!IsUsableRenderTargetSize(nativeSize))
        {
            PatchHelper.Log($"[Display] Dynamic render target skipped ({reason}): invalid native attachment size={nativeSize}, visible={visibleSize}, stretchScale={stretchScale}.");
            return;
        }

        var plan = BuildRenderTargetPlan(nativeSize, renderRequest);
        var baseCanvasTransform = stretchTransform * window.GlobalCanvasTransform;
        var rendererCanvasTransform = baseCanvasTransform.Scaled(plan.Scale);
        var viewportRid = window.GetViewportRid();

        // Keep the Android Surface and Window node untouched. The renderer draws into
        // the requested offscreen target and Godot's existing root attachment blits it
        // back to the native CanvasItems rectangle. Only the RenderingServer transform
        // is compensated; the scene-side transform used for input remains unchanged.
        try
        {
            RenderingServer.ViewportSetRenderDirectToScreen(viewportRid, false);
            RenderingServer.ViewportSetSize(viewportRid, plan.EffectiveSize.X, plan.EffectiveSize.Y);
            RenderingServer.ViewportSetGlobalCanvasTransform(viewportRid, rendererCanvasTransform);
        }
        catch (Exception exception)
        {
            try
            {
                RenderingServer.ViewportSetSize(viewportRid, nativeSize.X, nativeSize.Y);
                RenderingServer.ViewportSetGlobalCanvasTransform(viewportRid, baseCanvasTransform);
            }
            catch (Exception restoreException)
            {
                PatchHelper.Log($"[Display] WARNING: Failed to restore native render target after dynamic apply failure ({reason}): {restoreException.Message}");
            }
            PatchHelper.Log($"[Display] Dynamic render target failed ({reason}): {exception}");
            return;
        }

        bool stateChanged = _lastNativeRenderTargetSize != plan.NativeSize
            || _lastEffectiveRenderTargetSize != plan.EffectiveSize
            || _lastRenderTargetWasCustom != plan.IsCustom;
        _lastNativeRenderTargetSize = plan.NativeSize;
        _lastEffectiveRenderTargetSize = plan.EffectiveSize;
        _lastRenderTargetWasCustom = plan.IsCustom;

        bool lifecycleReapply = reason.Contains("application-resumed", StringComparison.Ordinal)
            || reason.Contains("root-window-size-changed", StringComparison.Ordinal);
        if (stateChanged || lifecycleReapply)
        {
            PatchHelper.Log($"[Display] RenderTarget reason={reason}, request={renderRequest}, native={plan.NativeSize}, effective={plan.EffectiveSize}, custom={plan.IsCustom}, clamped={plan.WasClamped}, serverScale={plan.Scale}, logicalVisible={visibleSize}, stretchScale={stretchScale}, surface={DisplayServer.WindowGetSize()}");
        }
    }

    private static RenderTargetPlan BuildRenderTargetPlan(Vector2I nativeSize, Vector2I renderRequest)
    {
        if (!IsUsableRenderTargetSize(renderRequest))
            return new RenderTargetPlan(nativeSize, nativeSize, Vector2.One, isCustom: false, wasClamped: false);

        // Presets are reference rectangles (for example 1280x720). Match CanvasItems
        // Expand semantics by scaling the current native attachment uniformly until it
        // covers that rectangle. Ultrawide/narrow aspect ratios therefore get a wider
        // or taller effective target instead of non-uniform pixels or changed layout.
        double requestedScale = Math.Max(
            (double)renderRequest.X / nativeSize.X,
            (double)renderRequest.Y / nativeSize.Y);
        double targetWidth = nativeSize.X * requestedScale;
        double targetHeight = nativeSize.Y * requestedScale;

        int maximumDimension = Math.Max(
            MaximumDynamicRenderTargetDimension,
            Math.Max(nativeSize.X, nativeSize.Y));
        double clampScale = Math.Min(
            1.0,
            Math.Min(maximumDimension / targetWidth, maximumDimension / targetHeight));
        bool wasClamped = clampScale < 0.999999;
        targetWidth *= clampScale;
        targetHeight *= clampScale;

        var effectiveSize = new Vector2I(
            Math.Max(MinimumRenderTargetDimension, (int)Math.Round(targetWidth)),
            Math.Max(MinimumRenderTargetDimension, (int)Math.Round(targetHeight)));
        var scale = new Vector2(
            (float)effectiveSize.X / nativeSize.X,
            (float)effectiveSize.Y / nativeSize.Y);
        return new RenderTargetPlan(nativeSize, effectiveSize, scale, isCustom: true, wasClamped: wasClamped);
    }

    private static bool IsUsableRenderTargetSize(Vector2I size)
    {
        return size.X >= MinimumRenderTargetDimension && size.Y >= MinimumRenderTargetDimension;
    }

    private static void ScheduleResumeConsistencyValidation(long generation, long targetRevision, ContentScaleTarget target)
    {
        if (_resumeValidationScheduledGeneration == generation)
            return;
        _resumeValidationScheduledGeneration = generation;
        try
        {
            Callable.From(() => ValidateResumeContentScale(generation, targetRevision, target, afterRepair: false)).CallDeferred();
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"[Display] Failed to queue resume consistency validation generation={generation}: {exception.Message}");
        }
    }

    private static void ValidateResumeContentScale(long generation, long targetRevision, ContentScaleTarget target, bool afterRepair)
    {
        if (generation != _resumeGeneration)
            return;
        if (!_hasLatestContentScaleTarget
            || targetRevision != _contentScaleTargetRevision
            || !AreContentScaleTargetsEqual(_latestContentScaleTarget, target))
        {
            PatchHelper.Log($"[Display] Resume consistency validation skipped for stale target: generation={generation}, targetRevision={targetRevision}, currentRevision={_contentScaleTargetRevision}.");
            return;
        }

        var window = GetRootWindow();
        if (window == null)
        {
            PatchHelper.Log($"[Display] Resume consistency validation skipped: root Window missing, generation={generation}.");
            return;
        }
        if (IsContentScaleTargetApplied(window, target))
        {
            // A late Android/Godot resize can restore only the renderer-side root
            // viewport while leaving every scene Window property unchanged. Reassert
            // the current render request during the bounded resume validation too.
            ApplyDynamicRenderTarget(window, GetFullscreenRenderSize(), $"resume-validation#{generation}");
            PatchHelper.Log($"[Display] Resume ContentScale consistent: generation={generation}, afterRepair={afterRepair}, owner={target.Owner}, mode={target.Mode}, aspect={target.Aspect}, size={target.Size}, factor={target.Factor:0.##}.");
            return;
        }

        if (afterRepair || _resumeRepairAttemptedGeneration == generation)
        {
            PatchHelper.Log($"[Display] WARNING: Resume ContentScale remains inconsistent after one repair; generation={generation}, {DescribeContentScaleMismatch(window, target)}");
            return;
        }

        _resumeRepairAttemptedGeneration = generation;
        if (_isApplyingDisplaySettings)
        {
            PatchHelper.Log($"[Display] WARNING: Resume ContentScale repair skipped during active display apply; generation={generation}, {DescribeContentScaleMismatch(window, target)}");
            return;
        }

        int changedProperties;
        _isApplyingDisplaySettings = true;
        try
        {
            changedProperties = ApplyContentScaleTarget(window, target);
            ApplyDynamicRenderTarget(window, GetFullscreenRenderSize(), $"resume-repair#{generation}");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"[Display] WARNING: Resume ContentScale repair failed; generation={generation}: {exception}");
            return;
        }
        finally
        {
            _isApplyingDisplaySettings = false;
        }

        PatchHelper.Log($"[Display] Resume ContentScale repair applied once: generation={generation}, changed={changedProperties}, owner={target.Owner}, mode={target.Mode}, aspect={target.Aspect}, size={target.Size}, factor={target.Factor:0.##}.");
        try
        {
            Callable.From(() => ValidateResumeContentScale(generation, targetRevision, target, afterRepair: true)).CallDeferred();
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"[Display] WARNING: Failed to queue final resume consistency validation generation={generation}: {exception.Message}");
        }
    }

    private static bool IsContentScaleTargetApplied(Window window, ContentScaleTarget target)
    {
        return window.ContentScaleMode == target.Mode
            && window.ContentScaleAspect == target.Aspect
            && window.ContentScaleSize == target.Size
            && Mathf.IsEqualApprox(window.ContentScaleFactor, target.Factor);
    }

    private static bool AreContentScaleTargetsEqual(ContentScaleTarget left, ContentScaleTarget right)
    {
        return left.Owner == right.Owner
            && left.Mode == right.Mode
            && left.Aspect == right.Aspect
            && left.Size == right.Size
            && Mathf.IsEqualApprox(left.Factor, right.Factor);
    }

    private static string DescribeContentScaleMismatch(Window window, ContentScaleTarget target)
    {
        return $"actual(mode={window.ContentScaleMode}, aspect={window.ContentScaleAspect}, size={window.ContentScaleSize}, factor={window.ContentScaleFactor:0.##}), expected(mode={target.Mode}, aspect={target.Aspect}, size={target.Size}, factor={target.Factor:0.##})";
    }

    private static void RequestDeferredDisplayApply(DeferredDisplayApplyKind applyKind, string reason)
    {
        var previousApplyKind = _deferredDisplayApplyKind;
        if ((int)applyKind >= (int)_deferredDisplayApplyKind)
        {
            _deferredDisplayApplyKind = applyKind;
            _deferredDisplayApplyReason = reason;
        }
        if (_deferredDisplayApplyQueued)
        {
            if (applyKind == DeferredDisplayApplyKind.RuntimeSettings)
                PatchHelper.Log($"[Display] Coalesced deferred resume apply: pending={previousApplyKind}, requested={applyKind}");
            return;
        }

        _deferredDisplayApplyQueued = true;
        try
        {
            Callable.From(RunDeferredDisplayApply).CallDeferred();
            if (applyKind == DeferredDisplayApplyKind.RuntimeSettings)
                PatchHelper.Log("[Display] Queued deferred resume apply.");
        }
        catch (Exception exception)
        {
            _deferredDisplayApplyQueued = false;
            _deferredDisplayApplyKind = DeferredDisplayApplyKind.None;
            PatchHelper.Log($"Failed to queue deferred display apply ({reason}): {exception.Message}");
        }
    }

    private static void RunDeferredDisplayApply()
    {
        var applyKind = _deferredDisplayApplyKind;
        var reason = _deferredDisplayApplyReason;
        _deferredDisplayApplyQueued = false;
        _deferredDisplayApplyKind = DeferredDisplayApplyKind.None;
        if (applyKind != DeferredDisplayApplyKind.None)
            ApplyDisplaySettings(applyKind, $"deferred-{reason}");
    }

    private static void ApplyAndroidScreenOrientationSetting()
    {
        if (!OS.GetName().Equals("Android", StringComparison.OrdinalIgnoreCase))
            return;
        var mode = GetAndroidScreenRotationMode();
        if (mode == ScreenRotationUserLandscape)
        {
            ApplyAndroidActivityOrientation();
            return;
        }
        var orientation = mode switch
        {
            ScreenRotationLandscape => DisplayServer.ScreenOrientation.Landscape,
            ScreenRotationReverseLandscape => DisplayServer.ScreenOrientation.ReverseLandscape,
            _ => DisplayServer.ScreenOrientation.SensorLandscape,
        };
        DisplayServer.ScreenSetOrientation(orientation);
        ApplyAndroidActivityOrientation();
    }

    private static void ApplyAndroidActivityOrientation()
    {
        try
        {
            var javaClassWrapper = Engine.GetSingleton("JavaClassWrapper");
            var wrapper = (GodotObject)javaClassWrapper.Call("wrap", "com.godot.game.GodotApp");
            wrapper.Call("applySelectedScreenOrientationFromGame");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"GodotApp.applySelectedScreenOrientationFromGame bridge failed: {exception.Message}");
        }
    }

    internal static string GetAndroidScreenRotationMode()
    {
        var fallback = AndroidSettingsBridge.GetBool("android_flip_screen_180", false)
            ? ScreenRotationReverseLandscape
            : ScreenRotationUserLandscape;
        return NormalizeScreenRotationMode(AndroidSettingsBridge.GetString("android_screen_rotation_mode", fallback));
    }

    internal static string NormalizeScreenRotationMode(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return ScreenRotationUserLandscape;
        var normalized = value.Trim().ToLowerInvariant().Replace('-', '_').Replace(" ", "_");
        return normalized switch
        {
            "none" or "normal" or "no_rotate" or "no_rotation" or ScreenRotationLandscape => ScreenRotationLandscape,
            "180" or "flip_180" or "rotate_180" or "reverse" or ScreenRotationReverseLandscape => ScreenRotationReverseLandscape,
            "user" or "system" or "follow_system" or ScreenRotationUserLandscape => ScreenRotationUserLandscape,
            ScreenRotationAuto or "sensor" or "sensor_landscape" or "auto_rotate" or "auto_rotation" => ScreenRotationAuto,
            _ => ScreenRotationUserLandscape,
        };
    }

    private static void ApplyFontSizeSetting()
    {
        var scaleMultiplier = GetUiFontScaleMultiplier();
        if (!Mathf.IsEqualApprox(ThemeDB.FallbackBaseScale, scaleMultiplier))
            ThemeDB.FallbackBaseScale = scaleMultiplier;
        var window = GetRootWindow();
        if (window != null)
        {
            ApplyFontSizeOverridesRecursive(window);
            window.PropagateNotification((int)Control.NotificationThemeChanged);
        }
    }

    private static Vector2I GetAspectContentScaleSize(AspectRatioSetting aspect)
    {
        return aspect switch
        {
            AspectRatioSetting.FourByThree => new Vector2I(1680, 1260),
            AspectRatioSetting.SixteenByTen => new Vector2I(1920, 1200),
            AspectRatioSetting.SixteenByNine => new Vector2I(1920, 1080),
            AspectRatioSetting.TwentyOneByNine => new Vector2I(2580, 1080),
            _ => UiScalePatches.GetScaledContentSize(),
        };
    }

    private static bool SetContentScaleModeIfChanged(Window window, Window.ContentScaleModeEnum mode)
    {
        if (window.ContentScaleMode == mode)
            return false;
        window.ContentScaleMode = mode;
        return true;
    }

    private static bool SetContentScaleAspectIfChanged(Window window, Window.ContentScaleAspectEnum aspect)
    {
        if (window.ContentScaleAspect == aspect)
            return false;
        window.ContentScaleAspect = aspect;
        return true;
    }

    private static bool SetContentScaleSizeIfChanged(Window window, Vector2I size)
    {
        if (window.ContentScaleSize == size)
            return false;
        window.ContentScaleSize = size;
        return true;
    }

    private static bool SetContentScaleFactorIfChanged(Window window, float factor)
    {
        if (Mathf.IsEqualApprox(window.ContentScaleFactor, factor))
            return false;
        window.ContentScaleFactor = factor;
        return true;
    }

    internal static int GetUiFontScalePercent() => Mathf.Clamp(AndroidSettingsBridge.GetInt("ui_font_scale_percent", 100), 50, 200);

    internal static float GetUiFontScaleMultiplier() => GetUiFontScalePercent() / 100f;

    private static float GetGlobalScale() => Mathf.Clamp(AndroidSettingsBridge.GetFloat("global_scale", 1f), 0.5f, 4f);

    private static Vector2I GetFullscreenRenderSize()
    {
        var size = AndroidSettingsBridge.GetSize("fullscreen_render_size");
        return new Vector2I(size.X, size.Y);
    }

    private static Window GetRootWindow()
    {
        if (Engine.GetMainLoop() is SceneTree tree)
            return tree.Root;
        return null;
    }

    internal static void ApplyFontSizeOverridesRecursive(Node node)
    {
        if (node is Control control)
            ApplyFontSizeOverrides(control);
        foreach (Node child in node.GetChildren())
            ApplyFontSizeOverridesRecursive(child);
    }

    internal static void ApplyFontSizeOverridesToAddedNode(Node node)
    {
        if (node is Control control)
            ApplyFontSizeOverrides(control);
    }

    private static void ApplyFontSizeOverrides(Control control)
    {
        if (ShouldSkipFontOverrideScaling(control))
            return;
        var scaleMultiplier = GetUiFontScaleMultiplier();
        foreach (var fontSizeOverrideName in FontSizeOverrideNames)
        {
            if (!TryGetFontBaseSize(control, fontSizeOverrideName, out var baseSize))
                continue;
            var scaledSize = Mathf.Max(1, Mathf.RoundToInt(baseSize * scaleMultiplier));
            control.AddThemeFontSizeOverride(fontSizeOverrideName, scaledSize);
        }
    }

    private static bool TryGetFontBaseSize(Control control, StringName fontSizeOverrideName, out int baseSize)
    {
        var metaKey = $"__android_port_base_size__{fontSizeOverrideName}";
        if (control.HasMeta(metaKey))
        {
            baseSize = GetFontBaseSizeFromMeta(control.GetMeta(metaKey));
            return baseSize > 0;
        }

        if (control.HasThemeFontSizeOverride(fontSizeOverrideName))
        {
            baseSize = control.GetThemeFontSize(fontSizeOverrideName);
        }
        else if (ShouldSeedDefaultFontSize(control, fontSizeOverrideName))
        {
            var themeType = control.GetClass();
            baseSize = string.IsNullOrWhiteSpace(themeType)
                ? control.GetThemeFontSize(fontSizeOverrideName)
                : control.GetThemeFontSize(fontSizeOverrideName, themeType);
            if (baseSize <= 0)
                baseSize = control.GetThemeFontSize(fontSizeOverrideName);
        }
        else
        {
            baseSize = 0;
        }

        if (baseSize <= 0)
            return false;
        control.SetMeta(metaKey, baseSize);
        return true;
    }

    private static bool ShouldSeedDefaultFontSize(Control control, StringName fontSizeOverrideName)
    {
        if (fontSizeOverrideName == "font_size")
            return control is Label or Button or LineEdit or TextEdit;
        if (control is RichTextLabel)
        {
            return fontSizeOverrideName == "normal_font_size"
                || fontSizeOverrideName == "bold_font_size"
                || fontSizeOverrideName == "italics_font_size"
                || fontSizeOverrideName == "bold_italics_font_size"
                || fontSizeOverrideName == "mono_font_size";
        }
        return false;
    }

    private static bool ShouldSkipFontOverrideScaling(Control control)
    {
        if (control is MegaLabel { AutoSizeEnabled: true } megaLabel)
        {
            if (!HasSourcePortMegaTextScaling())
                ApplyAutoSizeFontScaling(megaLabel);
            return true;
        }
        if (control is MegaRichTextLabel { AutoSizeEnabled: true } megaRichTextLabel)
        {
            if (!HasSourcePortMegaTextScaling())
                ApplyAutoSizeFontScaling(megaRichTextLabel);
            return true;
        }
        return false;
    }

    private static void ApplyAutoSizeFontScaling(MegaLabel label)
    {
        if (!label.HasThemeFontOverride("font"))
            return;
        int baseMin = GetOrStoreIntMeta(label, "__android_port_base_min_font_size", label.MinFontSize);
        int baseMax = GetOrStoreIntMeta(label, "__android_port_base_max_font_size", label.MaxFontSize);
        float scale = GetUiFontScaleMultiplier();
        label.MinFontSize = Mathf.Max(1, Mathf.RoundToInt(baseMin * scale));
        label.MaxFontSize = Mathf.Max(label.MinFontSize, Mathf.RoundToInt(baseMax * scale));
        InvokeAdjustFontSize(label);
    }

    private static void ApplyAutoSizeFontScaling(MegaRichTextLabel label)
    {
        if (!label.HasThemeFontOverride("normal_font"))
            return;
        int baseMin = GetOrStoreIntMeta(label, "__android_port_base_min_font_size", label.MinFontSize);
        int baseMax = GetOrStoreIntMeta(label, "__android_port_base_max_font_size", label.MaxFontSize);
        float scale = GetUiFontScaleMultiplier();
        label.MinFontSize = Mathf.Max(1, Mathf.RoundToInt(baseMin * scale));
        label.MaxFontSize = Mathf.Max(label.MinFontSize, Mathf.RoundToInt(baseMax * scale));
        SetPrivateField(label, "_needsResize", true);
        InvokeAdjustFontSize(label);
    }

    private static bool HasSourcePortMegaTextScaling()
    {
        if (!_hasSourcePortMegaTextScaling.HasValue)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            _hasSourcePortMegaTextScaling = typeof(MegaLabel).GetField("_lastAppliedScaledFontSize", flags) != null
                || typeof(MegaRichTextLabel).GetField("_sourceText", flags) != null;
        }
        return _hasSourcePortMegaTextScaling.Value;
    }

    private static int GetOrStoreIntMeta(GodotObject obj, string metaKey, int currentValue)
    {
        if (obj.HasMeta(metaKey))
            return GetFontBaseSizeFromMeta(obj.GetMeta(metaKey));
        obj.SetMeta(metaKey, currentValue);
        return currentValue;
    }

    private static void SetPrivateField(object target, string fieldName, object value)
    {
        try
        {
            target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(target, value);
        }
        catch
        {
        }
    }

    private static void InvokeAdjustFontSize(object target)
    {
        try
        {
            target.GetType().GetMethod("AdjustFontSize", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(target, null);
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Auto-size font scaling failed on {target.GetType().Name}: {exception.Message}");
        }
    }

    private static int GetFontBaseSizeFromMeta(Variant metaValue)
    {
        return metaValue.VariantType switch
        {
            Variant.Type.Int => metaValue.AsInt32(),
            Variant.Type.Float => Mathf.RoundToInt((float)metaValue.AsDouble()),
            Variant.Type.String => int.TryParse(metaValue.AsString(), out var parsed) ? parsed : 0,
            _ => metaValue.Obj is IConvertible convertible ? Convert.ToInt32(convertible) : 0,
        };
    }
}
