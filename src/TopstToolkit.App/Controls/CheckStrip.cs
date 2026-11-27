using System.Globalization;
using System.Windows;
using System.Windows.Media;
using TopstToolkit.App.ViewModels;

namespace TopstToolkit.App.Controls;

/// <summary>Fonts, brushes and text for the controls that draw themselves (the check strip, the model graph).</summary>
internal static class Draw
{
    private static readonly FontFamily Ui = new(new Uri("pack://application:,,,/"), "./Assets/Fonts/#Pretendard");
    private static readonly FontFamily MonoFamily = new(new Uri("pack://application:,,,/"), "./Assets/Fonts/#D2Coding");

    public static readonly Typeface Regular = new(Ui, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    public static readonly Typeface Medium = new(Ui, FontStyles.Normal, FontWeights.Medium, FontStretches.Normal);
    public static readonly Typeface SemiBold = new(Ui, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    public static readonly Typeface Mono = new(MonoFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    public static Brush Brush(string color)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        b.Freeze();
        return b;
    }

    public static Pen Pen(string color, double thickness, params double[] dashes)
    {
        var p = new Pen(Brush(color), thickness);
        if (dashes.Length > 0)
            p.DashStyle = new DashStyle(dashes.Select(d => d / thickness), 0);
        p.Freeze();
        return p;
    }

    public static FormattedText Text(string text, Typeface face, double size, Brush brush, double dpi, double maxWidth = 0, double lineHeight = 0)
    {
        var t = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, brush, dpi);
        if (maxWidth > 0)
        {
            t.MaxTextWidth = maxWidth;
            t.Trimming = TextTrimming.None;
        }
        if (lineHeight > 0)
            t.LineHeight = lineHeight;
        return t;
    }
}

/// <summary>
/// The model as one strip from input to output, on the check card of step 1 (design §5.7): the part the NPU runs,
/// where the model is cut, the tail the board computes instead, and — as red marks — where the operators sit that the
/// NPU cannot run.
/// </summary>
public sealed class CheckStrip : FrameworkElement
{
    private static readonly Brush Sunken = Draw.Brush("#F3F4F6");
    private static readonly Brush Stripe = Draw.Brush("#E9EBEF");
    private static readonly Brush OkBg = Draw.Brush("#E7F5EE");
    private static readonly Brush Ok = Draw.Brush("#18794E");
    private static readonly Brush Bad = Draw.Brush("#C8312A");
    private static readonly Brush BadBg = Draw.Brush("#FDECEA");
    private static readonly Brush BadStripe = Draw.Brush("#F8DAD6");
    private static readonly Brush Muted = Draw.Brush("#6B7280");
    private static readonly Brush Secondary = Draw.Brush("#636363");
    private static readonly Brush Primary = Draw.Brush("#3B5998");
    private static readonly Brush Amber = Draw.Brush("#D97706");
    private static readonly Pen CutPen = Draw.Pen("#3B5998", 2, 5, 4);
    private static readonly Pen UndecidedPen = Draw.Pen("#D97706", 2, 5, 4);
    private static readonly Pen CheckPen = Draw.Pen("#18794E", 1.8);

    private const double Top = 22;
    private const double BarHeight = 44;
    private const double Cap = 46;

    public static readonly DependencyProperty ModeProperty = Register(nameof(Mode), typeof(CheckStripMode), CheckStripMode.None, measure: true);
    public static readonly DependencyProperty RunTextProperty = Register(nameof(RunText), typeof(string), "");
    public static readonly DependencyProperty TailTextProperty = Register(nameof(TailText), typeof(string), "");
    public static readonly DependencyProperty CutTextProperty = Register(nameof(CutText), typeof(string), "");
    public static readonly DependencyProperty TicksProperty = Register(nameof(Ticks), typeof(IReadOnlyList<double>), null);

    private static DependencyProperty Register(string name, Type type, object? value, bool measure = false) =>
        DependencyProperty.Register(name, type, typeof(CheckStrip), new FrameworkPropertyMetadata(value,
            measure ? FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender : FrameworkPropertyMetadataOptions.AffectsRender));

    public CheckStripMode Mode { get => (CheckStripMode)GetValue(ModeProperty); set => SetValue(ModeProperty, value); }
    public string RunText { get => (string)GetValue(RunTextProperty); set => SetValue(RunTextProperty, value); }
    public string TailText { get => (string)GetValue(TailTextProperty); set => SetValue(TailTextProperty, value); }
    public string CutText { get => (string)GetValue(CutTextProperty); set => SetValue(CutTextProperty, value); }
    public IReadOnlyList<double>? Ticks { get => (IReadOnlyList<double>?)GetValue(TicksProperty); set => SetValue(TicksProperty, value); }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width, Mode == CheckStripMode.None ? 0 : Top + BarHeight + 6);

