using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using MegaCrit.Sts2.addons.mega_text;

namespace STS2Mobile.Patches;

internal static class AndroidFontSizeScaler
{
    private static readonly StringName[] Names = { "font_size", "normal_font_size", "bold_font_size", "italics_font_size", "bold_italics_font_size", "mono_font_size" };
    private static readonly StringName[] BaseKeys =
    {
        "__android_port_base_size__font_size", "__android_port_base_size__normal_font_size",
        "__android_port_base_size__bold_font_size", "__android_port_base_size__italics_font_size",
        "__android_port_base_size__bold_italics_font_size", "__android_port_base_size__mono_font_size",
    };
    private static readonly StringName ScaledKey = "__android_port_font_sizes_scaled";
    private static readonly StringName MinKey = "__android_port_base_min_font_size";
    private static readonly StringName MaxKey = "__android_port_base_max_font_size";
    private static readonly StringName FontKey = "font";
    private static readonly StringName NormalFontKey = "normal_font";
    private static readonly ConditionalWeakTable<Type, AutoSizeMethods> Methods = new();
    private static bool? _sourcePortScaling;

    internal static void ApplyRecursive(Node node, float scale)
    {
        Apply(node, scale);
        for (int i = 0; i < node.GetChildCount(); i++) ApplyRecursive(node.GetChild(i), scale);
    }

    internal static void Apply(Node node, float scale)
    {
        if (node is not Control control) return;
        if (control is MegaLabel { AutoSizeEnabled: true } label)
        {
            if (!HasSourcePortScaling()) ApplyAutoSize(label, scale);
            return;
        }
        if (control is MegaRichTextLabel { AutoSizeEnabled: true } rich)
        {
            if (!HasSourcePortScaling()) ApplyAutoSize(rich, scale);
            return;
        }
        // At the default size, leave untouched controls and inherited themes alone.
        // Previously scaled controls still take the restoration path below.
        if (scale == 1f && !control.HasMeta(ScaledKey)) return;
        bool tracked = false;
        for (int i = 0; i < Names.Length; i++)
        {
            var name = Names[i];
            int baseSize;
            if (control.HasMeta(BaseKeys[i]))
                baseSize = ReadInt(control.GetMeta(BaseKeys[i]));
            else
            {
                bool explicitSize = control.HasThemeFontSizeOverride(name);
                if (explicitSize) baseSize = control.GetThemeFontSize(name);
                else if (ShouldSeed(control, i))
                {
                    var themeType = control.GetClass();
                    baseSize = string.IsNullOrWhiteSpace(themeType) ? control.GetThemeFontSize(name) : control.GetThemeFontSize(name, themeType);
                    if (baseSize <= 0) baseSize = control.GetThemeFontSize(name);
                }
                else continue;
                if (baseSize > 0) control.SetMeta(BaseKeys[i], baseSize);
            }
            if (baseSize <= 0) continue;
            tracked = true;
            int scaled = Mathf.Max(1, Mathf.RoundToInt(baseSize * scale));
            if (control.GetThemeFontSize(name) != scaled)
                control.AddThemeFontSizeOverride(name, scaled);
        }
        if (scale == 1f) control.RemoveMeta(ScaledKey);
        else if (tracked && !control.HasMeta(ScaledKey)) control.SetMeta(ScaledKey, true);
    }

    private static bool ShouldSeed(Control control, int name) => name == 0
        ? control is Label or Button or LineEdit or TextEdit
        : control is RichTextLabel;

    private static void ApplyAutoSize(MegaLabel label, float scale)
    {
        if (!label.HasThemeFontOverride(FontKey) || scale == 1f && !label.HasMeta(MinKey)) return;
        int min = Mathf.Max(1, Mathf.RoundToInt(GetOrStore(label, MinKey, label.MinFontSize) * scale));
        int max = Mathf.Max(min, Mathf.RoundToInt(GetOrStore(label, MaxKey, label.MaxFontSize) * scale));
        if (label.MinFontSize == min && label.MaxFontSize == max) return;
        label.MinFontSize = min;
        label.MaxFontSize = max;
        Adjust(label, false);
    }

    private static void ApplyAutoSize(MegaRichTextLabel label, float scale)
    {
        if (!label.HasThemeFontOverride(NormalFontKey) || scale == 1f && !label.HasMeta(MinKey)) return;
        int min = Mathf.Max(1, Mathf.RoundToInt(GetOrStore(label, MinKey, label.MinFontSize) * scale));
        int max = Mathf.Max(min, Mathf.RoundToInt(GetOrStore(label, MaxKey, label.MaxFontSize) * scale));
        if (label.MinFontSize == min && label.MaxFontSize == max) return;
        label.MinFontSize = min;
        label.MaxFontSize = max;
        Adjust(label, true);
    }

    private static bool HasSourcePortScaling()
    {
        if (!_sourcePortScaling.HasValue)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            _sourcePortScaling = typeof(MegaLabel).GetField("_lastAppliedScaledFontSize", flags) != null
                || typeof(MegaRichTextLabel).GetField("_sourceText", flags) != null;
        }
        return _sourcePortScaling.Value;
    }

    private static int GetOrStore(GodotObject obj, StringName key, int value)
    {
        if (obj.HasMeta(key)) return ReadInt(obj.GetMeta(key));
        obj.SetMeta(key, value);
        return value;
    }

    private static void Adjust(Control control, bool rich)
    {
        try
        {
            var methods = Methods.GetValue(control.GetType(), static type => new AutoSizeMethods(type));
            if (rich) methods.NeedsResize?.SetValue(control, true);
            methods.Adjust?.Invoke(control, null);
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Auto-size font scaling failed on {control.GetType().Name}: {exception.Message}");
        }
    }

    private sealed class AutoSizeMethods
    {
        internal readonly MethodInfo Adjust;
        internal readonly FieldInfo NeedsResize;
        internal AutoSizeMethods(Type type)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            Adjust = type.GetMethod("AdjustFontSize", flags);
            NeedsResize = type.GetField("_needsResize", flags);
        }
    }

    private static int ReadInt(Variant value) => value.VariantType switch
    {
        Variant.Type.Int => value.AsInt32(),
        Variant.Type.Float => Mathf.RoundToInt((float)value.AsDouble()),
        Variant.Type.String => int.TryParse(value.AsString(), out var parsed) ? parsed : 0,
        _ => value.Obj is IConvertible convertible ? Convert.ToInt32(convertible) : 0,
    };
}
