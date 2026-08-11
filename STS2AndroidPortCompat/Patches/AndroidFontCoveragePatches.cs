using System;
using System.Collections.Generic;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Localization.Fonts;
using MegaCrit.Sts2.Core.Nodes;

namespace STS2Mobile.Patches;

/// <summary>
/// Applies Android font scaling and supplies explicit locale font fallbacks to
/// runtime-created UI without patching Godot's hot AddChild path.
///
/// Android system font discovery is vendor-dependent. In particular, Samsung
/// firmware can leave Godot's built-in Open Sans fallback without CJK glyphs.
/// STS2 already ships locale fonts, so preserve each control's base font and
/// append the matching STS2 font instead of relying on the device font config.
///
/// The previous font-size implementation Harmony-patched Node.AddChild and
/// recursively rescanned every added Control tree. Deck/compendium/combat
/// screens create many UI nodes, so rapid toggles could build up large deferred
/// font work and stall the game. Subscribe to SceneTree.NodeAdded once from
/// NGame._Ready and process only the reported node. Full recursive passes remain
/// limited to initial installation or explicit settings refreshes.
/// </summary>
public static class AndroidFontCoveragePatches
{
    private static readonly Dictionary<(ulong BaseFontId, ulong LocaleFontId), Font> LocaleFallbackCache = new();
    private static readonly Dictionary<ulong, Font> WrappedBaseFonts = new();
    private static readonly HashSet<ulong> FontsWithLocaleCoverage = new();

    private static SceneTree _subscribedTree;
    private static string _locale = string.Empty;
    private static long _localeProbe;
    private static Font _localeRegularFont;
    private static Font _localeBoldFont;
    private static Font _localeItalicFont;
    private static Font _themeFallbackBase;
    private static Font _installedThemeFallback;

    public static void Apply(Harmony harmony)
    {
        PatchHelper.Patch(harmony, typeof(NGame), "_Ready", postfix: PatchHelper.Method(typeof(AndroidFontCoveragePatches), nameof(GameReadyPostfix)));
    }

    public static void GameReadyPostfix(NGame __instance)
    {
        try
        {
            var tree = __instance?.GetTree();
            if (tree == null)
                return;

            if (!ReferenceEquals(_subscribedTree, tree))
            {
                if (_subscribedTree != null && GodotObject.IsInstanceValid(_subscribedTree))
                {
                    try
                    {
                        _subscribedTree.NodeAdded -= OnSceneTreeNodeAdded;
                    }
                    catch
                    {
                    }
                }
                _subscribedTree = tree;
                _subscribedTree.NodeAdded += OnSceneTreeNodeAdded;
            }

            bool localeFallbackActive = false;
            int localeOverrides = 0;
            try
            {
                localeFallbackActive = InstallLocaleFallback();
                localeOverrides = localeFallbackActive ? ApplyLocaleFallbackRecursive(tree.Root) : 0;
            }
            catch (Exception exception)
            {
                PatchHelper.Log($"Android explicit locale font fallback installation failed: {exception.Message}");
            }

            PatchHelper.Log(
                $"Android font coverage installed via SceneTree.NodeAdded; locale={(_locale.Length == 0 ? "none" : _locale)}; explicit_fallback={localeFallbackActive}; existing_overrides={localeOverrides}.");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Android font coverage subscription failed: {exception.Message}");
        }
    }

    public static void RegisterTreePostfix(Node node)
    {
        try
        {
            if (node == null)
                return;
            ApplyLocaleFallbackRecursive(node);
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Android locale font fallback explicit scan failed on {node?.GetType().Name}: {exception.Message}");
        }
        try
        {
            if (node != null)
                DisplaySettingsPatches.ApplyFontSizeOverridesRecursive(node);
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Android font-size explicit scan failed on {node?.GetType().Name}: {exception.Message}");
        }
    }

