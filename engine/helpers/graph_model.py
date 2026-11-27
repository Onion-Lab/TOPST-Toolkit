"""Looks at an ONNX model the way the "check first" flow needs (design section 5.7): which operators the NPU cannot
run, what is stuck behind them, where the model can be cut, and the part of the graph worth drawing.

    python graph_model.py --toolkit <toolkit root> --out <json> --model <file.onnx> [--layers <json>] [--depth 2]

--layers  the incompatible layers of the converter's compatibility table: [{"name", "type", "description"}, ...].
          Without it every operator is taken as runnable (the drawing shows the end of the model).

Words used below
  bad       an operator the NPU cannot run (a layer of --layers, matched by name)
  blocked   an operator that could run but sits behind a bad one: its input never reaches the NPU
  frontier  "the last supported points": the runnable operators whose result a bad or blocked operator reads,
            leaving out those that sit behind another frontier operator (a cut removes everything behind it)
  verdict   clean    nothing to cut
            tail     everything bad is in a tail without learned layers: cut at the frontier and it is gone
            blocked  cutting would throw away learned layers (or there is nothing runnable in front): no cut helps

Uses onnx from <runtime>/extras/onnx and never imports enlight (the two cannot share a process).
"""
import json
import os
import re
import sys

from _common import add_onnx_path, parser, run

# operators that carry learned values in an initializer input
WEIGHTED = {"Conv": 1, "ConvTranspose": 1, "Gemm": 1, "MatMul": 1}
# more than this share of the model's learned values behind the cut: the cut would remove part of the network itself
LEARNED_SHARE_LIMIT = 0.01
MAX_DRAWN = 260
# a model this small is drawn whole, without a folded front part
DRAW_ALL_UP_TO = 60


def norm(name):
    return re.sub(r"[^0-9A-Za-z]+", "_", name or "").strip("_")


def dims_of(value_info):
    return [d.dim_value if d.HasField("dim_value") else None for d in value_info.type.tensor_type.shape.dim]


class Graph(object):
    def __init__(self, model):
        self.model = model
        g = model.graph
        self.nodes = [n for n in g.node]
        self.count = len(self.nodes)
        self.sizes = {}
        for t in g.initializer:
            size = 1
            for d in t.dims:
                size *= d
            self.sizes[t.name] = size
        produced = {}
        for i, n in enumerate(self.nodes):
            for o in n.output:
                if o:
                    produced[o] = i
        self.produced = produced
        self.is_const = [n.op_type == "Constant" for n in self.nodes]
        self.parents = [[] for _ in self.nodes]
        self.children = [[] for _ in self.nodes]
        for i, n in enumerate(self.nodes):
            seen = set()
            for x in n.input:
                p = produced.get(x)
                if p is None or p in seen or self.is_const[p]:
                    continue
                seen.add(p)
                self.parents[i].append(p)
                self.children[p].append(i)
        self.outputs = [(o.name, produced.get(o.name)) for o in g.output]
        # display names: an unnamed node is called by its operator and position
        self.names = [n.name or "%s_%d" % (n.op_type, i) for i, n in enumerate(self.nodes)]

    def reach(self, start, links, depth=None):
        """Everything reachable from start (start itself excluded unless reached again), optionally depth-limited."""
        seen = set()
        level = list(start)
        steps = 0
        while level and (depth is None or steps < depth):
            following = []
            for i in level:
                for j in links[i]:
                    if j not in seen:
                        seen.add(j)
                        following.append(j)
            level = following
            steps += 1
        return seen

    def weight_values(self, i):
        n = self.nodes[i]
        slot = WEIGHTED.get(n.op_type)
        if slot is None or len(n.input) <= slot:
            return 0
        total = self.sizes.get(n.input[slot], 0)
        if n.op_type in ("Conv", "ConvTranspose", "Gemm") and len(n.input) > 2:
            total += self.sizes.get(n.input[2], 0)
        return total


