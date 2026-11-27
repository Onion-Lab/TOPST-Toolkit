using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using TopstToolkit.Core.Projects;

namespace TopstToolkit.App.Controls;

/// <summary>
/// Draws the end of a model as a graph (design §5.7, docs/mockups/mockup-check-cut.html): operators as boxes, red for
/// what the NPU cannot run, amber dashes for what is stuck behind it, blue for a cut, and a grey zone around the part
/// a cut removes. A click on an operator goes to <see cref="NodeCommand"/> with the operator's id; a click on empty
/// space to <see cref="BackgroundCommand"/>. The geometry comes from <see cref="GraphLayout"/>, the states from
/// <see cref="CutEffect"/>; this control only draws and reports clicks.
/// </summary>
public sealed class GraphView : FrameworkElement
{
    private static readonly Brush Heading = Draw.Brush("#111111");
    private static readonly Brush Muted = Draw.Brush("#6B7280");
    private static readonly Brush Secondary = Draw.Brush("#636363");
    private static readonly Brush Primary = Draw.Brush("#3B5998");
    private static readonly Brush PrimaryTint = Draw.Brush("#EFF2F7");
    private static readonly Brush BadText = Draw.Brush("#C8312A");
    private static readonly Brush BadBg = Draw.Brush("#FDECEA");
    private static readonly Brush WarnBg = Draw.Brush("#FFF4DC");
    private static readonly Brush WarnText = Draw.Brush("#9A5B00");
    private static readonly Brush OkBg = Draw.Brush("#E7F5EE");
    private static readonly Brush OkText = Draw.Brush("#18794E");
    private static readonly Brush FoldBg = Draw.Brush("#F7F8FA");
    private static readonly Brush Night = Draw.Brush("#212529");
    private static readonly Brush NightDetail = Draw.Brush("#AEB4BE");
    private static readonly Brush ZoneFill = Draw.Brush("#0E636B7A");
    private static readonly Brush Shadow = Draw.Brush("#1F111827");
    private static readonly Brush Text = Draw.Brush("#222222");

    private static readonly Pen NodePen = Draw.Pen("#C4C8CF", 1.5);
    private static readonly Pen NodeDashPen = Draw.Pen("#C4C8CF", 1.5, 4, 3);
    private static readonly Pen HoverPen = Draw.Pen("#3B5998", 1.5);
    private static readonly Pen PickedPen = Draw.Pen("#C4C8CF", 3);
    private static readonly Pen BadPen = Draw.Pen("#C8312A", 1.5);
    private static readonly Pen BadDashPen = Draw.Pen("#C8312A", 1.5, 4, 3);
    private static readonly Pen BadPickedPen = Draw.Pen("#C8312A", 3);
    private static readonly Pen BlockedPen = Draw.Pen("#D97706", 1.5, 4, 3);
    private static readonly Pen BlockedPickedPen = Draw.Pen("#D97706", 3, 4, 3);
    private static readonly Pen CutPen = Draw.Pen("#3B5998", 2.5);
    private static readonly Pen FoldPen = Draw.Pen("#C4C8CF", 1.5, 5, 4);
    private static readonly Pen EdgePen = Draw.Pen("#C4C8CF", 1.5);
    private static readonly Pen FadedEdgePen = Draw.Pen("#DADDE3", 1.5, 4, 3);
    private static readonly Pen CutEdgePen = Draw.Pen("#3B5998", 1.5, 3, 3);
    private static readonly Pen ZonePen = Draw.Pen("#B9BFC9", 1.2, 6, 5);
    private static readonly Pen NotePen = Draw.Pen("#DDDDDD", 1);
    private static readonly Pen OkNotePen = Draw.Pen("#9FD3B9", 1);

    private const double Faded = 0.56;
    private int? _hover;
    private int? _pressed;