    private static bool InstallLocaleFallback()
    {
        string language = LocManager.Instance?.Language ?? string.Empty;
        if (!FontManager.NeedsFontSubstitution(language))
        {
            RestoreThemeFallback();
            ResetLocaleState(language);
            return false;
        }

        long probe = GetLocaleProbe(language);
        Font regular = FontManager.GetSubstituteFont(language, FontType.Regular);
        Font bold = FontManager.GetSubstituteFont(language, FontType.Bold) ?? regular;
        Font italic = FontManager.GetSubstituteFont(language, FontType.Italic) ?? regular;
        if (probe == 0 || !IsUsableFont(regular) || !HasGlyph(regular, probe))
        {
            RestoreThemeFallback();
            ResetLocaleState(language);
            PatchHelper.Log($"Android locale font fallback unavailable for language={language}; probe=U+{probe:X4}.");
            return false;
        }

        Font currentThemeFallback = ThemeDB.FallbackFont;
        if (IsSameFont(currentThemeFallback, _installedThemeFallback) && IsUsableFont(_themeFallbackBase))
            currentThemeFallback = _themeFallbackBase;

        _locale = language;
        _localeProbe = probe;
        _localeRegularFont = regular;
        _localeBoldFont = IsUsableFont(bold) ? bold : regular;
        _localeItalicFont = IsUsableFont(italic) ? italic : regular;
        LocaleFallbackCache.Clear();
        FontsWithLocaleCoverage.Clear();

        _themeFallbackBase = currentThemeFallback;
        _installedThemeFallback = CreateLocaleFallback(currentThemeFallback, regular);
        if (IsUsableFont(_installedThemeFallback))
            ThemeDB.FallbackFont = _installedThemeFallback;

        PatchHelper.Log(
            $"Android explicit locale font fallback ready: language={language}; probe=U+{probe:X4}; theme_wrapped={!IsSameFont(currentThemeFallback, _installedThemeFallback)}.");
        return true;
    }

    private static void RestoreThemeFallback()
    {
        if (IsUsableFont(_themeFallbackBase) && IsSameFont(ThemeDB.FallbackFont, _installedThemeFallback))
            ThemeDB.FallbackFont = _themeFallbackBase;
        _installedThemeFallback = null;
        _themeFallbackBase = null;
    }

    private static void ResetLocaleState(string language)
    {
        _locale = language;
        _localeProbe = 0;
        _localeRegularFont = null;
        _localeBoldFont = null;
        _localeItalicFont = null;
        LocaleFallbackCache.Clear();
        FontsWithLocaleCoverage.Clear();
    }

    private static long GetLocaleProbe(string language)
    {
        return language switch
        {
            "zhs" => '汉',
            "zht" => '漢',
            "jpn" => '日',
            "kor" => '한',
            "tha" => 'ก',
            "rus" => 'Ж',
            "pol" => 'Ł',
            _ => 0,
        };
    }

    private static int ApplyLocaleFallbackRecursive(Node node)
    {
        if (node == null || !IsUsableFont(_localeRegularFont))
            return 0;
        int changed = ApplyLocaleFallbackToNode(node);
        foreach (Node child in node.GetChildren())
            changed += ApplyLocaleFallbackRecursive(child);
        return changed;
    }

    private static int ApplyLocaleFallbackToNode(Node node)
    {
        if (!IsUsableFont(_localeRegularFont))
            return 0;

        if (node is RichTextLabel richTextLabel)
        {
            int changed = 0;
            changed += ApplyControlFontFallback(richTextLabel, "normal_font", _localeRegularFont);
            changed += ApplyControlFontFallback(richTextLabel, "bold_font", _localeBoldFont);
            changed += ApplyControlFontFallback(richTextLabel, "italics_font", _localeItalicFont);
            changed += ApplyControlFontFallback(richTextLabel, "bold_italics_font", _localeBoldFont);
            changed += ApplyControlFontFallback(richTextLabel, "mono_font", _localeRegularFont);
            return changed;
        }

        if (node is Label label)
            return ApplyControlFontFallback(label, "font", _localeRegularFont);

        if (node is Button || node is LineEdit || node is TextEdit || node is ItemList || node is Tree || node is TabBar)
            return ApplyControlFontFallback((Control)node, "font", _localeRegularFont);

        if (node is Label3D label3D)
        {
            Font baseFont = UnwrapManagedFallback(label3D.Font ?? ThemeDB.FallbackFont);
            Font fallbackFont = CreateLocaleFallback(baseFont, _localeRegularFont);
            if (!IsUsableFont(fallbackFont) || IsSameFont(label3D.Font, fallbackFont))
                return 0;
            label3D.Font = fallbackFont;
            return 1;
        }

        return 0;
    }

