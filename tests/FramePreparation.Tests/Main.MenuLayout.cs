using System;
using System.Threading.Tasks;
using Godot;
using STS2Mobile.Patches;

public partial class Main
{
    private static object MenuButtonGeometry(Control buttons) => (
        buttons.AnchorLeft, buttons.AnchorRight, buttons.AnchorTop, buttons.AnchorBottom,
        buttons.OffsetLeft, buttons.OffsetRight, buttons.OffsetTop, buttons.OffsetBottom,
        buttons.GrowHorizontal, buttons.GrowVertical, buttons.SizeFlagsHorizontal, buttons.SizeFlagsVertical);

    private void ApplyMenuScale(int scale, int width)
    {
        // Configure the real observer input without writing any user settings.
        UiScalePatches.EnsureUiScaleLoaded();
        typeof(UiScalePatches).GetProperty(nameof(UiScalePatches.UiScalePercent)).SetValue(null, scale);
        GetTree().Root.ContentScaleSize = new Vector2I(width, 1080);
        UiScalePatches.ApplyUiScale();
    }

    private async Task CheckMenuLayoutRoundTrip()
    {
        var window = GetTree().Root;
        Vector2I previousSize = window.ContentScaleSize;
        UiScalePatches.EnsureUiScaleLoaded();
        int previousScale = UiScalePatches.UiScalePercent;
        var menu = new Control { Size = new Vector2(1680, 1080) };
        var buttons = new Control { Name = "MainMenuTextButtons" };
        var background = new Control { Name = "MainMenuBg" };
        var logo = new Node2D { Name = "Logo", Position = new Vector2(128, 75) };
        menu.AddChild(buttons);
        menu.AddChild(background);
        background.AddChild(logo);
        foreach (var node in new Node[] { buttons, background, logo })
        {
            node.Owner = menu;
            node.UniqueNameInOwner = true;
        }
        AddChild(menu);
        try
        {
            buttons.AnchorLeft = 0.15f;
            buttons.AnchorRight = 0.75f;
            buttons.AnchorTop = 0.12f;
            buttons.AnchorBottom = 0.82f;
            buttons.OffsetLeft = 12;
            buttons.OffsetRight = -25;
            buttons.OffsetTop = 18;
            buttons.OffsetBottom = -36;
            buttons.GrowHorizontal = Control.GrowDirection.Begin;
            buttons.GrowVertical = Control.GrowDirection.End;
            buttons.SizeFlagsHorizontal = Control.SizeFlags.Fill;
            buttons.SizeFlagsVertical = Control.SizeFlags.Expand;
            await Frame();
            object neutralButtons = MenuButtonGeometry(buttons);
            Rect2 neutralRect = buttons.GetRect();
            Vector2 neutralLogo = logo.Position;
            ApplyMenuScale(100, 1680);
            MobileLayoutPatches.MainMenuReadyPostfix(menu);
            foreach (var (scale, width) in new[] { (100, 1680), (110, 1527), (120, 1400), (110, 1527), (100, 1680) })
            {
                ApplyMenuScale(scale, width);
                await Frame();
                Require(logo.Position.IsEqualApprox(neutralLogo + new Vector2(scale > 100 ? width * 0.25f : 0, 0)),
                    "Menu logo must be positioned from neutral geometry, not accumulated offsets.");
                object once = MenuButtonGeometry(buttons);
                Rect2 onceRect = buttons.GetRect();
                Vector2 onceLogo = logo.Position;
                UiScalePatches.ApplyUiScale();
                MobileLayoutPatches.MainMenuReadyPostfix(menu);
                await Frame();
                Require(once.Equals(MenuButtonGeometry(buttons)) && onceRect.IsEqualApprox(buttons.GetRect()) && onceLogo.IsEqualApprox(logo.Position),
                    "Duplicate Ready and scale reapply must leave native geometry unchanged.");
                if (scale == 100)
                    Require(neutralButtons.Equals(MenuButtonGeometry(buttons)) && neutralRect.IsEqualApprox(buttons.GetRect()),
                        "Returning to 100% must restore the original anchors, offsets, grow, size flags and rendered rect.");
            }
            GD.Print("PASS: native main-menu 100 -> 110 -> 120 -> 110 -> 100 geometry and idempotent reapply.");
        }
        finally
        {
            menu.Free();
            typeof(UiScalePatches).GetProperty(nameof(UiScalePatches.UiScalePercent)).SetValue(null, previousScale);
            window.ContentScaleSize = previousSize;
        }
    }
}

namespace MegaCrit.Sts2.Core.Settings
{
    public enum AspectRatioSetting { Auto, Fixed }
}

namespace MegaCrit.Sts2.Core.Saves
{
    public sealed class SaveManager
    {
        public static SaveManager Instance = new();
        public Settings SettingsSave = new();
    }
    public sealed class Settings
    {
        public MegaCrit.Sts2.Core.Settings.AspectRatioSetting AspectRatioSetting;
    }
}

namespace STS2Mobile.Patches
{
    // The smoke configures the viewport explicitly; it does not replace the global display owner.
    public static class DisplaySettingsPatches
    {
        public static string CurrentContentScaleOwner => "native-menu-fixture";
        public static void ApplyUiScaleContentScaleSettings() { }
        public static void RequestDeferredContentScaleApply(string reason) { }
    }
}
