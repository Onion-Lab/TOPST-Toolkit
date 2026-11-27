using System.Globalization;
using TopstToolkit.Engine.Helpers;

namespace TopstToolkit.Core.Projects;

public enum GraphBoxKind
{
    Operator,
    /// <summary>The front part of the model that is not drawn operator by operator.</summary>
    Fold,
    /// <summary>An output of the model as it was brought in.</summary>
    Output,
}

/// <summary>A box of the drawing, in drawing units (the operator boxes are 96 × 40).</summary>
public sealed record PlacedBox(int Id, GraphBoxKind Kind, GraphNode? Node, GraphOutput? Output, int Row, double X, double Y, double Width, double Height,
    string Title, string Detail)
{
    public double CenterX => X + Width / 2;
    public double Bottom => Y + Height;
}

/// <summary>A connection from top to bottom; long ones run through a lane kept free for them.</summary>
public sealed record PlacedEdge(int From, int To, IReadOnlyList<(double X, double Y)> Points);

/// <summary>
/// Places the drawn part of a model graph (design §5.7): each operator one row below its lowest input, children under
/// their parents, long connections in their own lane so they do not run through boxes. The folded front part is one
/// wide box on top; the outputs of the model sit below the operators that produce them.
/// Pure geometry, no window code: any layout of the app draws from it.
/// </summary>
public sealed class GraphLayout
{
    public const double NodeWidth = 96;
    public const double NodeHeight = 40;
    public const double RowStep = 64;
    public const double Gap = 8;
    public const double Margin = 10;
    public const double MinContentWidth = 600;
    public const int FoldId = -1;
    private const double LaneWidth = 14;

    private GraphLayout(IReadOnlyList<PlacedBox> boxes, IReadOnlyList<PlacedEdge> edges, double width, double height, int rows)
    {
        Boxes = boxes;
        Edges = edges;
        Width = width;
        Height = height;
        Rows = rows;
    }

    public IReadOnlyList<PlacedBox> Boxes { get; }
    public IReadOnlyList<PlacedEdge> Edges { get; }
    public double Width { get; }
    public double Height { get; }
    public int Rows { get; }

    public PlacedBox? BoxOf(int id) => Boxes.FirstOrDefault(b => b.Id == id);

    /// <summary>"80×80×80" for [1, 80, 80, 80]: the size without the batch dimension.</summary>
    public static string SizeText(IReadOnlyList<int?>? shape)
    {
        if (shape is null || shape.Count == 0)
            return "";
        var dims = shape.Count > 1 && shape[0] == 1 ? shape.Skip(1) : shape;
        return string.Join("×", dims.Select(d => d?.ToString(CultureInfo.InvariantCulture) ?? "?"));
    }

    /// <summary>The second line of an operator box: what it holds (for a known head) and its size.</summary>
    public static string DetailOf(GraphNode node)
    {
        var size = SizeText(node.Shape);
        return node.Role switch
        {
            "class" => "클래스 " + size,
            "box" => "박스 " + size,
            _ => size,
        };
    }

    private sealed class Item
    {
        public int Id;
        public GraphBoxKind Kind;
        public GraphNode? Node;
        public GraphOutput? Output;
        public bool IsLane;
        public int Row;
        public double Width = NodeWidth;
        public double Center;
        public string Title = "";
        public string Detail = "";
        public readonly List<Item> Parents = [];
    }

