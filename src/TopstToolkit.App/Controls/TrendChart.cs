using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace TopstToolkit.App.Controls;

/// <summary>One line of a <see cref="TrendChart"/>: its name, colour and one value per sample (NaN: no value).</summary>
public sealed record TrendLine(string Name, Color Color, IReadOnlyList<double> Values, bool Wash = false);

/// <summary>A stretch the app ran something on the board: a grey band, named above the charts that show names.</summary>
public sealed record TrendSpan(DateTimeOffset From, DateTimeOffset To, string Label);

/// <summary>One row of the box under the pointer: the value first, then what it is; without a colour, no line key.</summary>
public sealed record TrendTipRow(string Value, string Name, Color? Color = null);

/// <summary>
/// What one <see cref="TrendChart"/> shows (design §5.10 "지난 몇 분 그래프"): the sample times, oldest first; the moment
/// the right edge stands for and how far back the chart looks; how far apart two samples may be and still be joined;
/// its lines; the stretches the app ran something; the value range and the unit on its scale; and the rows the box
/// under the pointer lists for a sample, with what ran then.
/// </summary>
public sealed record TrendFrame(
    IReadOnlyList<DateTimeOffset> Times, DateTimeOffset Now, TimeSpan Window, TimeSpan Gap,
    IReadOnlyList<TrendLine> Lines, IReadOnlyList<TrendSpan> Spans,
    double Min, double Max, string ScaleUnit,
    Func<int, IReadOnlyList<TrendTipRow>> Tip, Func<int, string?> RunAt);

/// <summary>
/// A small line chart of the last minutes (보드 › 상태, docs/mockups/mockup-board-graph.html, the user's choice of
/// 2026-11-28): 2 px lines, a 10 % wash under a single line, a hairline grid, grey bands where the app ran something,
/// the newest value as a dot at the right edge. The pointer puts a hairline at the nearest sample on every chart bound
/// to the same <see cref="HoverTime"/>; the chart under the pointer also lists that sample's values in a box.
/// </summary>
public sealed class TrendChart : FrameworkElement
{
    public const double PlotHeight = 78;
    private const double Left = 30, Right = 12, NamesBand = 16, TopPlain = 4, AxisBand = 18, BottomPlain = 4;

    private static readonly Brush Muted = Draw.Brush("#6B7280");
    private static readonly Brush Heading = Draw.Brush("#111111");
    private static readonly Brush Secondary = Draw.Brush("#636363");
    private static readonly Brush Band = Draw.Brush("#ECEEF2");
    private static readonly Pen Grid = Draw.Pen("#EEF0F3", 1);
    private static readonly Pen Baseline = Draw.Pen("#D9DCE1", 1);
    private static readonly Pen Cross = Draw.Pen("#9CA3AF", 1);
    private static readonly Pen Ring = Draw.Pen("#FFFFFF", 2);
    private static readonly Brush BoxEdge = Draw.Brush("#E5E7EB");

    public static readonly DependencyProperty FrameProperty = DependencyProperty.Register(
        nameof(Frame), typeof(TrendFrame), typeof(TrendChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((TrendChart)d).RefreshTip()));

