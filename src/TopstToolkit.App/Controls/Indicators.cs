using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TopstToolkit.App.ViewModels;

namespace TopstToolkit.App.Controls;

public enum ChipKind
{
    Muted,
    Ok,
    Warn,
    Bad,
    Info,
    Running,
}

/// <summary>Pill-shaped status label (design system §8). Styled in Themes\Controls.xaml.</summary>
public sealed class Chip : Control
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(Chip), new PropertyMetadata(""));

    public static readonly DependencyProperty KindProperty =
        DependencyProperty.Register(nameof(Kind), typeof(ChipKind), typeof(Chip), new PropertyMetadata(ChipKind.Muted));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public ChipKind Kind
    {
        get => (ChipKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }
}

/// <summary>
/// The 10px state dot of a step: done (green), running (TOPST Pulse with a soft ring), needs a rerun (amber),
/// failed (red), not yet (empty ring). State is never shown by colour alone; a label or chip goes with it.
/// </summary>
public sealed class StatusDot : FrameworkElement
{
    private static readonly Brush Ok = Frozen("#18794E");
    private static readonly Brush Warn = Frozen("#D97706");
    private static readonly Brush Bad = Frozen("#C8312A");
    private static readonly Pen TodoPen = FrozenPen("#C4C8CF", 1.5);
    private static readonly Brush Halo = Frozen("#333D8AED");
    private static readonly Brush Pulse = FrozenPulse();

    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State), typeof(StepState), typeof(StatusDot),
        new FrameworkPropertyMetadata(StepState.Todo, FrameworkPropertyMetadataOptions.AffectsRender));

    public StepState State
    {
        get => (StepState)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(16, 16);

    protected override void OnRender(DrawingContext dc)
    {
        var center = new Point(8, 8);
        switch (State)
        {
            case StepState.Ok: dc.DrawEllipse(Ok, null, center, 5, 5); break;
            case StepState.Warn: dc.DrawEllipse(Warn, null, center, 5, 5); break;
            case StepState.Bad: dc.DrawEllipse(Bad, null, center, 5, 5); break;
            case StepState.Running:
                dc.DrawEllipse(Halo, null, center, 8, 8);
                dc.DrawEllipse(Pulse, null, center, 5, 5);
                break;
            default: dc.DrawEllipse(Brushes.White, TodoPen, center, 4.25, 4.25); break;
        }
    }

    private static Brush Frozen(string color)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        b.Freeze();
        return b;
    }

    private static Pen FrozenPen(string color, double thickness)
    {
        var p = new Pen(Frozen(color), thickness);
        p.Freeze();
        return p;
    }

    private static Brush FrozenPulse()
    {
        var b = new LinearGradientBrush(Color.FromRgb(0x72, 0xE7, 0xAD), Color.FromRgb(0x3D, 0x8A, 0xED), 0);
        b.Freeze();
        return b;
    }
}

/// <summary>
/// Small isometric cube drawing, the TOPST hero motif, for empty and first-run states (design system §6).
/// <c>Voxels="0,0,0;1,0,0;1,0,1"</c> lists cube positions (x, y, height).
/// </summary>
public sealed class VoxelArt : FrameworkElement
{
    private static readonly Brush Top = Frozen(0x8F, 0xA2, 0xFF);
    private static readonly Brush Left = Frozen(0x71, 0x84, 0xFD);
    private static readonly Brush Right = Frozen(0x6A, 0x78, 0xFF);

    public static readonly DependencyProperty VoxelsProperty = DependencyProperty.Register(
        nameof(Voxels), typeof(string), typeof(VoxelArt),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(
        nameof(Unit), typeof(double), typeof(VoxelArt),
        new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public string Voxels
    {
        get => (string)GetValue(VoxelsProperty);
        set => SetValue(VoxelsProperty, value);
    }

    public double Unit
    {
        get => (double)GetValue(UnitProperty);
        set => SetValue(UnitProperty, value);
    }

    private List<(double X, double Y)> Origins(out Rect bounds)
    {
        var u = Unit;
        var cubes = new List<(int I, int J, int K)>();
        foreach (var part in (Voxels ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var n = part.Split(',');
            if (n.Length == 3 && int.TryParse(n[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
                              && int.TryParse(n[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var j)
                              && int.TryParse(n[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var k))
                cubes.Add((i, j, k));
        }
        // painter's order: far cubes first, lower cubes first
        cubes.Sort((a, b) => (a.I + a.J + a.K).CompareTo(b.I + b.J + b.K) is var c and not 0 ? c : a.K.CompareTo(b.K));
        var origins = cubes.Select(c => ((c.I - c.J) * u, (c.I + c.J) * u / 2 - c.K * u)).ToList();
        if (origins.Count == 0)
        {
            bounds = Rect.Empty;
            return origins;
        }
        var minX = origins.Min(o => o.Item1) - u;
        var maxX = origins.Max(o => o.Item1) + u;
        var minY = origins.Min(o => o.Item2) - u / 2;
        var maxY = origins.Max(o => o.Item2) + u * 1.5;
        bounds = new Rect(minX, minY, maxX - minX, maxY - minY);
        return origins;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        Origins(out var bounds);
        return bounds.IsEmpty ? new Size(0, 0) : bounds.Size;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var origins = Origins(out var bounds);
        if (bounds.IsEmpty)
            return;
        var u = Unit;
        dc.PushTransform(new TranslateTransform(-bounds.X, -bounds.Y));
        foreach (var (x, y) in origins)
        {
            dc.DrawGeometry(Top, null, Polygon((x, y - u / 2), (x + u, y), (x, y + u / 2), (x - u, y)));
            dc.DrawGeometry(Left, null, Polygon((x - u, y), (x, y + u / 2), (x, y + u * 1.5), (x - u, y + u)));
            dc.DrawGeometry(Right, null, Polygon((x + u, y), (x, y + u / 2), (x, y + u * 1.5), (x + u, y + u)));
        }
        dc.Pop();
    }

    private static Geometry Polygon(params (double X, double Y)[] points)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(points[0].X, points[0].Y), isFilled: true, isClosed: true);
            foreach (var p in points.Skip(1))
                ctx.LineTo(new Point(p.X, p.Y), isStroked: false, isSmoothJoin: false);
        }
        g.Freeze();
        return g;
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