    private static int ApplyControlFontFallback(Control control, StringName themeName, Font localeFont)
    {
        if (control == null || !IsUsableFont(localeFont))
            return 0;
        Font currentFont = control.GetThemeFont(themeName);
        Font baseFont = UnwrapManagedFallback(currentFont);
        Font fallbackFont = CreateLocaleFallback(baseFont, localeFont);
        if (!IsUsableFont(fallbackFont) || IsSameFont(currentFont, fallbackFont))
            return 0;
        control.AddThemeFontOverride(themeName, fallbackFont);
        return 1;
    }

    private static Font CreateLocaleFallback(Font baseFont, Font localeFont)
    {
        if (!IsUsableFont(localeFont))
            return baseFont;
        if (!IsUsableFont(baseFont))
            return localeFont;

        baseFont = UnwrapManagedFallback(baseFont);
        ulong baseId = baseFont.GetInstanceId();
        ulong localeId = localeFont.GetInstanceId();
        if (baseId == localeId || FontHasLocaleCoverage(baseFont))
            return baseFont;

        var key = (baseId, localeId);
        if (LocaleFallbackCache.TryGetValue(key, out Font cached) && IsUsableFont(cached))
            return cached;

        var fallbacks = new Godot.Collections.Array<Font>();
        AppendExistingFallbacks(baseFont, fallbacks);
        if (!ContainsFont(fallbacks, localeFont))
            fallbacks.Add(localeFont);

        var combined = new FontVariation
        {
            BaseFont = baseFont,
            Fallbacks = fallbacks,
        };
        LocaleFallbackCache[key] = combined;
        WrappedBaseFonts[combined.GetInstanceId()] = baseFont;
        return combined;
    }

    private static void AppendExistingFallbacks(Font font, Godot.Collections.Array<Font> destination)
    {
        if (!IsUsableFont(font))
            return;
        if (font.Fallbacks.Count > 0)
        {
            foreach (Font existing in font.Fallbacks)
            {
                if (IsUsableFont(existing) && !ContainsFont(destination, existing))
                    destination.Add(existing);
            }
            return;
        }
        if (font is FontVariation variation && IsUsableFont(variation.BaseFont))
            AppendExistingFallbacks(variation.BaseFont, destination);
    }

    private static Font UnwrapManagedFallback(Font font)
    {
        if (!IsUsableFont(font))
            return font;
        return WrappedBaseFonts.TryGetValue(font.GetInstanceId(), out Font baseFont) && IsUsableFont(baseFont)
            ? baseFont
            : font;
    }

    private static bool FontHasLocaleCoverage(Font font)
    {
        if (!IsUsableFont(font) || _localeProbe == 0)
            return false;
        ulong id = font.GetInstanceId();
        if (FontsWithLocaleCoverage.Contains(id))
            return true;
        if (!HasGlyph(font, _localeProbe))
            return false;
        FontsWithLocaleCoverage.Add(id);
        return true;
    }

    private static bool ContainsFont(Godot.Collections.Array<Font> fonts, Font expected)
    {
        foreach (Font font in fonts)
        {
            if (IsSameFont(font, expected))
                return true;
        }
        return false;
    }

    private static bool HasGlyph(Font font, long codePoint)
    {
        try
        {
            return IsUsableFont(font) && font.HasChar(codePoint);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSameFont(Font left, Font right)
    {
        return IsUsableFont(left) && IsUsableFont(right) && left.GetInstanceId() == right.GetInstanceId();
    }

    private static bool IsUsableFont(Font font)
    {
        return font != null && GodotObject.IsInstanceValid(font);
    }

    private static void OnSceneTreeNodeAdded(Node node)
    {
        try
        {
            ApplyLocaleFallbackToNode(node);
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Android locale font fallback apply failed on {node?.GetType().Name}: {exception.Message}");
        }
        try
        {
            DisplaySettingsPatches.ApplyFontSizeOverridesToAddedNode(node);
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Android font-size apply failed on {node?.GetType().Name}: {exception.Message}");
        }
    }
}