    public static readonly DependencyProperty LayoutProperty = Register(nameof(Layout), typeof(GraphLayout), null, measure: true);
    public static readonly DependencyProperty CutEffectProperty = Register(nameof(CutEffect), typeof(CutEffect), null);
    public static readonly DependencyProperty PickedProperty = Register(nameof(Picked), typeof(int?), null);
    public static readonly DependencyProperty LockedProperty = Register(nameof(Locked), typeof(bool), false);
    public static readonly DependencyProperty ZoneTitleProperty = Register(nameof(ZoneTitle), typeof(string), "");
    public static readonly DependencyProperty ZoneBodyProperty = Register(nameof(ZoneBody), typeof(string), "");
    public static readonly DependencyProperty ZoneKindProperty = Register(nameof(ZoneKind), typeof(ChipKind), ChipKind.Muted);
    public static readonly DependencyProperty PopTitleProperty = Register(nameof(PopTitle), typeof(string), "");
    public static readonly DependencyProperty PopNameProperty = Register(nameof(PopName), typeof(string), "");
    public static readonly DependencyProperty PopBodyProperty = Register(nameof(PopBody), typeof(string), "");
    public static readonly DependencyProperty PopKindProperty = Register(nameof(PopKind), typeof(ChipKind), ChipKind.Bad);
    public static readonly DependencyProperty NodeCommandProperty = Register(nameof(NodeCommand), typeof(ICommand), null);
    public static readonly DependencyProperty BackgroundCommandProperty = Register(nameof(BackgroundCommand), typeof(ICommand), null);

    private static DependencyProperty Register(string name, Type type, object? value, bool measure = false) =>
        DependencyProperty.Register(name, type, typeof(GraphView), new FrameworkPropertyMetadata(value,
            measure ? FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender : FrameworkPropertyMetadataOptions.AffectsRender));

    public GraphLayout? Layout { get => (GraphLayout?)GetValue(LayoutProperty); set => SetValue(LayoutProperty, value); }
    public CutEffect? CutEffect { get => (CutEffect?)GetValue(CutEffectProperty); set => SetValue(CutEffectProperty, value); }
    public int? Picked { get => (int?)GetValue(PickedProperty); set => SetValue(PickedProperty, value); }
    public bool Locked { get => (bool)GetValue(LockedProperty); set => SetValue(LockedProperty, value); }
    public string ZoneTitle { get => (string)GetValue(ZoneTitleProperty); set => SetValue(ZoneTitleProperty, value); }
    public string ZoneBody { get => (string)GetValue(ZoneBodyProperty); set => SetValue(ZoneBodyProperty, value); }
    public ChipKind ZoneKind { get => (ChipKind)GetValue(ZoneKindProperty); set => SetValue(ZoneKindProperty, value); }
    public string PopTitle { get => (string)GetValue(PopTitleProperty); set => SetValue(PopTitleProperty, value); }
    public string PopName { get => (string)GetValue(PopNameProperty); set => SetValue(PopNameProperty, value); }
    public string PopBody { get => (string)GetValue(PopBodyProperty); set => SetValue(PopBodyProperty, value); }
    public ChipKind PopKind { get => (ChipKind)GetValue(PopKindProperty); set => SetValue(PopKindProperty, value); }
    public ICommand? NodeCommand { get => (ICommand?)GetValue(NodeCommandProperty); set => SetValue(NodeCommandProperty, value); }
    public ICommand? BackgroundCommand { get => (ICommand?)GetValue(BackgroundCommandProperty); set => SetValue(BackgroundCommandProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => Layout is { } layout ? new Size(layout.Width, layout.Height) : new Size(0, 0);

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    /// <summary>The drawing as one element named "모델 그래프"; the operators are reached through the lists beside it.</summary>
    private sealed class Peer(GraphView owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Custom;
        protected override string GetClassNameCore() => nameof(GraphView);
        protected override string GetNameCore() => "모델 그래프";
        protected override bool IsControlElementCore() => true;
        protected override bool IsContentElementCore() => true;
    }

    // ---------------------------------------------------------------- states

    private CutState StateOf(PlacedBox box)
    {
        var effect = CutEffect;
        if (effect is null)
            return CutState.Keep;
        switch (box.Kind)
        {
            case GraphBoxKind.Operator:
                return effect.StateOf(box.Id);
            case GraphBoxKind.Output when box.Output?.Node is { } producer && effect.AwayOps > 0:
                // an output of the original model is gone when what produced it is
                return effect.StateOf(producer) is CutState.Gone or CutState.Dropped ? CutState.Gone : CutState.Keep;
            default:
                return CutState.Keep;
        }
    }

    private static bool Clickable(PlacedBox box, bool locked) =>
        box is { Kind: GraphBoxKind.Operator, Node: not null } && !(locked && box.Node.State == "ok");

    private PlacedBox? BoxAt(Point p)
    {
        if (Layout is not { } layout)
            return null;
        foreach (var box in layout.Boxes)
            if (box.Kind == GraphBoxKind.Operator && p.X >= box.X && p.X <= box.X + box.Width && p.Y >= box.Y && p.Y <= box.Bottom)
                return box;
        return null;
    }

    // ---------------------------------------------------------------- mouse

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var box = BoxAt(e.GetPosition(this));
        var id = box is not null && Clickable(box, Locked) ? box.Id : (int?)null;
        if (id == _hover)
            return;
        _hover = id;
        Cursor = id is null ? null : Cursors.Hand;
        ToolTip = box?.Node is not { } node || id is null ? null
            : node.Name + (node.State != "ok" ? " — 누르면 이유를 보여 줍니다"
                : StateOf(box) == CutState.Cut ? " — 누르면 취소합니다" : " — 누르면 여기서 자릅니다");
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (_hover is null)
            return;
        _hover = null;
        Cursor = null;
        ToolTip = null;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Press(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        Release(e.GetPosition(this));
        e.Handled = true;
    }

    /// <summary>The id of the operator at a point of the drawing that answers a click, or -1.</summary>
    private int HitId(Point p) => BoxAt(p) is { } box && Clickable(box, Locked) ? box.Id : -1;

    private void Press(Point p) => _pressed = HitId(p);

    private void Release(Point p)
    {
        var id = HitId(p);
        if (_pressed == id)
        {
            if (id >= 0)
                Run(NodeCommand, id);
            else
                Run(BackgroundCommand, null);
        }
        _pressed = null;
    }

    /// <summary>
    /// What a click at this point of the drawing does (drawing units, as <see cref="GraphLayout"/> places the boxes).
    /// For the capture tool, which has no mouse: the same hit test and the same commands as a real click.
    /// </summary>
    internal void ClickAt(Point p)
    {
        Press(p);
        Release(p);
    }

    private static void Run(ICommand? command, object? parameter)
    {
        if (command?.CanExecute(parameter) == true)
            command.Execute(parameter);
    }

    // ---------------------------------------------------------------- drawing

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize)); // clicks on empty space arrive too
        if (Layout is not { } layout)
            return;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var states = layout.Boxes.ToDictionary(b => b.Id, StateOf);
        var locked = Locked;

