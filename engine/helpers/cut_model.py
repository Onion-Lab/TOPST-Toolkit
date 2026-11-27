"""Cuts an ONNX model at the operators chosen on the graph (design section 5.7).

    python cut_model.py --toolkit <toolkit root> --out <json> --model <file.onnx> --output-dir <dir> --stem <name>
                        --nodes <operator name> [<operator name> ...]

The result of each chosen operator becomes an output of the model; everything behind it is removed, and so is every
branch no remaining output needs. An output of the original model that is not behind a cut stays.

  <stem>_extracted.onnx   the cut model
  <stem>.bin              only when the cut is the head of an exported YOLOv8 model: the grid file of prepare_yolo.py,
                          and the outputs are put in the order the toolkit's YOLOv8 box maths expects

Operator names are the ones graph_model.py reports (an unnamed operator is called <type>_<position>).
Uses onnx from <runtime>/extras/onnx and never imports enlight (the two cannot share a process).
"""
import os
import sys

from _common import add_onnx_path, parser, run


def main(args):
    add_onnx_path()
    import onnx
    from onnx import shape_inference

    from graph_model import Graph, cut_effect, dims_of, find_yolo_v8
    from prepare_yolo import extract, grid_values, write_grid_file

    path = os.path.abspath(args.model)
    result = {"ok": False, "path": path, "family": None, "problems": []}
    model = onnx.load(path, load_external_data=False)
    graph = Graph(model)

    by_name = {}
    for i, name in enumerate(graph.names):
        by_name.setdefault(name, []).append(i)
    cut = []
    for name in args.nodes:
        found = by_name.get(name, [])
        if len(found) != 1:
            result["problems"].append("operator not found: %s" % name if not found else "operator name is not unique: %s" % name)
            continue
        cut.append(found[0])
    if result["problems"]:
        return result
    cut = sorted(set(cut))

    behind = graph.reach(cut, graph.children)
    nested = [graph.names[i] for i in cut if i in behind]
    if nested:
        result["problems"].append("these operators are behind another chosen one: %s" % ", ".join(nested))
        return result

    types = {}
    shapes = {}
    try:
        inferred = shape_inference.infer_shapes(model)
        for v in inferred.graph.value_info:
            shapes[v.name] = dims_of(v)
            types[v.name] = v.type.tensor_type.elem_type
    except Exception as exc:
        result["problems"].append("shape inference failed: %s" % (str(exc).splitlines()[0] if str(exc) else type(exc).__name__))
    for o in model.graph.output:
        shapes[o.name] = dims_of(o)
        types[o.name] = o.type.tensor_type.elem_type

    # the head of an exported YOLOv8: the order and the grid file the toolkit's box maths needs
    family = None
    head = None
    try:
        head = find_yolo_v8(graph)
    except Exception:
        head = None
    ordered = None
    if head is not None and set(cut) == set(i for cls, box, _ in head["pairs"] for i in (cls, box)):
        pairs = head["pairs"]
        sizes = [shapes.get(graph.nodes[cls].output[0]) for cls, _, _ in pairs]
        channels = set(c for _, _, c in pairs)
        if all(s and len(s) == 4 and None not in s for s in sizes) and len(channels) == 1 and next(iter(channels)) % 4 == 0:
            order = sorted(range(3), key=lambda k: -(sizes[k][2] * sizes[k][3]))
            ordered = [i for k in order for i in (pairs[k][0], pairs[k][1])]
            family = "v8"
            grids = [(sizes[k][2], sizes[k][3]) for k in order]
    if ordered is None:
        ordered = cut

    names = []
    for i in ordered:
        names += [o for o in graph.nodes[i].output if o]
    kept_outputs = [name for name, producer in graph.outputs if producer is not None and producer not in behind and name not in names]
    names += kept_outputs
    for name in names:
        shapes.setdefault(name, None)

    gone, dropped = cut_effect(graph, cut)
    stem = args.stem or os.path.splitext(os.path.basename(path))[0]
    os.makedirs(args.output_dir, exist_ok=True)
    extracted_path = os.path.join(os.path.abspath(args.output_dir), stem + "_extracted.onnx")
    extracted = extract(model, names, shapes)
    for value in extracted.graph.output:  # extract() assumes float; keep the real element type where it is known
        if types.get(value.name):
            value.type.tensor_type.elem_type = types[value.name]
    onnx.save(extracted, extracted_path)

    result.update({
        "ok": True,
        "family": family,
        "extracted": extracted_path,
        "outputs": [{"name": n, "shape": shapes.get(n)} for n in names],
        "nodes": [graph.names[i] for i in ordered],
        "keptOutputs": kept_outputs,
        "removedNodes": sum(1 for n in graph.nodes if n.op_type != "Constant") - sum(1 for n in extracted.graph.node if n.op_type != "Constant"),
        "keptNodes": sum(1 for n in extracted.graph.node if n.op_type != "Constant"),
        "opset": next((o.version for o in model.opset_import if o.domain in ("", "ai.onnx")), None),
    })
    if family == "v8":
        grid_path = os.path.join(os.path.abspath(args.output_dir), stem + ".bin")
        result.update({
            "grid": grid_path,
            "gridShape": write_grid_file(grid_path, grid_values(grids, -0.5)),
            "grids": [list(g) for g in grids],
            "numClass": head["classes"],
            "regMax": next(iter(channels)) // 4,
            "outputOrder": "cl",
        })
    return result


if __name__ == "__main__":
    ap = parser(__doc__)
    ap.add_argument("--model", required=True, help="the .onnx file to cut")
    ap.add_argument("--output-dir", required=True, help="folder for <stem>_extracted.onnx")
    ap.add_argument("--stem", default=None, help="file name stem of the results (default: the model's)")
    ap.add_argument("--nodes", required=True, nargs="+", help="names of the operators to cut at")
    sys.exit(run(main, ap.parse_args()))
