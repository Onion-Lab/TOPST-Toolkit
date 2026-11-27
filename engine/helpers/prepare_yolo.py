"""Prepares a YOLO model as Ultralytics exports it for the ENLIGHT toolkit.

    python prepare_yolo.py --toolkit <toolkit root> --out <json> --model <exported.onnx> --output-dir <dir> [--stem <name>]

An exported YOLOv8 model ends in box maths the NPU cannot run (DFL, Slice, Sub, Div ... and one output
[1, 4 + classes, anchors]). The toolkit expects two things instead (feature/02, recipe E "사전 준비"):

  <stem>_extracted.onnx   the model cut at the six head convolutions: per scale the class scores, then the box
                          distribution (the order of the toolkit's extract_yolo_onnx.sh, --output-order cl)
  <stem>.bin              the grid coordinates the board-side box maths needs (ENLTGRID file, the same bytes
                          tools/gen_default_box_tools/default_box_generator.py writes)

The toolkit's own scripts are bash, need a venv and hard-code the layer names of the sample models. This helper
finds the head by its structure, so the name of the head module (model.22, model.23, ...) does not matter.

Uses onnx from <runtime>/extras/onnx and never imports enlight (the two cannot share a process).
Supported: the anchor-free head of YOLOv8 (the same head as YOLO11). Anything else is reported, not guessed.
"""
import datetime
import os
import struct
import sys

from _common import add_onnx_path, parser, run

GRID_FILE_VERSION = "ENBIN_V1"


def dims_of(value_info):
    return [d.dim_value if d.HasField("dim_value") else None for d in value_info.type.tensor_type.shape.dim]


def find_v8_head(graph, classes):
    """The three (class conv output, box conv output) pairs, largest grid first.

    Up to Ultralytics 8.3 each scale ends in  Concat(box conv, class conv) -> Reshape -> Concat(all scales).
    Ultralytics 8.4 joins the two kinds separately:  box conv -> Reshape -> Concat(box of all scales), and the same
    for the class convs (seen 2026-11-22 in the model zoo review; the old pattern found nothing there).
    Either way the convolutions are plain 1x1 convolutions without an activation, and nothing else reads them.
    """
    pairs = find_v8_head_joined(graph, classes)
    return pairs if pairs else find_v8_head_split(graph, classes)


def find_v8_head_split(graph, classes):
    """Ultralytics 8.4: Conv -> Reshape -> Concat(one input per scale), once for the boxes and once for the classes."""
    produced = {o: n for n in graph.node for o in n.output}
    readers = {}
    for n in graph.node:
        for i in n.input:
            readers.setdefault(i, []).append(n)
    weights = {t.name: list(t.dims) for t in graph.initializer}
    branches = []
    for concat in graph.node:
        if concat.op_type != "Concat" or len(concat.input) != 3:
            continue
        convs = []
        for i in concat.input:
            reshape = produced.get(i)
            conv = produced.get(reshape.input[0]) if reshape is not None and reshape.op_type == "Reshape" else None
            if conv is None or conv.op_type != "Conv" or len(readers.get(conv.output[0], [])) != 1:
                break
            convs.append(conv)
        else:
            counts = [weights.get(c.input[1], [None])[0] if len(c.input) > 1 else None for c in convs]
            if None not in counts and len(set(counts)) == 1:
                branches.append((counts[0], convs))
    cls = [b for b in branches if b[0] == classes]
    box = [b for b in branches if b[0] != classes and b[0] % 4 == 0]
    if len(cls) != 1 or len(box) != 1:
        return []
    return [(cls[0][1][i], box[0][1][i], box[0][0]) for i in range(3)]