        // the part a cut removes, and the note that says what computes it instead
        Rect? note = null;
        FormattedText? noteTitle = null, noteBody = null;
        var gone = layout.Boxes.Where(b => b.Kind != GraphBoxKind.Fold && states[b.Id] == CutState.Gone).ToList();
        if (gone.Count > 0)
        {
            var left = gone.Min(b => b.X) - 10;
            var top = gone.Min(b => b.Y) - 4;
            var zone = new Rect(left, top, gone.Max(b => b.X + b.Width) + 10 - left, gone.Max(b => b.Bottom) + 10 - top);
            if (ZoneTitle.Length > 0)
            {
                noteTitle = Draw.Text(ZoneTitle, Draw.SemiBold, 12, ZoneKind == ChipKind.Ok ? OkText : Heading, dpi, NoteWidth - 20);
                noteBody = ZoneBody.Length > 0 ? Draw.Text(ZoneBody, Draw.Regular, 11.5, Secondary, dpi, NoteWidth - 20, 17) : null;
                note = PlaceNote(layout, zone, 8 + noteTitle.Height + (noteBody?.Height ?? 0) + 8);
                if (note is { } placed && placed.Right + 8 > zone.Right)
                    zone = new Rect(zone.Left, zone.Top, placed.Right + 8 - zone.Left, zone.Height);
            }
            dc.DrawRoundedRectangle(ZoneFill, ZonePen, zone, 14, 14);
        }

        DrawEdges(dc, layout, states);
        foreach (var box in layout.Boxes)
            DrawBox(dc, box, states[box.Id], locked, dpi);