    protected override void OnRender(DrawingContext dc)
    {
        if (Mode == CheckStripMode.None || ActualWidth < 2 * Cap + 80)
            return;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var width = ActualWidth;
        var bar = new Rect(0, Top, width, BarHeight);
        var middle = width - 2 * Cap;
        var tailWidth = Mode switch
        {
            CheckStripMode.Clean => 0,
            CheckStripMode.Blocked => Math.Min(300, middle * 0.55),
            _ => Math.Min(236, middle * 0.45),
        };
        var runRect = new Rect(Cap, Top, middle - tailWidth, BarHeight);
        var tailRect = new Rect(runRect.Right, Top, tailWidth, BarHeight);

        dc.PushClip(new RectangleGeometry(bar, 10, 10));
        dc.DrawRectangle(Sunken, null, bar);
        Centered(dc, "입력", new Rect(0, Top, Cap, BarHeight), Muted, dpi);
        Centered(dc, "출력", new Rect(width - Cap, Top, Cap, BarHeight), Muted, dpi);

        // the part the NPU runs
        dc.DrawRectangle(OkBg, null, runRect);
        var mark = new Point(runRect.Left + 14, Top + BarHeight / 2);
        dc.DrawLine(CheckPen, new Point(mark.X, mark.Y), new Point(mark.X + 4, mark.Y + 4));
        dc.DrawLine(CheckPen, new Point(mark.X + 4, mark.Y + 4), new Point(mark.X + 11, mark.Y - 4));
        var run = Draw.Text(RunText, Draw.SemiBold, 13, Ok, dpi);
        run.MaxTextWidth = Math.Max(20, runRect.Width - 44);
        run.MaxLineCount = 1;
        run.Trimming = TextTrimming.CharacterEllipsis;
        dc.DrawText(run, new Point(runRect.Left + 34, Top + (BarHeight - run.Height) / 2));

        // the tail
        if (tailWidth > 0)
        {
            var blocked = Mode == CheckStripMode.Blocked;
            dc.PushClip(new RectangleGeometry(tailRect));
            dc.DrawRectangle(blocked ? BadBg : Sunken, null, tailRect);
            var stripe = new Pen(blocked ? BadStripe : Stripe, 6);
            for (var x = tailRect.Left - BarHeight; x < tailRect.Right; x += 16)
                dc.DrawLine(stripe, new Point(x, tailRect.Bottom), new Point(x + BarHeight, tailRect.Top));
            var tail = Draw.Text(TailText, Draw.SemiBold, 13, blocked ? Bad : Secondary, dpi);
            tail.MaxTextWidth = Math.Max(20, tailRect.Width - 28);
            tail.MaxLineCount = 1;
            tail.Trimming = TextTrimming.CharacterEllipsis;
            dc.DrawText(tail, new Point(tailRect.Left + 14, Top + (BarHeight - tail.Height) / 2));

            // where the operators sit that cannot run
            var from = Math.Min(tailRect.Left + 14 + tail.WidthIncludingTrailingWhitespace + 14, tailRect.Right - 40);
            var span = tailRect.Right - 12 - from;
            foreach (var t in Ticks ?? [])
            {
                var x = from + Math.Clamp(t, 0, 1) * span;
                dc.DrawRoundedRectangle(Bad, null, new Rect(x - 2, Top + 6, 4, BarHeight - 12), 2, 2);
            }
            dc.Pop();
        }
        dc.Pop();

        // the cut
        if (Mode is CheckStripMode.Cut or CheckStripMode.Undecided && CutText.Length > 0)
        {
            var decided = Mode == CheckStripMode.Cut;
            var x = tailRect.Left;
            dc.DrawLine(decided ? CutPen : UndecidedPen, new Point(x, 10), new Point(x, Top + BarHeight + 5));
            var label = Draw.Text("✂ " + CutText, Draw.SemiBold, 11, Brushes.White, dpi);
            var pill = new Rect(x - (label.Width + 16) / 2, 0, label.Width + 16, 18);
            dc.DrawRoundedRectangle(decided ? Primary : Amber, null, pill, 9, 9);
            dc.DrawText(label, new Point(pill.Left + 8, pill.Top + (18 - label.Height) / 2));
        }
    }

    private static void Centered(DrawingContext dc, string text, Rect rect, Brush brush, double dpi)
    {
        var t = Draw.Text(text, Draw.SemiBold, 11, brush, dpi);
        dc.DrawText(t, new Point(rect.Left + (rect.Width - t.Width) / 2, rect.Top + (rect.Height - t.Height) / 2));
    }
}