    public static readonly DependencyProperty ShowTimeAxisProperty = DependencyProperty.Register(
        nameof(ShowTimeAxis), typeof(bool), typeof(TrendChart),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowRunNamesProperty = DependencyProperty.Register(
        nameof(ShowRunNames), typeof(bool), typeof(TrendChart),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HoverTimeProperty = DependencyProperty.Register(
        nameof(HoverTime), typeof(DateTimeOffset?), typeof(TrendChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public TrendFrame? Frame { get => (TrendFrame?)GetValue(FrameProperty); set => SetValue(FrameProperty, value); }

    /// <summary>"5분 전 … 지금" under the chart (the lower chart of a card).</summary>
    public bool ShowTimeAxis { get => (bool)GetValue(ShowTimeAxisProperty); set => SetValue(ShowTimeAxisProperty, value); }

    /// <summary>
    /// The upper chart of a card: the names of the grey bands above it, and "연결한 뒤부터 그려요" in the part before
    /// the app was connected (once per card).
    /// </summary>
    public bool ShowRunNames { get => (bool)GetValue(ShowRunNamesProperty); set => SetValue(ShowRunNamesProperty, value); }

    /// <summary>The sample the pointer is at, shared by the charts of one page; null when the pointer is elsewhere.</summary>
    public DateTimeOffset? HoverTime { get => (DateTimeOffset?)GetValue(HoverTimeProperty); set => SetValue(HoverTimeProperty, value); }

    public TrendChart()
    {
        Cursor = Cursors.Cross;
        IsVisibleChanged += (_, e) => { if (!(bool)e.NewValue) Leave(); };
        Unloaded += (_, _) => Leave();
    }

    private double Top => ShowRunNames ? NamesBand : TopPlain;

    protected override Size MeasureOverride(Size available) =>
        new(double.IsInfinity(available.Width) ? 320 : available.Width, Top + PlotHeight + (ShowTimeAxis ? AxisBand : BottomPlain));

    // ---------------------------------------------------------------- drawing

    private double X(TrendFrame f, DateTimeOffset t) =>
        Left + (t - (f.Now - f.Window)).TotalSeconds / f.Window.TotalSeconds * (ActualWidth - Left - Right);

    private double Y(TrendFrame f, double v) =>
        Top + PlotHeight - (Math.Clamp(v, f.Min, f.Max) - f.Min) / (f.Max - f.Min) * PlotHeight;

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize)); // the pointer is seen over the empty parts too
        if (Frame is not { } f || ActualWidth < Left + Right + 40 || f.Window <= TimeSpan.Zero || f.Max <= f.Min)
            return;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var top = Top;
        var right = ActualWidth - Right;
        var start = f.Now - f.Window;

        // what the app ran: grey bands. Above the upper chart of a card each band is named where the name fits before
        // the next band starts (a short run in a long view may run its name into the free space after it)
        var bands = f.Spans
            .Select(s => (Span: s, From: s.From < start ? start : s.From, To: s.To > f.Now ? f.Now : s.To))
            .Where(b => b.To > b.From).OrderBy(b => b.From).ToList();
        for (var k = 0; k < bands.Count; k++)
        {
            var band = new Rect(X(f, bands[k].From), top, Math.Max(1.5, X(f, bands[k].To) - X(f, bands[k].From)), PlotHeight);
            dc.DrawRectangle(Band, null, band);
            if (!ShowRunNames)
                continue;
            var room = (k + 1 < bands.Count ? X(f, bands[k + 1].From) : right) - band.X - 6;
            var label = bands[k].Span.Label;
            var name = Draw.Text(label, Draw.Medium, 10.5, Muted, dpi);
            if (name.Width > room && label.IndexOf(" · ", StringComparison.Ordinal) is > 0 and var cut)
                name = Draw.Text(label[..cut], Draw.Medium, 10.5, Muted, dpi);   // "영상 · YOLOv8n" → "영상"
            if (name.Width <= room)
                dc.DrawText(name, new Point(band.X + 1, top - 3 - name.Height));
        }

        // the scale: three hairlines, the lowest one a step darker
        foreach (var v in new[] { f.Min, (f.Min + f.Max) / 2, f.Max })
        {
            var y = Math.Round(Y(f, v)) + 0.5;
            dc.DrawLine(v == f.Min ? Baseline : Grid, new Point(Left, y), new Point(right, y));
            var label = Draw.Text(v.ToString("0", CultureInfo.InvariantCulture) + f.ScaleUnit, Draw.Regular, 10.5, Muted, dpi);
            dc.DrawText(label, new Point(Left - 6 - label.Width, y - label.Height / 2));
        }

        if (ShowTimeAxis)
        {
            var ticks = Ticks(f.Window);
            for (var k = 0; k < ticks.Length; k++)
            {
                var label = Draw.Text(TickText(ticks[k]), Draw.Regular, 10.5, Muted, dpi);
                var x = X(f, f.Now - ticks[k]);
                x = k == 0 ? x : k == ticks.Length - 1 ? x - label.Width : x - label.Width / 2;
                dc.DrawText(label, new Point(x, top + PlotHeight + 3));
            }
        }

        var visible = LowerBound(f.Times, start);
        if (visible >= f.Times.Count)
            return;
        // the lines, from the sample just before the left edge so they enter the chart instead of starting in it
        dc.PushClip(new RectangleGeometry(new Rect(Left, top - 3, right - Left, PlotHeight + 6)));
        foreach (var line in f.Lines)
            DrawLine(dc, f, line, Math.Max(0, visible - 1));
        dc.Pop();

        // the newest value
        var last = f.Times.Count - 1;
        foreach (var line in f.Lines)
            if (last < line.Values.Count && !double.IsNaN(line.Values[last]))
                Dot(dc, line.Color, new Point(X(f, f.Times[last]), Y(f, line.Values[last])));

        // the part before the app was connected stays empty: the upper chart of a card says so where there is room
        var startX = X(f, f.Times[visible]);
        if (ShowRunNames && visible == 0 && f.Times[0] - start > TimeSpan.FromSeconds(20) && startX - Left > 170)
        {
            var note = Draw.Text("연결한 뒤부터 그려요", Draw.Regular, 11.5, Muted, dpi);
            dc.DrawText(note, new Point(Left + (startX - Left - note.Width) / 2, top + (PlotHeight - note.Height) / 2));
        }

        if (HoverTime is { } hover && Index(f, hover) is { } i)
        {
            var x = Math.Round(X(f, f.Times[i])) + 0.5;
            dc.DrawLine(Cross, new Point(x, top), new Point(x, top + PlotHeight));
            foreach (var line in f.Lines)
                if (i < line.Values.Count && !double.IsNaN(line.Values[i]))
                    Dot(dc, line.Color, new Point(x, Y(f, line.Values[i])));
        }
    }

    private void DrawLine(DrawingContext dc, TrendFrame f, TrendLine line, int first)
    {
        var brush = new SolidColorBrush(line.Color);
        brush.Freeze();
        var pen = new Pen(brush, 2) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        var wash = new SolidColorBrush(Color.FromArgb(0x1A, line.Color.R, line.Color.G, line.Color.B));
        wash.Freeze();
        var floor = Y(f, f.Min);
        var points = new List<Point>();
        for (var k = first; k <= f.Times.Count; k++)
        {
            var end = k == f.Times.Count || k >= line.Values.Count || double.IsNaN(line.Values[k])
                      || (points.Count > 0 && f.Times[k] - f.Times[k - 1] > f.Gap);
            if (end && points.Count > 0)
            {
                Stroke(dc, points, pen, line.Wash ? wash : null, floor);
                points.Clear();
            }
            if (k < f.Times.Count && k < line.Values.Count && !double.IsNaN(line.Values[k]))
                points.Add(new Point(X(f, f.Times[k]), Y(f, line.Values[k])));
        }
    }

    private static void Stroke(DrawingContext dc, List<Point> points, Pen pen, Brush? wash, double floor)
    {
        if (wash is not null && points.Count > 1)
        {
            var area = new StreamGeometry();
            using (var g = area.Open())
            {
                g.BeginFigure(new Point(points[0].X, floor), true, true);
                g.PolyLineTo(points, false, true);
                g.LineTo(new Point(points[^1].X, floor), false, true);
            }
            area.Freeze();
            dc.DrawGeometry(wash, null, area);
        }
        var path = new StreamGeometry();
        using (var g = path.Open())
        {
            g.BeginFigure(points[0], false, false);
            if (points.Count == 1)
                g.LineTo(points[0], true, true);
            else
                g.PolyLineTo(points.Skip(1).ToList(), true, true);
        }
        path.Freeze();
        dc.DrawGeometry(null, pen, path);
    }

    private static void Dot(DrawingContext dc, Color color, Point at)
    {
        var fill = new SolidColorBrush(color);
        fill.Freeze();
        dc.DrawEllipse(fill, Ring, at, 4, 4);
    }

    /// <summary>The first sample at or after <paramref name="t"/> (the count when there is none).</summary>
    private static int LowerBound(IReadOnlyList<DateTimeOffset> times, DateTimeOffset t)
    {
        int lo = 0, hi = times.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (times[mid] < t) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    /// <summary>The sample nearest to <paramref name="t"/> among those the chart shows, or null when it shows none.</summary>
    private static int? Index(TrendFrame f, DateTimeOffset t)
    {
        var first = LowerBound(f.Times, f.Now - f.Window);
        if (first >= f.Times.Count)
            return null;
        var i = Math.Clamp(LowerBound(f.Times, t), first, f.Times.Count - 1);
        if (i > first && (t - f.Times[i - 1]).Duration() < (f.Times[i] - t).Duration())
            i--;
        return i;
    }

    private static TimeSpan[] Ticks(TimeSpan window) => window.TotalSeconds switch
    {
        <= 60 => [TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(15), TimeSpan.Zero],
        <= 300 => [TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(1), TimeSpan.Zero],
        _ => [TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(5), TimeSpan.Zero],
    };

    private static string TickText(TimeSpan ago) =>
        ago <= TimeSpan.Zero ? "지금" : ago.TotalSeconds < 60 ? $"{ago.TotalSeconds:0}초 전" : $"{ago.TotalMinutes:0}분 전";

    /// <summary>"지금", "48초 전", "1분 12초 전".</summary>
    public static string Ago(TimeSpan ago)
    {
        var s = (int)Math.Round(Math.Max(0, ago.TotalSeconds));
        return s == 0 ? "지금" : s < 60 ? $"{s}초 전" : s % 60 == 0 ? $"{s / 60}분 전" : $"{s / 60}분 {s % 60}초 전";
    }

    // ---------------------------------------------------------------- pointer

    private Popup? _tip;
    private StackPanel? _rows;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (Frame is not { } f || f.Times.Count == 0)
            return;
        var p = e.GetPosition(this);
        var width = ActualWidth - Left - Right;
        if (p.X < Left - 6 || p.X > ActualWidth - Right + 6 || width <= 0)
        {
            Leave();
            return;
        }
        var t = f.Now - f.Window + TimeSpan.FromSeconds((p.X - Left) / width * f.Window.TotalSeconds);
        if (Index(f, t) is not { } i)
        {
            Leave();
            return;
        }
        HoverTime = f.Times[i];
        ShowTip(f, i);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        Leave();
    }

    private void Leave()
    {
        if (_tip is { IsOpen: true })
            _tip.IsOpen = false;
        if (HoverTime is not null)
            HoverTime = null;
    }

    /// <summary>A new frame every two seconds: the box under a resting pointer follows the sample it points at.</summary>
    private void RefreshTip()
    {
        if (_tip is not { IsOpen: true } || Frame is not { } f || HoverTime is not { } h || Index(f, h) is not { } i)
            return;
        ShowTip(f, i);
    }

    private void ShowTip(TrendFrame f, int i)
    {
        if (_tip is null)
        {
            _rows = new StackPanel();
            var box = new Border
            {
                Background = Brushes.White, BorderBrush = BoxEdge, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 7, 10, 8), Margin = new Thickness(0, 0, 10, 12),
                Child = _rows, IsHitTestVisible = false,
                Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Direction = 270, Opacity = 0.14, Color = Color.FromRgb(17, 24, 39) },
            };
            TextElement.SetFontFamily(box, (FontFamily)FindResource("Font.Ui"));
            TextElement.SetFontSize(box, 12);
            _tip = new Popup
            {
                Child = box, AllowsTransparency = true, Placement = PlacementMode.Relative, PlacementTarget = this,
                IsHitTestVisible = false, Focusable = false, StaysOpen = true,
            };
        }
        var rows = _rows!;
        rows.Children.Clear();
        rows.Children.Add(new TextBlock { Text = Ago(f.Now - f.Times[i]), Foreground = Muted, Margin = new Thickness(0, 0, 0, 3) });
        foreach (var row in f.Tip(i))
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
            var key = new Border { Width = 12, Height = 2, CornerRadius = new CornerRadius(1), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            if (row.Color is { } c)
            {
                var b = new SolidColorBrush(c);
                b.Freeze();
                key.Background = b;
            }
            line.Children.Add(key);
            line.Children.Add(new TextBlock { Text = row.Value, FontWeight = FontWeights.SemiBold, Foreground = Heading, MinWidth = 52 });
            line.Children.Add(new TextBlock { Text = row.Name, Foreground = Secondary, Margin = new Thickness(6, 0, 0, 0) });
            rows.Children.Add(line);
        }
        if (f.RunAt(i) is { Length: > 0 } run)
            rows.Children.Add(new Border
            {
                BorderBrush = BoxEdge, BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(0, 5, 0, 0), Padding = new Thickness(0, 4, 0, 0),
                Child = new TextBlock { Text = run + " 실행 중", Foreground = Muted },
            });
        var boxElement = (FrameworkElement)_tip.Child;
        boxElement.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var x = X(f, f.Times[i]);
        var left = x + 12;
        if (left + boxElement.DesiredSize.Width > ActualWidth + 40)
            left = x - 12 - boxElement.DesiredSize.Width + 10; // the box keeps a 10 px margin on its right for the shadow
        _tip.HorizontalOffset = left;
        _tip.VerticalOffset = Top - 6;
        _tip.IsOpen = true;
    }
}