def match_layers(graph, layers):
    """Converter layer name '<Kind>_<n>__<node name with / and . as _>' -> node index.

    The separator is two underscores; a node name that starts with '/' (PyTorch 1.13 and later: '/model.22/Concat_3')
    makes it look like three. PyTorch 1.12 and older name nodes 'Softmax_119', which gives 'Softmax_0__Softmax_119'
    (seen 2026-11-22 in the model zoo review: splitting on three left every such layer unplaced, the check said "listed").
    """
    by_name = {}
    for i, name in enumerate(graph.names):
        if not graph.is_const[i]:
            by_name.setdefault(norm(name), []).append(i)
    alias = {"Permute": "Transpose", "Linear": "Gemm", "Upsample": "Resize"}
    bad, unmapped = {}, []
    for layer in layers:
        name = layer.get("name", "")
        key = norm(name.split("__", 1)[1]) if "__" in name else None
        found = by_name.get(key, []) if key else []
        if len(found) > 1:
            kind = alias.get(layer.get("type", ""), layer.get("type", ""))
            same = [i for i in found if graph.nodes[i].op_type == kind]
            found = same if len(same) == 1 else []
        if len(found) == 1:
            bad[found[0]] = layer
        else:
            unmapped.append(name)
    return bad, unmapped


def cut_effect(graph, cut):
    """What a cut at the given nodes does: (gone = behind the cut, dropped = no longer needed by any output)."""
    gone = graph.reach(cut, graph.children)
    ends = set(cut) | set(p for _, p in graph.outputs if p is not None and p not in gone)
    needed = set(ends) | graph.reach(ends, graph.parents)
    dropped = set(i for i in range(graph.count) if i not in gone and i not in needed and not graph.is_const[i])
    return gone, dropped


def away_stats(graph, away):
    """Operators and stored values that leave the NPU part when the nodes in `away` are removed."""
    weights = sum(graph.weight_values(i) for i in away)
    layers = sum(1 for i in away if graph.weight_values(i) > 0)
    return {"ops": sum(1 for i in away if not graph.is_const[i]), "weightValues": weights, "weightLayers": layers}


def find_yolo_v8(graph):
    """The exported YOLOv8 head, as prepare_yolo.py finds it: [(class conv index, box conv index)] largest grid first."""
    from prepare_yolo import dims_of as dims, find_v8_head
    from onnx import shape_inference

    g = graph.model.graph
    outputs = [dims(o) for o in g.output]
    if not (len(outputs) == 1 and len(outputs[0]) == 3 and outputs[0][1] and outputs[0][2] and outputs[0][2] > outputs[0][1] > 4):
        return None
    classes = outputs[0][1] - 4
    pairs = find_v8_head(g, classes)
    if len(pairs) != 3:
        return None
    result = []
    for cls, box, channels in pairs:
        result.append((graph.produced[cls.output[0]], graph.produced[box.output[0]], channels))
    return {"classes": classes, "pairs": result}


def find_ssd_head(graph, shape_of, behind):
    """An SSD-style model with its box maths attached has two outputs, class scores [1, N, classes] and boxes
    [1, N, 4]. Above each sits the Concat that gathers the raw values of all feature maps; those two are what the
    toolkit's SSD box maths reads. Returns (class concat, box concat) or None."""
    if len(graph.outputs) != 2:
        return None
    shapes = [shape_of(p) if p is not None else None for _, p in graph.outputs]
    if any(s is None or len(s) != 3 or None in s for s in shapes) or shapes[0][1] != shapes[1][1]:
        return None
    lasts = [s[2] for s in shapes]
    if lasts.count(4) != 1:
        return None

    def nearest_concat(start, last):
        seen, level = {start}, [start]
        while level:
            following = []
            for i in level:
                s = shape_of(i)
                if graph.nodes[i].op_type == "Concat" and len(graph.nodes[i].input) >= 2 and i not in behind \
                        and s and len(s) == 3 and s[1] == shapes[0][1] and s[2] == last:
                    return i
                for p in graph.parents[i]:
                    if p not in seen:
                        seen.add(p)
                        following.append(p)
            level = following
        return None

    box_at = lasts.index(4)
    box = nearest_concat(graph.outputs[box_at][1], 4)
    cls = nearest_concat(graph.outputs[1 - box_at][1], lasts[1 - box_at])
    if box is None or cls is None or box == cls:
        return None
    return cls, box


