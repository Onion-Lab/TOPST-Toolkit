using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TopstToolkit.App.Controls;

/// <summary>A box on a picture, in the picture's own pixels.</summary>
/// <param name="Dashed">Drawn with a dashed line (for example the original network's box under the board network's).</param>
public sealed record BoxOverlay(double X1, double Y1, double X2, double Y2, string Label, Color Color, bool Dashed = false);

/// <summary>
/// Shows a picture with the detected boxes drawn by the app (design §5.5): Korean labels, any size, zoom with the
/// wheel and drag to move. The toolkit's own PNG uses a small bitmap font and English only.
/// </summary>
public sealed class DetectionView : FrameworkElement
{
    private static readonly Typeface LabelFace = new(new FontFamily(new Uri("pack://application:,,,/"), "./Assets/Fonts/#Pretendard"),
        FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    private static readonly Brush Backdrop = Frozen(Color.FromRgb(0xF3, 0xF4, 0xF6));

    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(BitmapSource), typeof(DetectionView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
            (d, _) => ((DetectionView)d).ResetView()));

    public static readonly DependencyProperty BoxesProperty = DependencyProperty.Register(
        nameof(Boxes), typeof(IReadOnlyList<BoxOverlay>), typeof(DetectionView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CaptionProperty = DependencyProperty.Register(
        nameof(Caption), typeof(string), typeof(DetectionView),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>1 = the whole picture fits; larger values magnify.</summary>
    public static readonly DependencyProperty ZoomProperty = DependencyProperty.Register(
        nameof(Zoom), typeof(double), typeof(DetectionView),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, _) => ((DetectionView)d).ClampOffset(), (_, v) => Math.Clamp((double)v, 1.0, 8.0)));

    private Vector _offset; // how far the picture is moved from centred, in screen pixels
    private Point? _dragFrom;

    public DetectionView()
    {
        ClipToBounds = true;
        Focusable = false;
    }

    public BitmapSource? Source
    {
        get => (BitmapSource?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public IReadOnlyList<BoxOverlay>? Boxes
    {
        get => (IReadOnlyList<BoxOverlay>?)GetValue(BoxesProperty);
        set => SetValue(BoxesProperty, value);
    }

    /// <summary>A tag in the top-left corner (the class name of a classification result).</summary>
    public string Caption
    {
        get => (string)GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }

    public double Zoom
    {
        get => (double)GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    private void ResetView()
    {
        _offset = default;
        if (Zoom != 1.0)
            SetCurrentValue(ZoomProperty, 1.0);
    }

    /// <summary>The largest size with the picture's proportions that fits the offered space: the control is the picture.</summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        if (Source is not { } image || image.PixelWidth == 0 || image.PixelHeight == 0)
            return new Size(0, 0);
        var w = double.IsInfinity(availableSize.Width) ? image.PixelWidth : availableSize.Width;
        var h = double.IsInfinity(availableSize.Height) ? image.PixelHeight : availableSize.Height;
        var scale = Math.Min(w / image.PixelWidth, h / image.PixelHeight);
        return new Size(image.PixelWidth * scale, image.PixelHeight * scale);
    }

    /// <summary>Where the picture is drawn, and how many screen pixels one picture pixel takes.</summary>
    private Rect PictureRect(out double scale)
    {
        scale = 1;
        if (Source is not { } image || ActualWidth <= 0 || ActualHeight <= 0)
            return Rect.Empty;
        scale = Math.Min(ActualWidth / image.PixelWidth, ActualHeight / image.PixelHeight) * Zoom;
        var w = image.PixelWidth * scale;
        var h = image.PixelHeight * scale;
        return new Rect((ActualWidth - w) / 2 + _offset.X, (ActualHeight - h) / 2 + _offset.Y, w, h);
    }

    private void ClampOffset()
    {
        if (Source is not { } image || ActualWidth <= 0)
            return;
        var scale = Math.Min(ActualWidth / image.PixelWidth, ActualHeight / image.PixelHeight) * Zoom;
        var maxX = Math.Max(0, (image.PixelWidth * scale - ActualWidth) / 2);
        var maxY = Math.Max(0, (image.PixelHeight * scale - ActualHeight) / 2);
        _offset = new Vector(Math.Clamp(_offset.X, -maxX, maxX), Math.Clamp(_offset.Y, -maxY, maxY));
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRoundedRectangle(Backdrop, null, new Rect(0, 0, ActualWidth, ActualHeight), 8, 8);
        var rect = PictureRect(out var scale);
        if (rect.IsEmpty || Source is null)
            return;

        dc.PushClip(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight), 8, 8));
        dc.DrawImage(Source, rect);

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (var box in Boxes ?? [])
        {
            var brush = Frozen(box.Color);
            var pen = new Pen(brush, 2) { DashStyle = box.Dashed ? new DashStyle([4, 3], 0) : DashStyles.Solid };
            var r = new Rect(
                new Point(rect.X + box.X1 * scale, rect.Y + box.Y1 * scale),
                new Point(rect.X + box.X2 * scale, rect.Y + box.Y2 * scale));
            dc.DrawRoundedRectangle(null, pen, r, 2, 2);
            if (box.Label.Length == 0)
                continue;
            var text = new FormattedText(box.Label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, LabelFace, 11, Brushes.White, dpi);
            var tag = new Rect(r.X - 1, r.Y - 19, text.Width + 12, 18);
            if (tag.Y < 0) tag.Y = r.Y + 1;                       // no room above: put the tag inside the box
            if (tag.Right > ActualWidth) tag.X = ActualWidth - tag.Width;
            if (tag.X < 0) tag.X = 0;
            if (box.Dashed)
            {
                dc.DrawRoundedRectangle(Brushes.White, new Pen(brush, 1), tag, 3, 3);
                text.SetForegroundBrush(brush);
            }
            else
            {
                dc.DrawRoundedRectangle(brush, null, tag, 3, 3);
            }
            dc.DrawText(text, new Point(tag.X + 6, tag.Y + (tag.Height - text.Height) / 2));
        }

        if (!string.IsNullOrEmpty(Caption))
        {
            var text = new FormattedText(Caption, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, LabelFace, 13, Brushes.White, dpi);
            var tag = new Rect(Math.Max(rect.X, 0) + 10, Math.Max(rect.Y, 0) + 10, text.Width + 20, 28);
            dc.DrawRoundedRectangle(Frozen(Color.FromArgb(0xE6, 0x21, 0x25, 0x29)), null, tag, 6, 6);
            dc.DrawText(text, new Point(tag.X + 10, tag.Y + (tag.Height - text.Height) / 2));
        }
        dc.Pop();
    }

    // ---- wheel to zoom at the pointer, drag to move
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (Source is null)
            return;
        var before = PictureRect(out var oldScale);
        var at = e.GetPosition(this);
        var pictureX = (at.X - before.X) / oldScale;
        var pictureY = (at.Y - before.Y) / oldScale;
        SetCurrentValue(ZoomProperty, Zoom * (e.Delta > 0 ? 1.25 : 0.8));
        var after = PictureRect(out var newScale);
        // keep the picture point under the pointer where it was
        _offset += new Vector(at.X - (after.X + pictureX * newScale), at.Y - (after.Y + pictureY * newScale));
        if (Zoom <= 1.0)
            _offset = default;
        ClampOffset();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (Zoom <= 1.0)
            return;
        _dragFrom = e.GetPosition(this);
        CaptureMouse();
        Cursor = Cursors.SizeAll;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragFrom is not { } from || e.LeftButton != MouseButtonState.Pressed)
            return;
        var now = e.GetPosition(this);
        _offset += now - from;
        _dragFrom = now;
        ClampOffset();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _dragFrom = null;
        ReleaseMouseCapture();
        Cursor = null;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        ClampOffset();
    }

    /// <summary>The picture at its own size with the boxes drawn in, for saving (labels sized to the picture).</summary>
    public static BitmapSource Render(BitmapSource source, IReadOnlyList<BoxOverlay> boxes, double pixelsPerDip = 1.0)
    {
        var w = source.PixelWidth;
        var h = source.PixelHeight;
        var unit = Math.Max(1.0, Math.Max(w, h) / 900.0);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(source, new Rect(0, 0, w, h));
            foreach (var box in boxes)
            {
                var brush = Frozen(box.Color);
                var r = new Rect(new Point(box.X1, box.Y1), new Point(box.X2, box.Y2));
                dc.DrawRectangle(null, new Pen(brush, 2 * unit), r);
                if (box.Label.Length == 0)
                    continue;
                var text = new FormattedText(box.Label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, LabelFace, 12 * unit, Brushes.White, pixelsPerDip);
                var tag = new Rect(r.X - unit, r.Y - text.Height - 4 * unit, text.Width + 10 * unit, text.Height + 4 * unit);
                if (tag.Y < 0) tag.Y = r.Y;
                if (tag.Right > w) tag.X = w - tag.Width;
                if (tag.X < 0) tag.X = 0;
                dc.DrawRectangle(brush, null, tag);
                dc.DrawText(text, new Point(tag.X + 5 * unit, tag.Y + 2 * unit));
            }
        }
        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