def find_v8_head_joined(graph, classes):
    """Up to Ultralytics 8.3: Concat(box conv, class conv) per scale, read by a Reshape."""
    produced = {o: n for n in graph.node for o in n.output}
    readers = {}
    for n in graph.node:
        for i in n.input:
            readers.setdefault(i, []).append(n)
    weights = {t.name: list(t.dims) for t in graph.initializer}

    def channels(conv):
        shape = weights.get(conv.input[1]) if len(conv.input) > 1 else None
        return shape[0] if shape else None

    pairs = []
    for concat in graph.node:
        if concat.op_type != "Concat" or len(concat.input) != 2:
            continue
        axis = next((a.i for a in concat.attribute if a.name == "axis"), None)
        if axis != 1:
            continue
        convs = [produced.get(i) for i in concat.input]
        if any(c is None or c.op_type != "Conv" for c in convs):
            continue
        if any(len(readers.get(c.output[0], [])) != 1 for c in convs):
            continue
        if not any(r.op_type == "Reshape" for r in readers.get(concat.output[0], [])):
            continue
        a, b = convs
        ca, cb = channels(a), channels(b)
        if ca is None or cb is None:
            continue
        # which one holds the class scores: the channel count, and the Ultralytics name (cv3) when both could be it
        if ca == classes and cb != classes:
            cls, box = a, b
        elif cb == classes and ca != classes:
            cls, box = b, a
        elif "cv3" in a.name and "cv2" in b.name:
            cls, box = a, b
        elif "cv3" in b.name and "cv2" in a.name:
            cls, box = b, a
        else:
            continue
        pairs.append((cls, box, channels(box)))
    return pairs


def extract(model, output_names, shapes):
    """A copy of the model that ends at the given tensors (what onnx.utils.extract_model does, without the checker,
    which refuses models newer than this onnx knows)."""
    import onnx

    graph = model.graph
    produced = {o: index for index, n in enumerate(graph.node) for o in n.output}
    keep = set()
    stack = list(output_names)
    while stack:
        index = produced.get(stack.pop())
        if index is None or index in keep:
            continue
        keep.add(index)
        stack.extend(i for i in graph.node[index].input if i)

    result = onnx.ModelProto()
    result.CopyFrom(model)
    g = result.graph
    nodes = [n for index, n in enumerate(graph.node) if index in keep]
    used = set(i for n in nodes for i in n.input)
    del g.node[:]
    g.node.extend(nodes)
    initializers = [t for t in graph.initializer if t.name in used]
    del g.initializer[:]
    g.initializer.extend(initializers)
    inputs = [v for v in graph.input if v.name in used]
    del g.input[:]
    g.input.extend(inputs)
    del g.value_info[:]
    del g.output[:]
    for name in output_names:
        g.output.append(onnx.helper.make_tensor_value_info(name, onnx.TensorProto.FLOAT, shapes[name]))
    return result


def grid_values(grids, offset):
    """Grid coordinates as default_box_generator.py makes them for an anchor-free head: per scale, row by row,
    (x, y) of each cell minus the offset; all scales one after the other. Shape [1, 1, sum(h * w * 2)]."""
    values = []
    for height, width in grids:
        for y in range(height):
            for x in range(width):
                values.append(x - offset)
                values.append(y - offset)
    return values


def write_grid_file(path, values, today=None):
    """ENLTGRID file (feature/04 section 2.2): '8s4f104s' header, then float32 values."""
    date = (today or datetime.date.today()).strftime("%Y%m%d")
    shape = (1, 1, len(values))
    header = struct.pack("8s4f104s", b"ENLTGRID", float(len(shape)), float(shape[0]), float(shape[1]), float(shape[2]),
                         (GRID_FILE_VERSION + date).encode())
    with open(path, "wb") as fp:
        fp.write(header)
        fp.write(struct.pack("%df" % len(values), *values))
    return list(shape)


