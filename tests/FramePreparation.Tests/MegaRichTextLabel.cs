using Godot;

namespace MegaCrit.Sts2.addons.mega_text;

public partial class MegaRichTextLabel : RichTextLabel
{
    public bool AutoSizeEnabled { get; set; } = true;
    public int MinFontSize { get; set; } = 10;
    public int MaxFontSize { get; set; } = 20;
    private bool _needsResize;
    private void AdjustFontSize()
    {
        if (_needsResize) AddThemeFontSizeOverride("normal_font_size", MaxFontSize);
        _needsResize = false;
    }
}
