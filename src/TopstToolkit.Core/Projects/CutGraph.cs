using TopstToolkit.Engine.Helpers;

namespace TopstToolkit.Core.Projects;

/// <summary>What happens to an operator of the drawing for a chosen cut.</summary>
public enum CutState
{
    /// <summary>Stays in the part the NPU runs.</summary>
    Keep,
    /// <summary>A cut: its result becomes an output of the model.</summary>
    Cut,
    /// <summary>Behind a cut: removed, computed on the board instead.</summary>
    Gone,
    /// <summary>In front of no remaining output: removed because nothing needs it any more.</summary>
    Dropped,
}

/// <param name="States">By node id of the drawing.</param>
/// <param name="KeptOps">Operators left for the NPU (the folded front part included).</param>
/// <param name="AwayOps">Operators removed: behind a cut, or no longer needed.</param>
/// <param name="DroppedOps">The part of <paramref name="AwayOps"/> that is only "no longer needed".</param>
/// <param name="BadLeft">Operators the NPU cannot run that are still in the kept part.</param>
/// <param name="Overlap">One of the cuts lies behind another one.</param>
public sealed record CutEffect(IReadOnlyDictionary<int, CutState> States, int KeptOps, int AwayOps, int DroppedOps, int BadLeft, bool Overlap)
{
    public CutState StateOf(int id) => States.GetValueOrDefault(id, CutState.Keep);
}

/// <summary>
/// The rules of cutting on the graph (design §5.7), on the drawn part of the model: a cut at an operator makes its
/// result an output and removes everything behind it; what no remaining output needs goes too. Only an operator the
/// NPU can run and that is not stuck behind one it cannot run may be cut at.
/// The counts treat the folded front part as kept, so for an unusual cut they are a close estimate; the exact numbers
/// come from <c>cut_model.py</c> when the cut is made.
/// </summary>
public sealed class CutGraph
{
    private readonly Dictionary<int, List<int>> _children = [];
    private readonly Dictionary<int, List<int>> _parents = [];
    private readonly Dictionary<int, GraphNode> _byId = [];
    private readonly Dictionary<string, int> _byName = new(StringComparer.Ordinal);

    public CutGraph(ModelGraphReport report)
    {
        Report = report;
        foreach (var node in report.Graph.Nodes)
        {
            _byId[node.Id] = node;
            _byName[node.Name] = node.Id;
            _children[node.Id] = [];
            _parents[node.Id] = [];
        }
        foreach (var edge in report.Graph.Edges)
        {
            if (edge.Length != 2 || !_byId.ContainsKey(edge[0]) || !_byId.ContainsKey(edge[1]))
                continue;
            _children[edge[0]].Add(edge[1]);
            _parents[edge[1]].Add(edge[0]);
        }
    }

    public ModelGraphReport Report { get; }
    public IReadOnlyList<GraphNode> Nodes => Report.Graph.Nodes;

    public GraphNode? Find(int id) => _byId.GetValueOrDefault(id);
    public IReadOnlyList<int> ChildrenOf(int id) => _children.TryGetValue(id, out var list) ? list : [];
    public IReadOnlyList<int> ParentsOf(int id) => _parents.TryGetValue(id, out var list) ? list : [];

    /// <summary>Only a runnable operator in front of everything that cannot run can be cut at.</summary>
    public static bool CanCutAt(GraphNode node) => node.State == "ok";

    /// <summary>Node ids for operator names; names that are not in the drawing are left out.</summary>
    public List<int> IdsOf(IEnumerable<string> names) =>
        names.Select(n => _byName.TryGetValue(n, out var id) ? id : -1).Where(id => id >= 0).ToList();

    public List<string> NamesOf(IEnumerable<int> ids) =>
        ids.Where(_byId.ContainsKey).OrderBy(id => id).Select(id => _byId[id].Name).ToList();

    public CutEffect Evaluate(IReadOnlyCollection<int> cut)
    {
        var states = new Dictionary<int, CutState>(_byId.Count);
        var bad = Nodes.Count(n => n.State == "bad");
        if (cut.Count == 0)
        {
            foreach (var node in Nodes)
                states[node.Id] = CutState.Keep;
            return new CutEffect(states, Report.Ops, 0, 0, bad, false);
        }

        var gone = Reach(cut, _children);
        var ends = new HashSet<int>(cut);
        foreach (var output in Report.Graph.Outputs)
            if (output.Node is { } producer && !gone.Contains(producer))
                ends.Add(producer);
        var needed = Reach(ends, _parents);
        needed.UnionWith(ends);

        int kept = Report.Graph.FoldOps, away = Report.Graph.FarOps, dropped = 0, badLeft = 0;
        foreach (var node in Nodes)
        {
            var state = cut.Contains(node.Id) ? CutState.Cut
                : gone.Contains(node.Id) ? CutState.Gone
                : needed.Contains(node.Id) ? CutState.Keep
                : CutState.Dropped;
            states[node.Id] = state;
            switch (state)
            {
                case CutState.Gone: away++; break;
                case CutState.Dropped: away++; dropped++; break;
                default:
                    kept++;
                    if (node.State == "bad") badLeft++;
                    break;
            }
        }
        return new CutEffect(states, kept, away, dropped, badLeft, cut.Any(gone.Contains));
    }

    private static HashSet<int> Reach(IEnumerable<int> start, Dictionary<int, List<int>> links)
    {
        var seen = new HashSet<int>();
        var stack = new Stack<int>(start);
        while (stack.Count > 0)
        {
            if (!links.TryGetValue(stack.Pop(), out var next))
                continue;
            foreach (var id in next)
                if (seen.Add(id))
                    stack.Push(id);
        }
        return seen;
    }
}
