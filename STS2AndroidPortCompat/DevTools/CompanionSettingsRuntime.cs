using System;
using System.Text.Json.Nodes;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Nodes.Combat;
using STS2Mobile.Android;
using STS2Mobile.Patches;

namespace STS2Mobile.DevTools;

/// <summary>
/// Shared runtime apply hooks for companion settings changed from overlay or in-game UI.
/// </summary>
public static class CompanionSettingsRuntime
{
    public static void ApplyAfterChange(JsonArray keys)
    {
        try
        {
            AndroidSettingsBridge.InvalidateCache();
            var applyDisplay = keys == null || keys.Count == 0;
            var applyTooltip = applyDisplay;
            var applyPreload = applyDisplay;
            var applyHand = applyDisplay;

            if (keys != null)
            {
                applyDisplay = false;
                applyTooltip = false;
                applyPreload = false;
                applyHand = false;
                foreach (var node in keys)
                {
                    var key = node?.GetValue<string>() ?? "";
                    switch (key)
                    {
                        case "mobile_tooltip_mode":
                        case "mobile_tooltip_long_press_ms":
                            applyTooltip = true;
                            break;
                        case "android_screen_rotation_mode":
                        case "android_flip_screen_180":
                        case "ui_font_scale_percent":
                        case "global_scale":
                        case "fullscreen_render_size":
                        case "aspect_ratio":
                        case "vsync":
                        case "msaa":
                            applyDisplay = true;
                            break;
                        case "preload_enabled":
                            applyPreload = true;
                            break;
                        case "show_more_hand_card_text":
                        case "show_more_hand_card_text_lift_height_percent":
                        case "touch_lift_preview":
                            applyHand = true;
                            break;
                        default:
                            break;
                    }
                }
            }

            if (applyDisplay)
                DisplaySettingsPatches.ApplyRuntimeDisplaySettings();
            if (applyTooltip)
                MobileTooltipPatches.RefreshModeFromSettings();
            if (applyPreload || keys == null || keys.Count == 0)
                PreloadManager.Enabled = AndroidSettingsBridge.GetBool("preload_enabled", true);
            if (applyHand)
            {
                try
                {
                    NPlayerHand.Instance?.ForceRefreshCardIndices();
                }
                catch (Exception exception)
                {
                    PatchHelper.Log($"[DevTools] hand refresh failed: {exception.Message}");
                }
            }
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"[DevTools] apply settings failed: {exception.Message}");
        }
    }
}