        if (note is { } rect && noteTitle is not null)
        {
            var ok = ZoneKind == ChipKind.Ok;
            dc.DrawRoundedRectangle(ok ? OkBg : Brushes.White, ok ? OkNotePen : NotePen, rect, 8, 8);
            dc.DrawText(noteTitle, new Point(rect.Left + 10, rect.Top + 8));
            if (noteBody is not null)
                dc.DrawText(noteBody, new Point(rect.Left + 10, rect.Top + 8 + noteTitle.Height));
        }
        if (PopTitle.Length > 0 && Picked is { } picked && layout.BoxOf(picked) is { } target)
            DrawPopover(dc, layout, target, note, dpi);
    }

    private static void DrawEdges(DrawingContext dc, GraphLayout layout, Dictionary<int, CutState> states)
    {
        var normal = new StreamGeometry();
        var faded = new StreamGeometry();
        var cut = new StreamGeometry();
        using (var n = normal.Open())
        using (var f = faded.Open())
        using (var c = cut.Open())
        {
            foreach (var edge in layout.Edges)
            {
                var from = edge.From == GraphLayout.FoldId ? CutState.Keep : states.GetValueOrDefault(edge.From, CutState.Keep);
                var to = states.GetValueOrDefault(edge.To, CutState.Keep);
                var target = from == CutState.Cut ? c : from == CutState.Keep && to is CutState.Keep or CutState.Cut ? n : f;
                var points = edge.Points;
                target.BeginFigure(new Point(points[0].X, points[0].Y), false, false);
                for (var i = 1; i < points.Count; i++)
                {
                    var (x0, y0) = points[i - 1];
                    var (x1, y1) = points[i];
                    if (i % 2 == 1)
                    {
                        // between two rows: leave straight down, arrive straight down
                        var middle = (y0 + y1) / 2;
                        target.BezierTo(new Point(x0, middle), new Point(x1, middle), new Point(x1, y1), true, true);
                    }
                    else
                    {
                        target.LineTo(new Point(x1, y1), true, true); // down a lane
                    }
                }
            }
        }
        normal.Freeze();
        faded.Freeze();
        cut.Freeze();
        dc.DrawGeometry(null, FadedEdgePen, faded);
        dc.DrawGeometry(null, EdgePen, normal);
        dc.DrawGeometry(null, CutEdgePen, cut);
    }

    private void DrawBox(DrawingContext dc, PlacedBox box, CutState state, bool locked, double dpi)
    {
        var rect = new Rect(box.X, box.Y, box.Width, box.Height);
        var away = state is CutState.Gone or CutState.Dropped;
        Brush fill = Brushes.White, title = Heading, detail = Muted;
        var pen = NodePen;
        var face = Draw.SemiBold;
        var opacity = away ? Faded : 1.0;
        var picked = Picked == box.Id;

        switch (box.Kind)
        {
            case GraphBoxKind.Fold:
                (fill, pen, title, face) = (FoldBg, FoldPen, Secondary, Draw.Medium);
                break;
            case GraphBoxKind.Output:
                (fill, pen, title, detail) = (Night, null!, Brushes.White, NightDetail);
                break;
            default:
                var node = box.Node!;
                if (state == CutState.Cut)
                    (fill, pen, title) = (PrimaryTint, CutPen, Primary);
                else if (node.State == "bad")
                {
                    (fill, title) = (BadBg, BadText);
                    pen = picked ? BadPickedPen : away ? BadDashPen : BadPen;
                    opacity = away ? 0.9 : 1.0;
                }
                else if (node.State == "blocked")
                    (fill, pen) = (WarnBg, picked ? BlockedPickedPen : BlockedPen);
                else if (away)
                    pen = NodeDashPen;
                else if (_hover == box.Id && !locked)
                    pen = HoverPen;
                else if (picked)
                    pen = PickedPen;
                break;
        }

        dc.PushOpacity(opacity);
        dc.DrawRoundedRectangle(fill, pen, rect, 10, 10);
        var t = Draw.Text(box.Title, face, 12.5, title, dpi);
        t.MaxTextWidth = Math.Max(10, box.Width - 8);
        t.MaxLineCount = 1;
        t.Trimming = TextTrimming.CharacterEllipsis;
        dc.DrawText(t, new Point(box.X + (box.Width - t.Width) / 2, box.Y + 4.5));
        if (box.Detail.Length > 0)
        {
            var d = Draw.Text(box.Detail, Draw.Regular, 10.5, detail, dpi);
            d.MaxTextWidth = Math.Max(10, box.Width - 8);
            d.MaxLineCount = 1;
            d.Trimming = TextTrimming.CharacterEllipsis;
            dc.DrawText(d, new Point(box.X + (box.Width - d.Width) / 2, box.Y + 21.5));
        }
        dc.Pop();

        if (box.Kind != GraphBoxKind.Operator)
            return;
        // an input that comes from the folded front part although the box sits further down
        if (box.Node is { FromFold: true } && box.Row > 1)
            dc.DrawLine(FadedEdgePen, new Point(box.X + 12, box.Y - 9), new Point(box.X + 12, box.Y));
        if (state == CutState.Cut)
        {
            var label = Draw.Text("✂ 새 출력", Draw.SemiBold, 10.5, Brushes.White, dpi);
            var tab = new Rect(box.CenterX - (label.Width + 14) / 2, box.Bottom + 3, label.Width + 14, 16);
            dc.DrawRoundedRectangle(Primary, null, tab, 8, 8);
            dc.DrawText(label, new Point(tab.Left + 7, tab.Top + (16 - label.Height) / 2));
        }
    }

    private const double NoteWidth = 200;

    /// <summary>
    /// A free spot for the note of the cut-off part: beside its first rows, to the right of everything drawn there
    /// (or to the left when there is room only there). Null when no row has room; the side panel says the same thing.
    /// </summary>
    private static Rect? PlaceNote(GraphLayout layout, Rect zone, double height)
    {
        const double width = NoteWidth;
        var limit = layout.Width - 6;
        Rect? place = null;
        var firstRow = Math.Max(0, (int)Math.Round((zone.Top + 4 - GraphLayout.Margin) / GraphLayout.RowStep));
        for (var row = firstRow; row < layout.Rows && place is null; row++)
        {
            var top = GraphLayout.Margin + row * GraphLayout.RowStep;
            if (top + height > zone.Bottom - 6)
                break;
            double maxRight = zone.Left, minLeft = limit;
            foreach (var box in layout.Boxes)
            {
                if (box.Kind == GraphBoxKind.Fold || box.Bottom < top - 2 || box.Y > top + height + 2)
                    continue;
                maxRight = Math.Max(maxRight, box.X + box.Width);
                minLeft = Math.Min(minLeft, box.X);
            }
            foreach (var edge in layout.Edges.Where(e => e.Points.Count > 2))
                for (var k = 1; k < edge.Points.Count - 1; k++)
                    if (edge.Points[k].Y >= top - 2 && edge.Points[k].Y <= top + height + 2)
                    {
                        maxRight = Math.Max(maxRight, edge.Points[k].X + 8);
                        minLeft = Math.Min(minLeft, edge.Points[k].X - 8);
                    }
            if (limit - 8 - maxRight - 12 >= width)
                place = new Rect(maxRight + 12, top, width, height);
            else if (minLeft - 12 - zone.Left - 8 >= width)
                place = new Rect(zone.Left + 8, top, width, height);
        }
        return place;
    }

    /// <summary>Why the picked operator cannot run, next to it.</summary>
    private void DrawPopover(DrawingContext dc, GraphLayout layout, PlacedBox target, Rect? avoid, double dpi)
    {
        const double width = 220;
        var title = Draw.Text(PopTitle, Draw.SemiBold, 12.5, Heading, dpi, width - 24);
        var name = Draw.Text(PopName, Draw.Mono, 11, Muted, dpi, width - 24);
        var body = Draw.Text(PopBody, Draw.Regular, 12, Text, dpi, width - 24, 18);
        var height = 9 + title.Height + name.Height + 4 + body.Height + 9;

        var top = Math.Min(target.Y - 2, layout.Height - height - 4);
        Rect[] candidates =
        [
            new(target.X + target.Width + 8, top, width, height),
            new(target.X - width - 8, top, width, height),
            new(Math.Clamp(target.CenterX - width / 2, 4, Math.Max(4, layout.Width - width - 4)), target.Bottom + 6, width, height),
        ];
        bool Fits(Rect r) => r.Left >= 4 && r.Right <= layout.Width - 4;
        var rect = candidates.FirstOrDefault(r => Fits(r) && !(avoid is { } a && a.IntersectsWith(r)));
        if (rect.Width == 0)
            rect = candidates.FirstOrDefault(Fits);
        if (rect.Width == 0)
            rect = candidates[2];

        var accent = PopKind == ChipKind.Bad ? BadText : Draw.Brush("#D97706");
        dc.DrawRoundedRectangle(Shadow, null, new Rect(rect.Left + 1, rect.Top + 3, rect.Width, rect.Height), 8, 8);
        dc.DrawRoundedRectangle(Brushes.White, NotePen, rect, 8, 8);
        dc.PushClip(new RectangleGeometry(rect, 8, 8));
        dc.DrawRectangle(accent, null, new Rect(rect.Left, rect.Top, 3, rect.Height));
        dc.Pop();
        var y = rect.Top + 9;
        dc.DrawText(title, new Point(rect.Left + 14, y));
        y += title.Height;
        dc.DrawText(name, new Point(rect.Left + 14, y));
        y += name.Height + 4;
        dc.DrawText(body, new Point(rect.Left + 14, y));
    }
}

