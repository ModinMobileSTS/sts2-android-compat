using Godot;

namespace MegaCrit.Sts2.addons.mega_text;

public partial class MegaLabel : Label
{
    public bool AutoSizeEnabled { get; set; } = true;
    public int MinFontSize { get; set; } = 10;
    public int MaxFontSize { get; set; } = 20;
    private void AdjustFontSize() => AddThemeFontSizeOverride("font_size", MaxFontSize);
}