def main(args):
    add_onnx_path()
    import onnx
    from onnx import shape_inference

    path = os.path.abspath(args.model)
    result = {"ok": False, "path": path, "problems": []}
    model = onnx.load(path, load_external_data=False)
    graph = Graph(model)
    layers = []
    if args.layers:
        with open(args.layers, "r", encoding="utf-8") as fp:
            layers = json.load(fp)

    shapes = {}
    try:
        inferred = shape_inference.infer_shapes(model)
        shapes = {v.name: dims_of(v) for v in inferred.graph.value_info}
    except Exception as exc:  # the drawing works without sizes
        result["problems"].append("shape inference failed: %s" % (str(exc).splitlines()[0] if str(exc) else type(exc).__name__))
    for o in model.graph.output:
        shapes[o.name] = dims_of(o)

    def shape_of(i):
        out = graph.nodes[i].output[0] if graph.nodes[i].output else None
        return shapes.get(out)

    real = [i for i in range(graph.count) if not graph.is_const[i]]
    bad, unmapped = match_layers(graph, layers)
    blocked = graph.reach(bad.keys(), graph.children) - set(bad)
    behind = set(bad) | blocked                       # never reaches the NPU as the model is
    runnable = [i for i in real if i not in behind]
    # the last supported points: runnable, read by something behind; topmost only
    frontier_all = [i for i in runnable if any(c in behind for c in graph.children[i])]
    nested = graph.reach(frontier_all, graph.children)
    frontier = [i for i in frontier_all if i not in nested]

    total_weights = sum(graph.weight_values(i) for i in real)
    front_bad = [i for i in bad if not (graph.reach([i], graph.parents) - behind)]  # nothing runnable in front of it

    verdict, reason = "clean", None
    gone, dropped = set(), set()
    if bad:
        gone, dropped = cut_effect(graph, frontier)
        stats = away_stats(graph, gone | dropped)
        share = (stats["weightValues"] / float(total_weights)) if total_weights else 0.0
        if front_bad or not frontier:
            verdict, reason = "blocked", "front"
        elif share > LEARNED_SHARE_LIMIT:
            verdict, reason = "blocked", "learned"
        else:
            verdict = "tail"
    elif unmapped:
        result["problems"].append("%d incompatible layers could not be matched to an operator" % len(unmapped))

    # ---- a cut the app can propose
    proposal = None
    roles = {}
    head = None
    try:
        head = find_yolo_v8(graph)
    except Exception as exc:
        result["problems"].append("YOLO head search failed: %s" % exc)
    if head is not None:
        pairs = head["pairs"]
        sizes = [shape_of(c) for c, _, _ in pairs]
        if all(s and len(s) == 4 and None not in s for s in sizes):
            order = sorted(range(3), key=lambda k: -(sizes[k][2] * sizes[k][3]))
            nodes = []
            for k in order:
                cls, box, _ = pairs[k]
                nodes += [cls, box]
                roles[cls], roles[box] = "class", "box"
            head_gone = graph.reach(nodes, graph.children)
            channels = set(c for _, _, c in pairs)
            if all(b in head_gone for b in bad) and len(channels) == 1 and next(iter(channels)) % 4 == 0:
                proposal = {"kind": "yolo-v8", "decoder": "yolo-v8", "nodes": nodes,
                            "numClass": head["classes"], "regMax": next(iter(channels)) // 4}
    if proposal is None and verdict == "tail":
        ssd = find_ssd_head(graph, shape_of, behind)
        if ssd is not None and all(b in graph.reach(ssd, graph.children) for b in bad):
            roles[ssd[0]], roles[ssd[1]] = "class", "box"
            proposal = {"kind": "ssd", "decoder": "ssd", "nodes": list(ssd)}
    if proposal is None and verdict == "tail":
        proposal = {"kind": "frontier", "decoder": None, "nodes": list(frontier)}
    if proposal is not None:
        p_gone, p_dropped = cut_effect(graph, proposal["nodes"])
        proposal["away"] = away_stats(graph, p_gone | p_dropped)
        proposal["kept"] = len(real) - proposal["away"]["ops"]
        if verdict == "blocked" and proposal["kind"] == "yolo-v8":
            # the known head sits in front of everything bad: cutting there is fine whatever lies behind
            stats = proposal["away"]
            if (stats["weightValues"] / float(total_weights) if total_weights else 0.0) <= LEARNED_SHARE_LIMIT:
                verdict, reason = "tail", None

    # ---- the part worth drawing
    depth = max(0, args.depth)
    if bad or proposal:
        anchors = set(frontier) | set(proposal["nodes"] if proposal else [])
        shown = set(bad) | blocked | anchors | graph.reach(anchors, graph.children) | graph.reach(frontier, graph.parents, depth)
    else:
        ends = [p for _, p in graph.outputs if p is not None]
        shown = set(ends) | graph.reach(ends, graph.parents, max(depth, 6))
    shown |= set(p for _, p in graph.outputs if p is not None)  # an output is drawn below the operator that makes it
    if len(real) <= DRAW_ALL_UP_TO:
        shown = set(real)
    shown = set(i for i in shown if not graph.is_const[i])
    far = 0
    if len(shown) > MAX_DRAWN:
        # keep what is near the operators in question, fold the far end
        near = set(bad) | set(frontier) | set(proposal["nodes"] if proposal else [])
        keep = set(near)
        level = list(near)
        while level and len(keep) < MAX_DRAWN:
            following = []
            for i in level:
                for j in graph.children[i] + graph.parents[i]:
                    if j in shown and j not in keep:
                        keep.add(j)
                        following.append(j)
            level = following
        far = len(shown) - len(keep)
        shown = keep
    order = [i for i in real if i in shown]
    ids = {i: k for k, i in enumerate(order)}
    proposed = set(proposal["nodes"]) if proposal else set()
    drawn = []
    for i in order:
        n = graph.nodes[i]
        entry = {
            "id": ids[i], "name": graph.names[i], "op": n.op_type, "shape": shape_of(i),
            "state": "bad" if i in bad else "blocked" if i in blocked else "ok",
        }
        if i in bad:
            entry["layer"] = bad[i].get("name")
            entry["layerType"] = bad[i].get("type")
            entry["why"] = bad[i].get("description")
        if i in roles:
            entry["role"] = roles[i]
        if i in proposed:
            entry["proposed"] = True
        if i in frontier:
            entry["frontier"] = True
        w = graph.weight_values(i)
        if w:
            entry["weights"] = w
        if any(p not in shown for p in graph.parents[i]) or not graph.parents[i]:
            entry["fromFold"] = True
        if any(c not in shown for c in graph.children[i]):
            entry["toFold"] = True
        drawn.append(entry)
    edges = [[ids[p], ids[i]] for i in order for p in graph.parents[i] if p in ids]
    outputs = [{"name": name, "shape": shapes.get(name), "node": ids.get(p)} for name, p in graph.outputs]

    result.update({
        "ok": True,
        "ops": len(real),
        "weightValues": total_weights,
        "verdict": verdict,
        "reason": reason,
        "bad": [{"node": graph.names[i], "op": graph.nodes[i].op_type, "layer": bad[i].get("name"), "type": bad[i].get("type"),
                 "description": bad[i].get("description"), "id": ids.get(i)} for i in sorted(bad)],
        "unmapped": unmapped,
        "blockedOps": len(blocked),
        "frontier": [graph.names[i] for i in frontier],
        "frontierAway": away_stats(graph, gone | dropped) if bad else None,
        "proposal": None if proposal is None else dict(proposal, nodes=[graph.names[i] for i in proposal["nodes"]]),
        "graph": {
            "foldOps": len(real) - len(order) - far,
            "farOps": far,
            "nodes": drawn,
            "edges": edges,
            "outputs": outputs,
        },
    })
    return result


if __name__ == "__main__":
    ap = parser(__doc__)
    ap.add_argument("--model", required=True, help=".onnx file")
    ap.add_argument("--layers", default=None, help="JSON file: the incompatible layers of the compatibility table")
    ap.add_argument("--depth", type=int, default=2, help="how many operators in front of the last supported points to draw")
    sys.exit(run(main, ap.parse_args()))