/// <summary>
/// The whole drawing as a narrow strip beside it: one mark per row (blue where a cut is, red where an operator cannot
/// run, amber where one is stuck behind), and a frame for the part in view. A click raises <see cref="Seek"/> with
/// the position, 0 = top, 1 = bottom.
/// </summary>
public sealed class GraphMinimap : FrameworkElement
{
    private static readonly Brush Background = Draw.Brush("#F3F4F6");
    private static readonly Brush Plain = Draw.Brush("#D5D9E0");
    private static readonly Brush Bad = Draw.Brush("#C8312A");
    private static readonly Brush Blocked = Draw.Brush("#D97706");
    private static readonly Brush Cut = Draw.Brush("#3B5998");
    private static readonly Brush ViewFill = Draw.Brush("#80FFFFFF");
    private static readonly Pen ViewPen = Draw.Pen("#636363", 1.5);

    public static readonly DependencyProperty LayoutProperty = Register(nameof(Layout), typeof(GraphLayout), null);
    public static readonly DependencyProperty CutEffectProperty = Register(nameof(CutEffect), typeof(CutEffect), null);
    public static readonly DependencyProperty ViewTopProperty = Register(nameof(ViewTop), typeof(double), 0.0);
    public static readonly DependencyProperty ViewHeightProperty = Register(nameof(ViewHeight), typeof(double), 1.0);

