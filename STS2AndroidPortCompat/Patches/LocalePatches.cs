using System;
using System.Globalization;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Localization.Fonts;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Platform.Null;
using MegaCrit.Sts2.Core.TestSupport;

namespace STS2Mobile.Patches;

/// <summary>
/// Android / HarmonyOS can report locales such as zh_CN_#Hans.  The original
/// PC NullPlatformUtilStrategy replaces underscores with dashes and then feeds
/// the result directly to CultureInfo, producing zh-CN-#Hans and crashing with
/// CultureNotFoundException during LocManager initialization.
///
/// Mirror the source Android port fix here in the compatibility DLL: sanitize
/// platform locale strings before exposing them to the game and resolve common
/// Android locale families directly to the game's three-letter language codes.
/// </summary>
public static class LocalePatches
{
    public static void Apply(Harmony harmony)
    {
        var rawLanguagePrefix = PatchHelper.Method(typeof(LocalePatches), nameof(GetRawLanguagePrefix));
        var threeLetterPrefix = PatchHelper.Method(typeof(LocalePatches), nameof(GetThreeLetterLanguageCodePrefix));

        // Patch both the public PlatformUtil facade and the underlying null
        // strategy.  LocManager normally calls the facade, while patching the
        // strategy also protects any direct IPlatformUtilStrategy users.
        PatchHelper.Patch(harmony, typeof(PlatformUtil), "GetRawLanguage", prefix: rawLanguagePrefix);
        PatchHelper.Patch(harmony, typeof(PlatformUtil), "GetThreeLetterLanguageCode", prefix: threeLetterPrefix);
        PatchHelper.Patch(harmony, typeof(NullPlatformUtilStrategy), "GetRawLanguage", prefix: rawLanguagePrefix);
        PatchHelper.Patch(harmony, typeof(NullPlatformUtilStrategy), "GetThreeLetterLanguageCode", prefix: threeLetterPrefix);

        PatchHelper.Patch(
            harmony,
            typeof(FontControlUtils),
            "ApplyLocaleFontSubstitution",
            prefix: PatchHelper.Method(typeof(LocalePatches), nameof(ApplyLocaleFontSubstitutionPrefix)));
    }

    public static bool GetRawLanguagePrefix(ref string __result)
    {
        __result = GetNormalizedPlatformLocale();
        return false;
    }

    public static bool GetThreeLetterLanguageCodePrefix(ref string __result)
    {
        var rawLanguage = GetNormalizedPlatformLocale();
        __result = ResolveThreeLetterLanguageCode(rawLanguage);
        return false;
    }

    public static bool ApplyLocaleFontSubstitutionPrefix(Control control, FontType fontType, StringName themeFontName)
    {
        try
        {
            if (Engine.IsEditorHint() || TestMode.IsOn)
                return false;

            var locManager = LocManager.Instance;
            var language = locManager?.Language;
            if (string.IsNullOrEmpty(language) || !FontManager.NeedsFontSubstitution(language))
                return false;

            var substituteFont = FontManager.GetSubstituteFont(language, fontType);
            if (substituteFont != null)
                control?.AddThemeFontOverride(themeFontName, substituteFont);
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Locale font substitution skipped: {exception.Message}");
        }

        return false;
    }

    private static string GetNormalizedPlatformLocale()
    {
        try
        {
            return NormalizeLocale(OS.GetLocale());
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Failed to read OS locale, falling back to en-US: {exception.Message}");
            return "en-US";
        }
    }

    internal static string ResolveThreeLetterLanguageCode(string locale)
    {
        var normalized = NormalizeLocale(locale);
        var lower = normalized.ToLowerInvariant().Replace('-', '_');

        if (lower.StartsWith("zh", StringComparison.Ordinal))
            return "zhs";

        if (lower.StartsWith("pt", StringComparison.Ordinal))
            return "ptb";

        if (lower.StartsWith("es_419", StringComparison.Ordinal) ||
            (lower.StartsWith("es_", StringComparison.Ordinal) && !lower.StartsWith("es_es", StringComparison.Ordinal)))
        {
            return "esp";
        }

        if (TryCreateCultureInfo(normalized, out var cultureInfo) &&
            LocManager.Languages.Contains(cultureInfo.ThreeLetterISOLanguageName))
        {
            return cultureInfo.ThreeLetterISOLanguageName;
        }

        PatchHelper.Log($"Locale '{locale}' (normalized '{normalized}') could not be mapped to a supported language; using eng.");
        return "eng";
    }

    internal static bool TryCreateCultureInfo(string locale, out CultureInfo cultureInfo)
    {
        var probe = NormalizeLocale(locale);
        while (!string.IsNullOrEmpty(probe))
        {
            try
            {
                cultureInfo = new CultureInfo(probe);
                return true;
            }
            catch (CultureNotFoundException)
            {
                var separator = probe.LastIndexOf('-');
                if (separator <= 0)
                    break;
                probe = probe.Substring(0, separator);
            }
        }

        cultureInfo = null;
        return false;
    }

    internal static string NormalizeLocale(string locale)
    {
        if (string.IsNullOrWhiteSpace(locale))
            return "en-US";

        var text = locale.Trim();

        var marker = text.IndexOf('@');
        if (marker >= 0)
            text = text.Substring(0, marker);

        marker = text.IndexOf('.');
        if (marker >= 0)
            text = text.Substring(0, marker);

        text = text.Replace('_', '-');

        marker = text.IndexOf('#');
        if (marker >= 0)
        {
            text = marker > 0 && text[marker - 1] == '-'
                ? text.Substring(0, marker - 1)
                : text.Substring(0, marker);
        }

        while (text.Contains("--"))
            text = text.Replace("--", "-");

        text = text.Trim('-');
        if (string.IsNullOrWhiteSpace(text) ||
            string.Equals(text, "C", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "POSIX", StringComparison.OrdinalIgnoreCase))
        {
            return "en-US";
        }

        return text;
    }
}