def main(args):
    add_onnx_path()
    import onnx
    from onnx import shape_inference

    path = os.path.abspath(args.model)
    result = {"ok": False, "path": path, "family": None, "problems": []}
    model = onnx.load(path, load_external_data=False)
    graph = model.graph

    outputs = [dims_of(o) for o in graph.output]
    # exported YOLOv8 / YOLO11: one output [batch, 4 + classes, anchors]
    if not (len(outputs) == 1 and len(outputs[0]) == 3 and outputs[0][1] and outputs[0][2] and outputs[0][2] > outputs[0][1] > 4):
        result["problems"].append("not an exported YOLOv8 model: the output is %s, expected one [1, 4 + classes, anchors]" % outputs)
        return result
    classes = outputs[0][1] - 4

    pairs = find_v8_head(graph, classes)
    if len(pairs) != 3:
        result["problems"].append("found %d detection heads, expected 3 (class and box convolution joined by a Concat)" % len(pairs))
        return result

    names = [n for cls, box, _ in pairs for n in (cls.output[0], box.output[0])]
    try:
        inferred = shape_inference.infer_shapes(model)
        shapes = {v.name: dims_of(v) for v in inferred.graph.value_info if v.name in names}
    except Exception as exc:
        result["problems"].append("shape inference failed: %s" % (str(exc).splitlines()[0] if str(exc) else type(exc).__name__))
        return result
    if any(n not in shapes or len(shapes[n]) != 4 or None in shapes[n] for n in names):
        result["problems"].append("the size of the detection heads could not be read (dynamic input size?)")
        return result

    # largest grid first, the order of the toolkit's sample and of the board-side decoder
    pairs.sort(key=lambda p: -(shapes[p[0].output[0]][2] * shapes[p[0].output[0]][3]))
    ordered = [n for cls, box, _ in pairs for n in (cls.output[0], box.output[0])]
    box_channels = set(c for _, _, c in pairs)
    if len(box_channels) != 1 or next(iter(box_channels)) % 4 != 0:
        result["problems"].append("unexpected box channels %s" % sorted(box_channels))
        return result
    reg_max = next(iter(box_channels)) // 4
    anchors = sum(shapes[cls.output[0]][2] * shapes[cls.output[0]][3] for cls, _, _ in pairs)
    if anchors != outputs[0][2]:
        result["problems"].append("the heads have %d cells but the model reports %d anchors" % (anchors, outputs[0][2]))
        return result

    stem = args.stem or os.path.splitext(os.path.basename(path))[0]
    os.makedirs(args.output_dir, exist_ok=True)
    extracted_path = os.path.join(os.path.abspath(args.output_dir), stem + "_extracted.onnx")
    grid_path = os.path.join(os.path.abspath(args.output_dir), stem + ".bin")

    extracted = extract(model, ordered, shapes)
    onnx.save(extracted, extracted_path)
    grids = [(shapes[cls.output[0]][2], shapes[cls.output[0]][3]) for cls, _, _ in pairs]
    grid_shape = write_grid_file(grid_path, grid_values(grids, -0.5))  # offset of v6 / v8 in the toolkit's generator

    input_shape = next((dims_of(v) for v in graph.input if v.name not in {t.name for t in graph.initializer}), None)
    result.update({
        "ok": True,
        "family": "v8",
        "extracted": extracted_path,
        "grid": grid_path,
        "gridShape": grid_shape,
        "grids": [list(g) for g in grids],
        "outputs": [{"name": n, "shape": shapes[n]} for n in ordered],
        "numClass": classes,
        "regMax": reg_max,
        "outputOrder": "cl",
        "inputShape": input_shape,
        "opset": next((o.version for o in model.opset_import if o.domain in ("", "ai.onnx")), None),
        "removedNodes": len(graph.node) - len(extracted.graph.node),
    })
    return result


if __name__ == "__main__":
    ap = parser(__doc__)
    ap.add_argument("--model", required=True, help="the exported .onnx file")
    ap.add_argument("--output-dir", required=True, help="folder for <stem>_extracted.onnx and <stem>.bin")
    ap.add_argument("--stem", default=None, help="file name stem of the results (default: the model's)")
    sys.exit(run(main, ap.parse_args()))
