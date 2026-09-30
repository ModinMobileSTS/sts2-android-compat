using System;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;

namespace STS2Mobile.Patches;

// Repositions main menu elements for mobile viewports. Scales the background
// image to fill taller screens and adjusts button/logo placement when UI scale
// is above 100%.
public static class MobileLayoutPatches
{
    private static readonly ConditionalWeakTable<Control, ButtonLayout> ButtonLayouts = new();
    private static readonly ConditionalWeakTable<Node2D, LogoLayout> LogoLayouts = new();

    private sealed class ButtonLayout
    {
        private readonly float _left, _right, _top, _bottom;
        private readonly float _offsetLeft, _offsetRight, _offsetTop, _offsetBottom;
        private readonly Control.GrowDirection _growHorizontal, _growVertical;
        private readonly Control.SizeFlags _sizeHorizontal, _sizeVertical;

        internal ButtonLayout(Control buttons)
        {
            _left = buttons.AnchorLeft;
            _right = buttons.AnchorRight;
            _top = buttons.AnchorTop;
            _bottom = buttons.AnchorBottom;
            _offsetLeft = buttons.OffsetLeft;
            _offsetRight = buttons.OffsetRight;
            _offsetTop = buttons.OffsetTop;
            _offsetBottom = buttons.OffsetBottom;
            _growHorizontal = buttons.GrowHorizontal;
            _growVertical = buttons.GrowVertical;
            _sizeHorizontal = buttons.SizeFlagsHorizontal;
            _sizeVertical = buttons.SizeFlagsVertical;
        }

        internal void Restore(Control buttons)
        {
            buttons.AnchorLeft = _left;
            buttons.AnchorRight = _right;
            buttons.AnchorTop = _top;
            buttons.AnchorBottom = _bottom;
            buttons.OffsetLeft = _offsetLeft;
            buttons.OffsetRight = _offsetRight;
            buttons.OffsetTop = _offsetTop;
            buttons.OffsetBottom = _offsetBottom;
            buttons.GrowHorizontal = _growHorizontal;
            buttons.GrowVertical = _growVertical;
            buttons.SizeFlagsHorizontal = _sizeHorizontal;
            buttons.SizeFlagsVertical = _sizeVertical;
        }
    }

    private sealed class LogoLayout
    {
        internal readonly Vector2 Position;
        internal LogoLayout(Node2D logo) => Position = logo.Position;
    }

    public static void Apply(Harmony harmony)
    {
        var sts2Asm = typeof(MegaCrit.Sts2.Core.Nodes.NGame).Assembly;

        var nMainMenuType = sts2Asm.GetType("MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NMainMenu");
        if (nMainMenuType != null)
        {
            PatchHelper.Patch(
                harmony,
                nMainMenuType,
                "_Ready",
                postfix: PatchHelper.Method(
                    typeof(MobileLayoutPatches),
                    nameof(MainMenuReadyPostfix)
                )
            );
        }
    }

    public static void MainMenuReadyPostfix(object __instance)
    {
        try
        {
            var menu = (Node)__instance;
            ApplyMainMenuLayout(menu);

            UiScalePatches.ObserveScale(menu, () => ApplyMainMenuLayout(menu));
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"MainMenuReadyPostfix failed: {ex.Message}");
        }
    }

    private static void ApplyMainMenuLayout(Node menu)
    {
        var window = menu.GetTree().Root;
        var vpSize = window.ContentScaleSize;

        // Scale background to fill the viewport on taller screens.
        if (SaveManager.Instance.SettingsSave.AspectRatioSetting == AspectRatioSetting.Auto)
            ScaleMainMenuBg(menu, window.GetVisibleRect().Size);

        UiScalePatches.EnsureUiScaleLoaded();
        var buttons = menu.GetNodeOrNull<Control>("%MainMenuTextButtons");
        var logo = menu.GetNodeOrNull<Node>("%MainMenuBg")?.GetNodeOrNull<Node2D>("%Logo");
        if (UiScalePatches.UiScalePercent <= 100)
        {
            if (buttons != null && ButtonLayouts.TryGetValue(buttons, out var buttonLayout))
            {
                buttonLayout.Restore(buttons);
                ButtonLayouts.Remove(buttons);
            }
            if (logo != null && LogoLayouts.TryGetValue(logo, out var logoLayout))
            {
                logo.Position = logoLayout.Position;
                LogoLayouts.Remove(logo);
            }
            return;
        }

        // Capture neutral geometry once per node, never our previous scaled result.
        if (buttons != null)
        {
            ButtonLayouts.GetValue(buttons, static node => new ButtonLayout(node));
            buttons.AnchorLeft = 0f;
            buttons.AnchorRight = 0.5f;
            buttons.AnchorTop = 0f;
            buttons.AnchorBottom = 1f;
            buttons.OffsetLeft = 0f;
            buttons.OffsetRight = 0f;
            buttons.OffsetTop = 0f;
            buttons.OffsetBottom = 0f;
            buttons.GrowHorizontal = Control.GrowDirection.Both;
            buttons.GrowVertical = Control.GrowDirection.Both;
            buttons.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
            buttons.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        }

        if (logo != null)
        {
            var baseline = LogoLayouts.GetValue(logo, static node => new LogoLayout(node));
            logo.Position = new Vector2(baseline.Position.X + vpSize.X * 0.25f, baseline.Position.Y);
        }

        PatchHelper.Log("Main menu: repositioned buttons left, logo right");
    }

    private static void ScaleMainMenuBg(Node menu, Vector2 vpSize)
    {
        try
        {
            var bg = menu.GetNodeOrNull<Control>("%MainMenuBg");
            if (bg == null)
                return;

            var bgContainer = bg.GetNodeOrNull<Control>("BgContainer");
            if (bgContainer == null)
                return;

            // BgContainer is 2560x1200. On taller screens it doesn't fill vertically.
            const float bgHeight = 1200f;
            float vpHeight = vpSize.Y;

            if (vpHeight <= bgHeight)
            {
                bgContainer.Scale = Vector2.One;
                return;
            }

            float scale = vpHeight / bgHeight;
            bgContainer.Scale = new Vector2(scale, scale);
            PatchHelper.Log($"Main menu bg: scaled by {scale:F3} for viewport height {vpHeight}");
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"ScaleMainMenuBg failed: {ex.Message}");
        }
    }
}