    private static DependencyProperty Register(string name, Type type, object? value) =>
        DependencyProperty.Register(name, type, typeof(GraphMinimap), new FrameworkPropertyMetadata(value, FrameworkPropertyMetadataOptions.AffectsRender));

    public GraphLayout? Layout { get => (GraphLayout?)GetValue(LayoutProperty); set => SetValue(LayoutProperty, value); }
    public CutEffect? CutEffect { get => (CutEffect?)GetValue(CutEffectProperty); set => SetValue(CutEffectProperty, value); }
    /// <summary>Top of the part in view, as a fraction of the whole drawing.</summary>
    public double ViewTop { get => (double)GetValue(ViewTopProperty); set => SetValue(ViewTopProperty, value); }
    public double ViewHeight { get => (double)GetValue(ViewHeightProperty); set => SetValue(ViewHeightProperty, value); }

    public event Action<double>? Seek;

    protected override Size MeasureOverride(Size availableSize) => new(26, double.IsInfinity(availableSize.Height) ? 300 : availableSize.Height);

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (ActualHeight > 0)
            Seek?.Invoke(Math.Clamp(e.GetPosition(this).Y / ActualHeight, 0, 1));
        e.Handled = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var area = new Rect(RenderSize);
        dc.DrawRoundedRectangle(Background, null, area, 8, 8);
        if (Layout is not { } layout || layout.Rows == 0 || area.Height < 20)
            return;
        var effect = CutEffect;
        var step = (area.Height - 16) / layout.Rows;
        foreach (var row in layout.Boxes.Where(b => b.Kind == GraphBoxKind.Operator).GroupBy(b => b.Row))
        {
            var cut = row.Any(b => effect?.StateOf(b.Id) == CutState.Cut);
            var brush = cut ? Cut : row.Any(b => b.Node!.State == "bad") ? Bad : row.Any(b => b.Node!.State == "blocked") ? Blocked : Plain;
            var y = 8 + (row.Key + 0.5) * step;
            dc.DrawRoundedRectangle(brush, null, cut ? new Rect(2, y - 1, area.Width - 4, 2) : new Rect(6, y - 1.5, area.Width - 12, 3), 1.5, 1.5);
        }
        var top = Math.Clamp(ViewTop, 0, 1) * area.Height;
        var height = Math.Max(24, Math.Clamp(ViewHeight, 0, 1) * area.Height);
        dc.DrawRoundedRectangle(ViewFill, ViewPen, new Rect(1.5, Math.Min(top, area.Height - height) + 0.75, area.Width - 3, height - 1.5), 6, 6);
    }
}
