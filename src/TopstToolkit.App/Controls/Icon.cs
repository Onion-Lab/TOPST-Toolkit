using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TopstToolkit.App.Controls;

/// <summary>
/// Line icons from Windows' own icon font (Segoe Fluent Icons on Windows 11, Segoe MDL2 Assets on Windows 10 —
/// the code points are shared). Screens name icons through this enum only, so the set can be swapped for
/// Fluent System Icons later without touching the screens.
/// </summary>
public enum IconGlyph
{
    None = 0,
    Home = 0xE80F,
    Folder = 0xE8B7,
    FolderOpen = 0xE838,
    Document = 0xE8A5,
    Photo = 0xE91B,
    Add = 0xE710,
    Accept = 0xE73E,
    Cancel = 0xE711,
    Play = 0xE768,
    Stop = 0xE71A,
    Refresh = 0xE72C,
    Upload = 0xE898,
    Help = 0xE897,
    Info = 0xE946,
    Warning = 0xE7BA,
    Settings = 0xE713,
    Search = 0xE721,
    Shield = 0xEA18,
    Graph = 0xE9D9,
    Package = 0xE7B8,
    Tag = 0xE8EC,
    Scan = 0xE8B3,
    Grid = 0xF0E2,
    Speed = 0xEC4A,
    Terminal = 0xE756,
    ChevronDown = 0xE70D,
    ChevronUp = 0xE70E,
    ChevronRight = 0xE76C,
    ArrowLeft = 0xE72B,
    ArrowRight = 0xE72A,
    More = 0xE712,
    Chip = 0xE950,
    Video = 0xE714,
    Camera = 0xE722,
    Download = 0xE896,
    Layers = 0xE81E,
    Heat = 0xE9CA,
    Power = 0xE7E8,
    Edit = 0xE70F,
    Copy = 0xE8C8,
    Delete = 0xE74D,
    ChromeMinimize = 0xE921,
    ChromeMaximize = 0xE922,
    ChromeRestore = 0xE923,
    ChromeClose = 0xE8BB,
}

public sealed class Icon : TextBlock
{
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(IconGlyph), typeof(Icon),
        new PropertyMetadata(IconGlyph.None, (d, e) => ((Icon)d).Text = (IconGlyph)e.NewValue == IconGlyph.None ? "" : char.ConvertFromUtf32((int)(IconGlyph)e.NewValue)));

    public Icon()
    {
        FontFamily = IconFont;
        FontSize = 16;
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalAlignment = HorizontalAlignment.Center;
        TextAlignment = TextAlignment.Center;
    }

    public IconGlyph Glyph
    {
        get => (IconGlyph)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }
}