    public static GraphLayout Compute(ModelGraphReport report)
    {
        var drawing = report.Graph;
        var hasFold = drawing.FoldOps > 0;
        var known = drawing.Nodes.Select(n => n.Id).ToHashSet();
        var parentIds = drawing.Nodes.ToDictionary(n => n.Id, _ => new List<int>());
        foreach (var e in drawing.Edges)
            if (e.Length == 2 && known.Contains(e[0]) && known.Contains(e[1]) && e[0] != e[1])
                parentIds[e[1]].Add(e[0]);

        // rows: one below the lowest input
        var rowOf = new Dictionary<int, int>();
        int Row(int id, int depth = 0)
        {
            if (rowOf.TryGetValue(id, out var r))
                return r;
            rowOf[id] = hasFold ? 1 : 0; // also the answer for a (malformed) cycle
            var parents = parentIds[id];
            r = parents.Count == 0 || depth > 4000 ? (hasFold ? 1 : 0) : parents.Max(p => Row(p, depth + 1)) + 1;
            rowOf[id] = r;
            return r;
        }
        foreach (var node in drawing.Nodes)
            Row(node.Id);

        var items = new Dictionary<int, Item>();
        foreach (var node in drawing.Nodes)
        {
            var detail = DetailOf(node);
            items[node.Id] = new Item
            {
                Id = node.Id, Kind = GraphBoxKind.Operator, Node = node, Row = rowOf[node.Id], Title = node.Op, Detail = detail,
                Width = Math.Clamp(Math.Max(22 + 7.6 * node.Op.Length, 20 + 6.3 * detail.Length), NodeWidth, 200),
            };
        }
        var lastRow = items.Count > 0 ? items.Values.Max(i => i.Row) : (hasFold ? 0 : -1);

        var all = new List<Item>(items.Values);
        var chains = new List<(int From, int To, List<Item> Lane)>();
        var nextLane = -1000;
        foreach (var node in drawing.Nodes)
        {
            foreach (var parent in parentIds[node.Id].Distinct())
            {
                var lane = new List<Item>();
                var above = items[parent];
                for (var r = rowOf[parent] + 1; r < rowOf[node.Id]; r++)
                {
                    var step = new Item { Id = nextLane--, IsLane = true, Row = r, Width = LaneWidth };
                    step.Parents.Add(above);
                    all.Add(step);
                    lane.Add(step);
                    above = step;
                }
                items[node.Id].Parents.Add(above);
                chains.Add((parent, node.Id, lane));
            }
        }

        var outputs = new List<Item>();
        for (var k = 0; k < drawing.Outputs.Count; k++)
        {
            var output = drawing.Outputs[k];
            var producer = output.Node is { } id && items.TryGetValue(id, out var found) ? found : null;
            var title = "출력 " + output.Name;
            var box = new Item
            {
                Id = -100 - k, Kind = GraphBoxKind.Output, Output = output, Row = (producer?.Row ?? lastRow) + 1, Title = title,
                Detail = string.Join("×", (output.Shape ?? []).Select(d => d?.ToString(CultureInfo.InvariantCulture) ?? "?")),
                Width = Math.Clamp(26 + 7.4 * title.Length, 130, 240),
            };
            if (producer is not null)
                box.Parents.Add(producer);
            all.Add(box);
            outputs.Add(box);
        }
        var rows = all.Count > 0 ? all.Max(i => i.Row) + 1 : 1;

        var byRow = all.GroupBy(i => i.Row).ToDictionary(g => g.Key, g => g.OrderBy(i => i.IsLane ? int.MaxValue : i.Id).ToList());
        var widest = byRow.Values.Select(r => r.Sum(i => i.Width) + Gap * (r.Count - 1)).DefaultIfEmpty(0).Max();
        var content = Math.Max(MinContentWidth, widest);

        for (var r = 0; r < rows; r++)
        {
            if (!byRow.TryGetValue(r, out var row))
                continue;
            // where each box would like to be: under its inputs
            var wish = new Dictionary<Item, double>();
            var orphans = row.Where(i => i.Parents.Count == 0).ToList();
            for (var k = 0; k < orphans.Count; k++)
                wish[orphans[k]] = Margin + (k + 0.5) * content / orphans.Count;
            foreach (var group in row.Where(i => i.Parents.Count > 0).GroupBy(i => string.Join(",", i.Parents.Select(p => p.Id).OrderBy(x => x))))
            {
                var members = group.ToList();
                var centre = members[0].Parents.Average(p => p.Center);
                var step = members.Average(m => m.Width) + Gap;
                for (var k = 0; k < members.Count; k++) // boxes with the same inputs fan out around them
                    wish[members[k]] = centre + (k - (members.Count - 1) / 2.0) * step;
            }

            var order = row.OrderBy(i => wish[i]).ThenBy(i => i.Id).ToList();
            var left = new double[order.Count];
            var right = new double[order.Count];
            for (var k = 0; k < order.Count; k++)
                left[k] = k == 0 ? wish[order[k]] : Math.Max(wish[order[k]], left[k - 1] + (order[k - 1].Width + order[k].Width) / 2 + Gap);
            for (var k = order.Count - 1; k >= 0; k--)
                right[k] = k == order.Count - 1 ? wish[order[k]] : Math.Min(wish[order[k]], right[k + 1] - (order[k].Width + order[k + 1].Width) / 2 - Gap);
            for (var k = 0; k < order.Count; k++)
                order[k].Center = (left[k] + right[k]) / 2; // pushed neither all the way left nor all the way right
        }

        // keep everything inside the drawing
        var minLeft = all.Count > 0 ? all.Min(i => i.Center - i.Width / 2) : Margin;
        var shift = minLeft < Margin ? Margin - minLeft : 0;
        foreach (var item in all)
            item.Center += shift;
        var maxRight = all.Count > 0 ? all.Max(i => i.Center + i.Width / 2) : 0;
        var width = Math.Max(content + 2 * Margin, maxRight + Margin);
        var height = Margin + (rows - 1) * RowStep + NodeHeight + Margin + 10;

        static double Top(int row) => Margin + row * RowStep;
        var boxes = new List<PlacedBox>();
        if (hasFold)
            boxes.Add(new PlacedBox(FoldId, GraphBoxKind.Fold, null, null, 0, Margin, Top(0), width - 2 * Margin, NodeHeight,
                "입력 → 모델의 앞부분", $"연산 {drawing.FoldOps:N0}개 접힘"));
        foreach (var item in all.Where(i => !i.IsLane).OrderBy(i => i.Row).ThenBy(i => i.Center))
            boxes.Add(new PlacedBox(item.Id, item.Kind, item.Node, item.Output, item.Row, item.Center - item.Width / 2, Top(item.Row), item.Width, NodeHeight,
                item.Title, item.Detail));

        var edges = new List<PlacedEdge>();
        foreach (var (from, to, lane) in chains)
        {
            var points = new List<(double, double)> { (items[from].Center, Top(items[from].Row) + NodeHeight) };
            foreach (var step in lane)
            {
                points.Add((step.Center, Top(step.Row)));
                points.Add((step.Center, Top(step.Row) + NodeHeight));
            }
            points.Add((items[to].Center, Top(items[to].Row)));
            edges.Add(new PlacedEdge(from, to, points));
        }
        foreach (var output in outputs.Where(o => o.Parents.Count > 0))
            edges.Add(new PlacedEdge(output.Parents[0].Id, output.Id,
                [(output.Parents[0].Center, Top(output.Parents[0].Row) + NodeHeight), (output.Center, Top(output.Row))]));
        if (hasFold)
            foreach (var item in items.Values.Where(i => i.Row == 1 && i.Parents.Count == 0))
                edges.Add(new PlacedEdge(FoldId, item.Id, [(item.Center, Top(0) + NodeHeight), (item.Center, Top(1))]));

        return new GraphLayout(boxes, edges, width, height, rows);
    }
}
