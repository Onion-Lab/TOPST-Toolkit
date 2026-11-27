"""The part of an ONNX model behind a cut ("the tail"): what the board's CPU has to compute once the NPU has run
everything in front of the cut (docs/design.md section 5.8, stage 나).

Three things live here, used by gen_tail.py (C code for the board) and eval_tail.py (the same maths on the PC):

  load_tail()      finds the tail of a model for a set of cut operators and folds what is constant
  evaluate()       runs a tail with numpy: the reference for what the generated code must produce
  TailGenerator    turns a tail into C. A value that only moves (reshape, transpose, concat, slice ...) or passes
                   through one-input functions (sigmoid, exp, x * constant ...) is never computed as a float on
                   its way: the generator tracks, for every element of every tensor, which byte of which NPU
                   output it comes from and which 256-entry table turns that byte into its value. Floats are
                   made only where an operator really needs them, read in the order the bytes lie in memory.

Uses onnx from <runtime>/extras/onnx and never imports enlight.
"""
import re

import numpy as np

FLOAT = np.float32


class TailError(Exception):
    """The tail cannot be generated; the message says why in terms a user of the graph page can act on."""


# ---------------------------------------------------------------------------------------------------- the tail

class Node(object):
    __slots__ = ("name", "op", "inputs", "outputs", "attrs")

    def __init__(self, name, op, inputs, outputs, attrs):
        self.name, self.op, self.inputs, self.outputs, self.attrs = name, op, inputs, outputs, attrs


class Tail(object):
    def __init__(self):
        self.inputs = []        # [(tensor name, shape, cut operator name)]: what the NPU hands over
        self.nodes = []         # [Node] in the order they have to run
        self.consts = {}        # tensor name -> numpy array
        self.outputs = []       # [(tensor name, original output name)]: outputs of the model behind the cut
        self.kept_outputs = []  # outputs of the model the cut does not touch (they stay NPU outputs)
        self.opset = 13


def _attributes(node, onnx):
    out = {}
    for a in node.attribute:
        v = onnx.helper.get_attribute_value(a)
        if isinstance(v, onnx.TensorProto):
            v = onnx.numpy_helper.to_array(v)
        elif isinstance(v, bytes):
            v = v.decode("utf-8", "replace")
        out[a.name] = v
    return out


def _static_shape(value_info):
    dims = []
    for d in value_info.type.tensor_type.shape.dim:
        if not d.HasField("dim_value"):
            return None
        dims.append(int(d.dim_value))
    return dims


def load_tail(model_path, cut_nodes):
    """The tail of the model behind the operators named in cut_nodes."""
    from _common import add_onnx_path
    add_onnx_path()
    import onnx
    from onnx import numpy_helper, shape_inference

    model = onnx.load(model_path, load_external_data=True)
    try:
        inferred = shape_inference.infer_shapes(model)
    except Exception:           # shape inference is a convenience; the cut tensors may still have shapes
        inferred = model
    graph = model.graph
    tail = Tail()
    tail.opset = max([o.version for o in model.opset_import if o.domain in ("", "ai.onnx")] or [13])

    shapes = {}
    for vi in list(inferred.graph.value_info) + list(inferred.graph.input) + list(inferred.graph.output):
        shapes[vi.name] = _static_shape(vi)
    init = {t.name: numpy_helper.to_array(t) for t in graph.initializer}
    by_name = {}
    for i, n in enumerate(graph.node):
        by_name[n.name or "%s_%d" % (n.op_type, i)] = n
    produced = {o: n for n in graph.node for o in n.output if o}

    start = {}
    for name in cut_nodes:
        if name not in by_name:
            raise TailError("the model has no operator named %s" % name)
        for o in by_name[name].output:
            if o:
                start[o] = name
    behind = set(start)
    ordered = []
    for n in graph.node:                        # an exported model lists its operators in running order
        if n.op_type == "Constant":
            continue
        if any(i in behind for i in n.input) and not any(o in start for o in n.output):
            ordered.append(n)
            behind.update(o for o in n.output if o)

    for tensor, cut in start.items():
        shape = shapes.get(tensor)
        if shape is None:
            raise TailError("the size of what %s produces is not fixed in the model file" % cut)
        tail.inputs.append((tensor, shape, cut))
    order = {name: i for i, name in enumerate(cut_nodes)}
    tail.inputs.sort(key=lambda item: order[item[2]])

    folding = set()

    def constant(name):
        """The value of a tensor that does not depend on the picture, or None."""
        if name in tail.consts:
            return tail.consts[name]
        if name in init:
            tail.consts[name] = init[name]
            return init[name]
        node = produced.get(name)
        if node is None or name in behind or name in folding:
            return None
        if node.op_type == "Constant":
            value = _attributes(node, onnx)
            array = value.get("value")
            if array is None:
                for key in ("value_float", "value_int", "value_floats", "value_ints"):
                    if key in value:
                        array = np.array(value[key], dtype=FLOAT if "float" in key else np.int64)
            if array is None:
                return None
            tail.consts[name] = np.asarray(array)
            return tail.consts[name]
        if node.op_type == "Shape":             # the size of a tensor is fixed even when its values are not
            shape = shapes.get(node.input[0])
            if shape is None:
                return None
            tail.consts[name] = np.array(shape, dtype=np.int64)
            return tail.consts[name]
        folding.add(name)
        try:
            inputs = []
            for i in node.input:
                value = constant(i) if i else None
                if i and value is None:
                    return None
                inputs.append(value)
            outputs = run_node(node.op_type, _attributes(node, onnx), inputs, tail.opset, len(node.output))
        except TailError:
            return None
        finally:
            folding.discard(name)
        for o, value in zip(node.output, outputs):
            if o:
                tail.consts[o] = value
        return tail.consts.get(name)

    missing = []
    for n in ordered:
        for i in n.input:
            if i and i not in behind and constant(i) is None:
                missing.append((i, n.name))
        tail.nodes.append(Node(n.name, n.op_type, list(n.input), list(n.output), _attributes(n, onnx)))
    if missing:
        names = sorted(set(produced[t].name if t in produced else t for t, _ in missing))
        raise TailError("the part behind the cut also reads what these operators produce, so they have to be "
                        "cut as well: " + ", ".join(names))

    for o in graph.output:
        if o.name in behind:
            tail.outputs.append((o.name, o.name))
        else:
            tail.kept_outputs.append(o.name)
    if not tail.outputs:
        raise TailError("no output of the model lies behind the cut")
    return tail


# ---------------------------------------------------------------------------------- the operators, in numpy

MOVES = {"Reshape": (0,), "Flatten": (0,), "Squeeze": (0,), "Unsqueeze": (0,), "Identity": (0,), "Dropout": (0,),
         "Transpose": (0,), "Slice": (0,), "Split": (0,), "Gather": (0,), "Expand": (0,), "Tile": (0,), "Concat": None}
UNARY = ("Sigmoid", "Exp", "Tanh", "Relu", "LeakyRelu", "Neg", "Abs", "Sqrt", "Reciprocal", "Log", "Clip", "Floor",
         "Ceil", "HardSigmoid", "Softplus")
BINARY = ("Add", "Sub", "Mul", "Div", "Max", "Min", "Pow")


def _axes(attrs, inputs, opset, since, index=1):
    """Axes given as an input from opset `since` on, as an attribute before."""
    if opset >= since and len(inputs) > index and inputs[index] is not None:
        return [int(a) for a in np.asarray(inputs[index]).reshape(-1)]
    if "axes" in attrs:
        return [int(a) for a in attrs["axes"]]
    return None


def unary(op, attrs, x, extra=()):
    """One-input functions, in float32 like the board."""
    x = np.asarray(x, dtype=FLOAT)
    with np.errstate(over="ignore", divide="ignore", invalid="ignore"):
        if op == "Sigmoid":
            return (FLOAT(1) / (FLOAT(1) + np.exp(-x))).astype(FLOAT)
        if op == "Exp":
            return np.exp(x).astype(FLOAT)
        if op == "Tanh":
            return np.tanh(x).astype(FLOAT)
        if op == "Relu":
            return np.maximum(x, FLOAT(0))
        if op == "LeakyRelu":
            return np.where(x >= 0, x, x * FLOAT(attrs.get("alpha", 0.01))).astype(FLOAT)
        if op == "Neg":
            return -x
        if op == "Abs":
            return np.abs(x)
        if op == "Sqrt":
            return np.sqrt(x).astype(FLOAT)
        if op == "Reciprocal":
            return (FLOAT(1) / x).astype(FLOAT)
        if op == "Log":
            return np.log(x).astype(FLOAT)
        if op == "Floor":
            return np.floor(x)
        if op == "Ceil":
            return np.ceil(x)
        if op == "HardSigmoid":
            return np.clip(FLOAT(attrs.get("alpha", 0.2)) * x + FLOAT(attrs.get("beta", 0.5)), 0, 1).astype(FLOAT)
        if op == "Softplus":
            return np.log1p(np.exp(x)).astype(FLOAT)
        if op == "Clip":
            lo, hi = extra
            if lo is not None:
                x = np.maximum(x, FLOAT(lo))
            if hi is not None:
                x = np.minimum(x, FLOAT(hi))
            return x
    raise TailError("operator %s is not supported" % op)


def binary(op, a, b):
    with np.errstate(over="ignore", divide="ignore", invalid="ignore"):
        if op == "Add":
            return a + b
        if op == "Sub":
            return a - b
        if op == "Mul":
            return a * b
        if op == "Div":
            if np.issubdtype(np.asarray(a).dtype, np.integer) and np.issubdtype(np.asarray(b).dtype, np.integer):
                return (np.trunc(np.asarray(a) / np.asarray(b))).astype(np.asarray(a).dtype)
            return a / b
        if op == "Max":
            return np.maximum(a, b)
        if op == "Min":
            return np.minimum(a, b)
        if op == "Pow":
            return np.power(a, b).astype(np.asarray(a).dtype)
    raise TailError("operator %s is not supported" % op)


def _clip_bounds(attrs, inputs, opset):
    if opset >= 11:
        lo = inputs[1] if len(inputs) > 1 else None
        hi = inputs[2] if len(inputs) > 2 else None
        return (None if lo is None else float(np.asarray(lo).reshape(-1)[0]),
                None if hi is None else float(np.asarray(hi).reshape(-1)[0]))
    return attrs.get("min"), attrs.get("max")


def softmax_axis(attrs, opset, rank):
    """The axis a Softmax works along, or None when it is the old form over several axes at once."""
    axis = int(attrs.get("axis", -1 if opset >= 13 else 1))
    if axis < 0:
        axis += rank
    if opset < 13 and axis != rank - 1:
        return None
    return axis


def run_node(op, attrs, inputs, opset, outputs=1):
    """One operator on numpy arrays. Operators that only move values work on any element type, which is how the
    generator pushes its index arrays through the very same code."""
    x = inputs[0] if inputs else None
    if op in ("Identity", "Dropout"):
        return [x]
    if op == "Reshape":
        shape = [int(s) for s in np.asarray(inputs[1]).reshape(-1)]
        if not attrs.get("allowzero", 0):
            shape = [x.shape[i] if s == 0 else s for i, s in enumerate(shape)]
        return [x.reshape(shape)]
    if op == "Flatten":
        axis = int(attrs.get("axis", 1))
        if axis < 0:
            axis += x.ndim
        return [x.reshape(int(np.prod(x.shape[:axis], dtype=np.int64)), -1)]
    if op == "Squeeze":
        axes = _axes(attrs, inputs, opset, 13)
        return [np.squeeze(x) if axes is None else np.squeeze(x, axis=tuple(axes))]
    if op == "Unsqueeze":
        axes = _axes(attrs, inputs, opset, 13)
        rank = x.ndim + len(axes)
        for a in sorted(a + rank if a < 0 else a for a in axes):
            x = np.expand_dims(x, a)
        return [x]
    if op == "Transpose":
        perm = attrs.get("perm")
        return [np.transpose(x, None if perm is None else [int(p) for p in perm])]
    if op == "Concat":
        return [np.concatenate(inputs, axis=int(attrs["axis"]))]
    if op == "Split":
        axis = int(attrs.get("axis", 0))
        sizes = None
        if opset >= 13 and len(inputs) > 1 and inputs[1] is not None:
            sizes = [int(s) for s in np.asarray(inputs[1]).reshape(-1)]
        elif "split" in attrs:
            sizes = [int(s) for s in attrs["split"]]
        if sizes is None:
            return list(np.array_split(x, outputs, axis=axis))
        return list(np.split(x, np.cumsum(sizes)[:-1], axis=axis))
    if op == "Slice":
        if opset >= 10:
            starts = np.asarray(inputs[1]).reshape(-1)
            ends = np.asarray(inputs[2]).reshape(-1)
            axes = np.asarray(inputs[3]).reshape(-1) if len(inputs) > 3 and inputs[3] is not None else np.arange(len(starts))
            steps = np.asarray(inputs[4]).reshape(-1) if len(inputs) > 4 and inputs[4] is not None else np.ones(len(starts), dtype=np.int64)
        else:
            starts, ends = np.asarray(attrs["starts"]), np.asarray(attrs["ends"])
            axes = np.asarray(attrs.get("axes", list(range(len(starts)))))
            steps = np.ones(len(starts), dtype=np.int64)
        index = [slice(None)] * x.ndim
        for s, e, a, st in zip(starts, ends, axes, steps):
            index[int(a)] = slice(int(max(min(s, 2 ** 62), -2 ** 62)), int(max(min(e, 2 ** 62), -2 ** 62)), int(st))
        return [x[tuple(index)]]
    if op == "Gather":
        return [np.take(x, np.asarray(inputs[1]).astype(np.int64), axis=int(attrs.get("axis", 0)))]
    if op == "Expand":
        shape = [int(s) for s in np.asarray(inputs[1]).reshape(-1)]
        return [np.broadcast_to(x, np.broadcast_shapes(x.shape, tuple(shape))) if hasattr(np, "broadcast_shapes")
                else x * np.ones(shape, dtype=x.dtype)]
    if op == "Tile":
        return [np.tile(x, [int(r) for r in np.asarray(inputs[1]).reshape(-1)])]
    if op == "Shape":
        return [np.array(x.shape, dtype=np.int64)]
    if op == "Cast":
        kinds = {1: FLOAT, 6: np.int32, 7: np.int64, 9: np.bool_, 10: np.float16, 11: np.float64}
        if int(attrs["to"]) not in kinds:
            raise TailError("Cast to element type %s is not supported" % attrs["to"])
        return [x.astype(kinds[int(attrs["to"])])]
    if op == "ConstantOfShape":
        value = attrs.get("value")
        value = FLOAT(0) if value is None else np.asarray(value).reshape(-1)[0]
        return [np.full([int(s) for s in np.asarray(x).reshape(-1)], value)]
    if op == "Range":
        return [np.arange(np.asarray(inputs[0]).item(), np.asarray(inputs[1]).item(), np.asarray(inputs[2]).item())]
    if op in UNARY:
        return [unary(op, attrs, x, _clip_bounds(attrs, inputs, opset) if op == "Clip" else ())]
    if op in BINARY:
        return [binary(op, inputs[0], inputs[1])]
    if op == "Softmax":
        axis = softmax_axis(attrs, opset, x.ndim)
        if axis is None:
            raise TailError("Softmax over several axes at once (an old model format) is not supported")
        x = np.asarray(x, dtype=FLOAT)
        e = np.exp(x - x.max(axis=axis, keepdims=True))
        return [(e / e.sum(axis=axis, keepdims=True)).astype(FLOAT)]
    if op == "Conv":
        w = np.asarray(inputs[1], dtype=FLOAT)
        b = np.asarray(inputs[2], dtype=FLOAT) if len(inputs) > 2 and inputs[2] is not None else None
        if not conv_is_pointwise(attrs, w):
            raise TailError("only 1x1 convolutions without stride, padding or groups are supported behind a cut")
        y = np.einsum("oc,nchw->nohw", w.reshape(w.shape[0], w.shape[1]), np.asarray(x, dtype=FLOAT)).astype(FLOAT)
        return [y if b is None else (y + b.reshape(1, -1, 1, 1)).astype(FLOAT)]
    raise TailError("operator %s is not supported" % op)


def conv_is_pointwise(attrs, weights):
    return (weights.ndim == 4 and weights.shape[2] == 1 and weights.shape[3] == 1
            and int(attrs.get("group", 1)) == 1
            and all(int(s) == 1 for s in attrs.get("strides", [1, 1]))
            and all(int(p) == 0 for p in attrs.get("pads", [0, 0, 0, 0]))
            and all(int(d) == 1 for d in attrs.get("dilations", [1, 1])))


def evaluate(tail, values):
    """Runs the tail on float arrays (tensor name -> array of the cut tensor's shape). Returns the outputs in order."""
    known = dict(tail.consts)
    for name, shape, _ in tail.inputs:
        known[name] = np.asarray(values[name], dtype=FLOAT).reshape(shape)
    for node in tail.nodes:
        inputs = [known[i] if i else None for i in node.inputs]
        for o, value in zip(node.outputs, run_node(node.op, node.attrs, inputs, tail.opset, len(node.outputs))):
            if o:
                known[o] = value
    return [np.asarray(known[name], dtype=FLOAT) for name, _ in tail.outputs]


# -------------------------------------------------------------------------------- what the NPU hands over

class NpuTensor(object):
    """One output tensor of the compiled network, as network.h describes it."""

    def __init__(self, name, index, offset, size, dims, scale, zero_point):
        self.name, self.index, self.offset, self.size = name, index, offset, size
        self.dims, self.scale, self.zero_point = dims, scale, zero_point

    @property
    def padded_channels(self):
        return (self.dims[1] + 31) & 0xFFE0

    def to_dict(self):
        return {"name": self.name, "index": self.index, "offset": self.offset, "size": self.size,
                "dims": self.dims, "scale": self.scale}


def read_network_header(path):
    """The output tensors of network.h, in the order of network_instance.output_tensors."""
    text = open(path, encoding="utf-8", errors="replace").read()
    tensors = {}
    pattern = re.compile(
        r"static\s+enlight_act_tensor_t\s+(\w+)\s*=\s*\{\s*(\w+)\s*,\s*\(void\*\)\s*\w+\s*,\s*(0x[0-9a-fA-F]+|\d+)\s*,"
        r"\s*(\d+)\s*,\s*\d+\s*,\s*\d+\s*,\s*\{([^}]*)\}\s*,\s*([-+0-9.eE]+)\s*,\s*(-?\d+)\s*,\s*(\d+)\s*,\s*\"([^\"]*)\"")
    for m in pattern.finditer(text):
        var, dtype, offset, size, dims, scale, zp, output, name = m.groups()
        tensors[var] = (dtype, int(offset, 0), int(size), [int(d) for d in dims.split(",") if d.strip()],
                        float(scale), int(zp), int(output), name)
    block = re.search(r"enlight_network_t\s+network_instance\s*=\s*\{(.*?)\n\};", text, re.S)
    if not block:
        raise TailError("network.h has no network_instance")
    listed = re.search(r"//output_tensors\s*\n\s*\{([^}]*)\}", block.group(1))
    if not listed:
        raise TailError("network.h does not list its output tensors")
    result = []
    for i, var in enumerate(v.strip().lstrip("&") for v in listed.group(1).split(",") if v.strip()):
        if var not in tensors:
            raise TailError("network.h lists the output tensor %s but does not define it" % var)
        dtype, offset, size, dims, scale, zp, output, name = tensors[var]
        if dtype != "ENLIGHT_DTYPE_INT8" or not output or len(dims) != 4:
            raise TailError("output tensor %s is not an 8-bit output in the layout this generator knows" % name)
        result.append(NpuTensor(name, i, offset, size, dims, scale, zp))
    return result


def layer_names(layers=None, layers_json=None, dump_dir=None):
    """The toolkit's full layer names (<Kind>_<n>___<operator name>), from whichever source a caller has:
    a text file with one name per line, the answer of read_enlight.py, or the *.oa.bin files of an INT8 dump."""
    import glob
    import json
    import os
    if layers_json:
        with open(layers_json, encoding="utf-8") as fp:
            return [str(layer.get("name", "")) for layer in json.load(fp).get("layers", [])]
    if layers:
        with open(layers, encoding="utf-8") as fp:
            return [line.strip() for line in fp if line.strip()]
    if dump_dir:
        return [os.path.basename(path)[:-len(".oa.bin")] for path in glob.glob(os.path.join(dump_dir, "*.oa.bin"))]
    raise TailError("no source for the layer names was given")


def normalized(onnx_name):
    """An ONNX operator name the way the toolkit writes it into its layer names."""
    return re.sub(r"[^0-9A-Za-z_]", "_", onnx_name.lstrip("/"))


def match_inputs(tail, npu_tensors, layer_names):
    """Which NPU output tensor holds each cut tensor: [(tail input, NpuTensor)].

    The toolkit names a layer <Kind>_<n>___<operator name with / and . as _>, and network.h calls its tensor
    <Kind>_<n>. layer_names are the full names (from the compatibility table or the dump file names)."""
    short = {}
    for full in layer_names:
        if "___" in full:
            head, rest = full.split("___", 1)
            short[rest] = head
    by_name = {t.name: t for t in npu_tensors}
    pairs = []
    for name, shape, cut in tail.inputs:
        tensor = by_name.get(short.get(normalized(cut), ""))
        if tensor is None:
            raise TailError("the compiled network has no output for the cut at %s (the toolkit may have merged that "
                            "operator into another layer)" % cut)
        pairs.append(((name, shape, cut), tensor))
    return pairs


def npu_offsets(shape, tensor):
    """For a cut tensor of the given ONNX shape: the byte offset of every element inside the NPU tensor, whose
    memory is height x width x channels with the channels padded to a multiple of 32."""
    _, channels, height, width = tensor.dims
    pad = tensor.padded_channels

    def fits(count):        # network.h gives the real channel count, or the padded one for joined tensors
        return count == channels or (channels % 32 == 0 and channels - 32 < count <= channels)

    if not shape or shape[0] != 1:
        raise TailError("the cut at layer %s produces a batch of %s, only 1 is supported" % (tensor.name, shape[:1]))
    rest = list(shape[1:])
    layouts = []
    if len(rest) == 3 and fits(rest[0]) and rest[1] == height and rest[2] == width:
        c, h, w = np.indices(rest)
        layouts.append(c + pad * w + pad * width * h)
    if len(rest) == 2 and width == 1:
        if fits(rest[0]) and rest[1] == height:             # channels first
            c, h = np.indices(rest)
            layouts.append(c + pad * h)
        if rest[0] == height and fits(rest[1]):             # channels last (what an SSD head is joined into)
            h, c = np.indices(rest)
            layouts.append(c + pad * h)
    if len(rest) == 1 and height == 1 and width == 1 and fits(rest[0]):
        layouts.append(np.arange(rest[0]))
    if len(layouts) != 1:
        raise TailError("cannot tell how the %s values of the cut are laid out in the NPU output %s %s"
                        % ("x".join(str(s) for s in shape), tensor.name, tensor.dims))
    return layouts[0].reshape(shape).astype(np.int64)


def dequantized(tensor, raw):
    """The float values of a dumped NPU tensor (raw bytes) in the board's own arithmetic: (q + zp) / scale."""
    q = np.frombuffer(raw, dtype=np.int8).astype(FLOAT)
    return ((q + FLOAT(tensor.zero_point)) / FLOAT(tensor.scale)).astype(FLOAT)


# ------------------------------------------------------------------------------------------- C generation

class View(object):
    """Where every element of a tensor comes from at run time: a source, an offset in it and, for NPU bytes,
    the table that turns the byte into the value (-1 for a float source)."""
    __slots__ = ("src", "off", "lut")

    def __init__(self, src, off, lut):
        self.src, self.off, self.lut = src, off, lut

    @property
    def shape(self):
        return self.src.shape

    @property
    def size(self):
        return int(self.src.size)


def _literal(value):
    value = float(value)
    if value != value:
        return "(0.0f / 0.0f)"
    if value in (float("inf"), float("-inf")):
        return "(%s1.0f / 0.0f)" % ("-" if value < 0 else "")
    text = "%.9g" % value
    if "." not in text and "e" not in text and "n" not in text:
        text += ".0"
    return text + "f"


def _array(values, per_line=8):
    values = np.asarray(values, dtype=FLOAT).reshape(-1)
    lines = []
    for i in range(0, len(values), per_line):
        lines.append("    " + ", ".join(_literal(v) for v in values[i:i + per_line]) + ",")
    return "\n".join(lines)


def decompose(src, off, lut, dst_base=0, dst_strides=None, axes=None):
    """Splits a view into blocks that are plain loops: inside a block every element comes from the same source
    through the same table, and source offset and destination index both step by constants along every axis.
    Returns [{"sid", "lut", "src", "dst", "dims": [(count, source step, destination step, axis)]}].
    dst_strides says where an element goes (row-major by default); axes names the axes for the caller."""
    if dst_strides is None:
        dst_strides = []
        step = 1
        for n in reversed(src.shape):
            dst_strides.insert(0, step)
            step *= n
    if axes is None:
        axes = list(range(src.ndim))
    if src.ndim == 0:
        return [{"sid": int(src), "lut": int(lut), "src": int(off), "dst": int(dst_base), "dims": []}]
    count = src.shape[0]
    if count == 1:
        return decompose(src[0], off[0], lut[0], dst_base, dst_strides[1:], axes[1:])
    if src.ndim == 1:
        # Runs of "same source, same table, same step", found from the places where one of them changes
        # (one Python step per run, not per element). joined[j]: elements j and j+1 may share a run.
        step = np.diff(off)
        joined = (src[1:] == src[:-1]) & (lut[1:] == lut[:-1])
        stops = np.ones(count - 1, dtype=bool)          # stops[j]: a run that reached element j ends there
        stops[1:] = ~joined[1:] | (step[1:] != step[:-1])
        stops[0] = not joined[0]
        stop_at = np.flatnonzero(stops)
        blocks = []
        pos = 0
        while pos < count:
            if pos == count - 1 or not joined[pos]:
                end = pos + 1
            else:
                k = np.searchsorted(stop_at, pos, side="right")
                end = int(stop_at[k]) + 1 if k < len(stop_at) else count
            dims = [] if end - pos == 1 else [(end - pos, int(step[pos]), dst_strides[0], axes[0])]
            blocks.append({"sid": int(src[pos]), "lut": int(lut[pos]), "src": int(off[pos]),
                           "dst": int(dst_base + pos * dst_strides[0]), "dims": dims})
            pos = end
        return blocks
    blocks = []
    start = 0
    while start < count:
        end = start + 1
        step = None
        while end < count:
            if not (np.array_equal(src[end], src[start]) and np.array_equal(lut[end], lut[start])):
                break
            delta = off[end] - off[end - 1]
            first = int(delta.flat[0])
            if not (delta == first).all() or (step is not None and first != step):
                break
            step = first
            end += 1
        inner = decompose(src[start], off[start], lut[start], dst_base + start * dst_strides[0], dst_strides[1:], axes[1:])
        if end - start > 1:
            for b in inner:
                b["dims"].insert(0, (end - start, step, dst_strides[0], axes[0]))
        blocks.extend(inner)
        start = end
    return blocks


def rebuild(blocks, size):
    """The view the blocks describe, as flat arrays over the destination: used to check a decomposition before
    any code is written from it."""
    src = np.full(size, -9, dtype=np.int64)
    off = np.zeros(size, dtype=np.int64)
    lut = np.zeros(size, dtype=np.int64)
    for b in blocks:
        s = np.array([b["src"]], dtype=np.int64)
        d = np.array([b["dst"]], dtype=np.int64)
        for count, sstep, dstep, _ in b["dims"]:
            k = np.arange(count, dtype=np.int64)
            s = (s[:, None] + k[None, :] * sstep).reshape(-1)
            d = (d[:, None] + k[None, :] * dstep).reshape(-1)
        src[d] = b["sid"]
        off[d] = s
        lut[d] = b["lut"]
    return src, off, lut


def merged(dims):
    """Loops that walk source and destination as one stretch become one loop (and a copy can become memcpy)."""
    out = []
    for dim in dims:
        if out:
            n0, s0, d0, _ = out[-1]
            n1, s1, d1, a1 = dim
            if s0 == n1 * s1 and d0 == n1 * d1:
                out[-1] = (n0 * n1, s1, d1, a1)
                continue
        out.append(dim)
    return out


def _term(name, step):
    return name if step == 1 else "%s * %d" % (name, step)


def row_major(shape):
    strides, step = [], 1
    for n in reversed(shape):
        strides.insert(0, step)
        step *= n
    return strides


class TailGenerator(object):
    """Walks the tail once and writes the C that computes it."""

    def __init__(self, tail, pairs, title="tail"):
        self.tail, self.title = tail, title
        self.sources = []       # {"kind": "npu" | "buf" | "const", "c": C name, ...}
        self.luts = []          # float32[256] tables, index = byte value + 128
        self._lut_ids = {}
        self._composed = {}
        self.body = []          # statements of the compute function
        self.buffers = []       # (C name, element count)
        self.constants = []     # (C name, float32 array)
        self.values = {}        # tensor name -> numpy array (constant) | View
        self.unsupported = []   # (operator name, type, why)
        self.stats = {"gathered": 0, "float_ops": 0, "tables": 0, "buffers": 0, "buffer_bytes": 0}
        self.inputs = []
        self.readers = {}       # tensor name -> the operators that read it
        for node in tail.nodes:
            for i in node.inputs:
                if i:
                    self.readers.setdefault(i, []).append(node)
        self._output_names = set(name for name, _ in tail.outputs)
        self._skip = {}         # operators already written together with the one in front of them
        self._fused = {}        # their results
        for (name, shape, cut), tensor in pairs:
            sid = self._source({"kind": "npu", "c": "in%d" % len(self.inputs), "tensor": tensor})
            table = ((np.arange(-128, 128, dtype=FLOAT) + FLOAT(tensor.zero_point)) / FLOAT(tensor.scale)).astype(FLOAT)
            off = npu_offsets(shape, tensor)
            self.values[name] = View(np.full(shape, sid, dtype=np.int64), off,
                                     np.full(shape, self._table(table), dtype=np.int64))
            self.inputs.append({"tensor": name, "cut": cut, "shape": list(shape), "npu": tensor})
        for name, value in tail.consts.items():
            self.values[name] = np.asarray(value)

    # ---- bookkeeping
    def _source(self, source):
        self.sources.append(source)
        return len(self.sources) - 1

    def _table(self, values):
        values = np.asarray(values, dtype=FLOAT)
        key = values.tobytes()
        if key not in self._lut_ids:
            self._lut_ids[key] = len(self.luts)
            self.luts.append(values)
        return self._lut_ids[key]

    def _buffer(self, size):
        name = "t%d" % len(self.buffers)
        self.buffers.append((name, int(size)))
        return name, self._source({"kind": "buf", "c": name})

    def _const_source(self, array):
        array = np.ascontiguousarray(np.asarray(array, dtype=FLOAT))
        name = "k%d" % len(self.constants)
        self.constants.append((name, array.reshape(-1)))
        return self._source({"kind": "const", "c": name})

    def _is_const(self, value):
        return isinstance(value, np.ndarray) or np.isscalar(value)

    def _view(self, value):
        """Any value as a view; a constant becomes a table in the C file."""
        if isinstance(value, View):
            return value
        array = np.asarray(value)
        if not np.issubdtype(array.dtype, np.floating):
            array = array.astype(FLOAT)
        sid = self._const_source(array)
        shape = array.shape
        return View(np.full(shape, sid, dtype=np.int64), np.arange(array.size, dtype=np.int64).reshape(shape),
                    np.full(shape, -1, dtype=np.int64))

    def _from_bytes_only(self, view):
        return bool((view.lut >= 0).all())

    def _pointer(self, view):
        """A C expression for the view's values as one contiguous float array, or None."""
        if view.size == 0:
            return None
        sid = int(view.src.flat[0])
        if self.sources[sid]["kind"] == "npu" or not (view.src == sid).all():
            return None
        off = view.off.reshape(-1)
        if not (off == off[0] + np.arange(off.size, dtype=np.int64)).all():
            return None
        return self.sources[sid]["c"] if off[0] == 0 else "(%s + %d)" % (self.sources[sid]["c"], off[0])

    def _materialize(self, view, fresh=False):
        """The view's values as a contiguous float array: (C expression, view of that array). A view that already
        is one is used as it is unless a fresh, writable copy is asked for."""
        if not fresh:
            pointer = self._pointer(view)
            if pointer is not None:
                return pointer, view
        name, sid = self._buffer(view.size)
        self._gather(view, name)
        shape = view.shape
        return name, View(np.full(shape, sid, dtype=np.int64), np.arange(view.size, dtype=np.int64).reshape(shape),
                          np.full(shape, -1, dtype=np.int64))

    def _checked(self, blocks, expect_src, expect_off, expect_lut):
        src, off, lut = rebuild(blocks, expect_src.size)
        if not (np.array_equal(src, expect_src.reshape(-1)) and np.array_equal(off, expect_off.reshape(-1))
                and np.array_equal(lut, expect_lut.reshape(-1))):
            raise TailError("internal: a value layout could not be turned into loops")

    def _gather(self, view, dst):
        blocks = decompose(view.src, view.off, view.lut)
        self._checked(blocks, view.src, view.off, view.lut)
        self.body.append("    /* %s <- %d values in %d block%s */" % (dst, view.size, len(blocks), "" if len(blocks) == 1 else "s"))
        for b in blocks:
            source = self.sources[b["sid"]]
            is_bytes = source["kind"] == "npu"
            table = "lut%d" % b["lut"]
            dims = merged([d for d in b["dims"] if d[0] > 1])
            # the innermost loop walks the source in the order its values lie in memory ...
            dims.sort(key=lambda d: -abs(d[1]))
            total = int(np.prod([d[0] for d in dims], dtype=np.int64)) if dims else 1
            self.stats["gathered"] += total if is_bytes else 0
            lines = ["    {", "        %s *s = %s + %d;" % ("const uint8_t" if is_bytes else "const float", source["c"], b["src"]),
                     "        float *d = %s + %d;" % (dst, b["dst"])]
            if not dims:
                lines.append("        d[0] = %s;" % ("%s[s[0]]" % table if is_bytes else "s[0]"))
            elif not is_bytes and len(dims) == 1 and dims[0][1] == 1 and dims[0][2] == 1:
                lines.append("        memcpy(d, s, sizeof(float) * %d);" % dims[0][0])
            elif len(dims) == 2 and abs(dims[1][2]) >= 1024 and abs(dims[0][2]) <= 16 and dims[0][0] >= 64:
                # ... unless that scatters the writes over as many places as the loop is long (a transpose: values
                # that lie cell by cell have to lie channel by channel). Then the destination is written in
                # order, a tile of the source at a time, small enough to stay in the first-level cache while it
                # is read once per inner loop. On the board, for 512,000 values: 5.4 ms in source order, 3.8 ms
                # in tiles, 1.7 ms in tiles with the lookups eight at a time.
                (n_x, s_x, d_x, _), (n_z, s_z, d_z, _) = dims
                tile = max(16, min(n_x, (8192 // max(1, abs(s_x) * (1 if is_bytes else 4))) & ~7))
                lines.extend([
                    "        for (int t = 0; t < %d; t += %d) {" % (n_x, tile),
                    "            const int n = t + %d < %d ? %d : %d - t;" % (tile, n_x, tile, n_x),
                    "            for (int i1 = 0; i1 < %d; i1++)" % n_z])
                s_at = "s + %s + %s" % (_term("t", s_x), _term("i1", s_z))
                d_at = "d + %s + %s" % (_term("t", d_x), _term("i1", d_z))
                if is_bytes:
                    lines.append("                tail_gather(%s, %d, %s, %d, %s, n);" % (s_at, s_x, d_at, d_x, table))
                else:
                    lines.extend(["                for (int i0 = 0; i0 < n; i0++)",
                                  "                    (%s)[%s] = (%s)[%s];" % (d_at, _term("i0", d_x), s_at, _term("i0", s_x))])
                lines.append("        }")
            else:
                inner_n, inner_s, inner_d, _ = dims[-1]
                s_index, d_index = [], []
                for depth, (count, sstep, dstep, _) in enumerate(dims[:-1]):
                    lines.append("        %sfor (int i%d = 0; i%d < %d; i%d++)" % ("    " * depth, depth, depth, count, depth))
                    s_index.append(_term("i%d" % depth, sstep))
                    d_index.append(_term("i%d" % depth, dstep))
                pad = "        " + "    " * (len(dims) - 1)
                if is_bytes:
                    lines.append("%stail_gather(%s, %d, %s, %d, %s, %d);" % (pad, " + ".join(["s"] + s_index), inner_s,
                                                                         " + ".join(["d"] + d_index), inner_d, table, inner_n))
                else:
                    k = "i%d" % (len(dims) - 1)
                    lines.append("%sfor (int %s = 0; %s < %d; %s++)" % (pad, k, k, inner_n, k))
                    lines.append("%s    d[%s] = s[%s];" % (pad, " + ".join(d_index + [_term(k, inner_d)]),
                                                         " + ".join(s_index + [_term(k, inner_s)])))
            lines.append("    }")
            self.body.extend(lines)

    def _compose(self, view, key, function):
        """A one-input function applied to values that are still bytes: only the tables change."""
        lut = view.lut.copy()
        for old in np.unique(view.lut):
            old = int(old)
            if (old, key) not in self._composed:
                self._composed[(old, key)] = self._table(function(self.luts[old]))
            lut[view.lut == old] = self._composed[(old, key)]
        return View(view.src, view.off, lut)

    # ---- the walk
    def run(self):
        for node in self.tail.nodes:
            if id(node) in self._skip:
                self.values[node.outputs[0]] = self._fused[node.outputs[0]]
                continue
            inputs = [self.values.get(i) if i else None for i in node.inputs]
            try:
                if all(v is None or self._is_const(v) for v in inputs):
                    outputs = run_node(node.op, node.attrs, inputs, self.tail.opset, len(node.outputs))
                else:
                    outputs = self._node(node, inputs)
            except TailError as error:
                self.unsupported.append((node.name, node.op, str(error)))
                return False
            for name, value in zip(node.outputs, outputs):
                if name:
                    self.values[name] = value
        self.outputs = []
        for name, original in self.tail.outputs:
            value = self.values[name]
            pointer, view = self._materialize(self._view(value))
            self.outputs.append({"name": original, "c": pointer, "shape": list(view.shape)})
        return True

    def _node(self, node, inputs):
        op, attrs, opset = node.op, node.attrs, self.tail.opset
        if op in MOVES:
            data = range(len(inputs)) if MOVES[op] is None else MOVES[op]
            for i, v in enumerate(inputs):
                if i not in data and isinstance(v, View):
                    raise TailError("%s takes its sizes or positions from values computed at run time" % op)
            views = {i: self._view(inputs[i]) for i in data}
            parts = []
            for field in ("src", "off", "lut"):
                moved = [getattr(views[i], field) if i in views else v for i, v in enumerate(inputs)]
                parts.append(run_node(op, attrs, moved, opset, len(node.outputs)))
            return [View(np.ascontiguousarray(s), np.ascontiguousarray(o), np.ascontiguousarray(l))
                    for s, o, l in zip(*parts)]
        if op in UNARY:
            extra = _clip_bounds(attrs, inputs, opset) if op == "Clip" else ()
            view = self._view(inputs[0])
            if self._from_bytes_only(view):
                key = (op, tuple(sorted((k, float(v)) for k, v in attrs.items() if np.isscalar(v))), extra)
                return [self._compose(view, key, lambda t: unary(op, attrs, t, extra))]
            return [self._unary_loop(op, attrs, extra, view)]
        if op in BINARY:
            return [self._binary(op, inputs[0], inputs[1])]
        if op == "Cast":
            if int(attrs.get("to", 1)) != 1:
                raise TailError("Cast to a type other than float is not supported behind a cut")
            return [inputs[0]]
        if op == "Softmax":
            return [self._softmax(node, attrs, self._view(inputs[0]))]
        if op == "Conv":
            return [self._conv(attrs, inputs)]
        raise TailError("operator %s cannot be turned into board code yet" % op)

    def _unary_loop(self, op, attrs, extra, view):
        pointer, view = self._materialize(view)
        name, sid = self._buffer(view.size)
        x = "x[i]"
        if op == "Sigmoid":
            expr = "1.0f / (1.0f + tg_expf(-%s))" % x
        elif op == "Exp":
            expr = "tg_expf(%s)" % x
        elif op == "Tanh":
            expr = "tg_tanhf(%s)" % x
        elif op == "Relu":
            expr = "%s > 0.0f ? %s : 0.0f" % (x, x)
        elif op == "LeakyRelu":
            expr = "%s >= 0.0f ? %s : %s * %s" % (x, x, x, _literal(attrs.get("alpha", 0.01)))
        elif op == "Neg":
            expr = "-%s" % x
        elif op == "Abs":
            expr = "%s < 0.0f ? -%s : %s" % (x, x, x)
        elif op == "Sqrt":
            expr = "__builtin_sqrtf(%s)" % x
        elif op == "Reciprocal":
            expr = "1.0f / %s" % x
        elif op == "Clip":
            lo, hi = extra
            expr = x
            if lo is not None:
                expr = "(%s < %s ? %s : %s)" % (expr, _literal(lo), _literal(lo), expr)
            if hi is not None:
                expr = "(%s > %s ? %s : %s)" % (expr, _literal(hi), _literal(hi), expr)
        else:
            raise TailError("operator %s on values that are already floats cannot be turned into board code yet" % op)
        self.stats["float_ops"] += view.size
        self.body.extend(["    {   /* %s */" % op, "        const float *x = %s;" % pointer,
                          "        for (int i = 0; i < %d; i++)" % view.size, "            %s[i] = %s;" % (name, expr), "    }"])
        shape = view.shape
        return View(np.full(shape, sid, dtype=np.int64), np.arange(view.size, dtype=np.int64).reshape(shape),
                    np.full(shape, -1, dtype=np.int64))

    def _binary(self, op, a, b):
        const_a, const_b = self._is_const(a), self._is_const(b)
        # a single number on one side and bytes on the other: still only a table
        for value, other, swapped in ((a, b, False), (b, a, True)):
            if self._is_const(other) and isinstance(value, View) and self._from_bytes_only(value):
                flat = np.asarray(other, dtype=FLOAT).reshape(-1)
                if flat.size and (flat == flat[0]).all() and np.broadcast_shapes(value.shape, np.shape(other)) == value.shape:
                    c = FLOAT(flat[0])
                    key = (op, float(c), swapped)
                    function = (lambda t: binary(op, c, t).astype(FLOAT)) if swapped else (lambda t: binary(op, t, c).astype(FLOAT))
                    return self._compose(value, key, function)
        shape_a = np.shape(a) if const_a else a.shape
        shape_b = np.shape(b) if const_b else b.shape
        shape = np.broadcast_shapes(shape_a, shape_b)
        operands = []
        for value, is_const, own in ((a, const_a, shape_a), (b, const_b, shape_b)):
            if is_const:
                flat = np.asarray(value, dtype=FLOAT).reshape(-1)
                if flat.size == 1 or (flat == flat[0]).all():
                    operands.append((_literal(flat[0]), None))
                    continue
                pointer = self.sources[self._const_source(np.asarray(value, dtype=FLOAT))]["c"]
            else:
                pointer, _ = self._materialize(value)
            operands.append((pointer, own))
        size = int(np.prod(shape, dtype=np.int64))
        name, sid = self._buffer(size)
        symbol = {"Add": "+", "Sub": "-", "Mul": "*", "Div": "/"}.get(op)
        self.stats["float_ops"] += size

        def strides(own):
            own = (1,) * (len(shape) - len(own)) + tuple(own)
            out, step = [], 1
            for n in reversed(own):
                out.insert(0, step)
                step *= n
            return [0 if own[i] == 1 and shape[i] != 1 else out[i] for i in range(len(shape))]

        loops = [(i, n) for i, n in enumerate(shape) if n > 1]
        out_strides = strides(shape)
        # axes that every operand steps through in the same way collapse into one loop
        same = all(own is None or strides(own) == out_strides for _, own in operands)
        lines = ["    {   /* %s */" % op]
        if same:
            index = lambda own: "[i]"
            lines.append("        for (int i = 0; i < %d; i++)" % size)
            target = "%s[i]" % name
            indent = "            "
        else:
            for depth, (axis, n) in enumerate(loops):
                lines.append("        %sfor (int i%d = 0; i%d < %d; i%d++)" % ("    " * depth, depth, depth, n, depth))

            def index(own):
                st = strides(own)
                terms = ["i%d" % depth if st[axis] == 1 else "i%d * %d" % (depth, st[axis])
                         for depth, (axis, n) in enumerate(loops) if st[axis]]
                return "[%s]" % (" + ".join(terms) or "0")
            target = "%s%s" % (name, index(shape))
            indent = "        " + "    " * len(loops)
        x = operands[0][0] + ("" if operands[0][1] is None else index(operands[0][1]))
        y = operands[1][0] + ("" if operands[1][1] is None else index(operands[1][1]))
        if symbol:
            expr = "%s %s %s" % (x, symbol, y)
        elif op == "Max":
            expr = "%s > %s ? %s : %s" % (x, y, x, y)
        elif op == "Min":
            expr = "%s < %s ? %s : %s" % (x, y, x, y)
        else:
            raise TailError("operator %s on values that are already floats cannot be turned into board code yet" % op)
        lines.append("%s%s = %s;" % (indent, target, expr))
        lines.append("    }")
        self.body.extend(lines)
        return View(np.full(shape, sid, dtype=np.int64), np.arange(size, dtype=np.int64).reshape(shape),
                    np.full(shape, -1, dtype=np.int64))

    def _softmax(self, node, attrs, view):
        shape = view.shape
        axis = softmax_axis(attrs, self.tail.opset, len(shape))
        if axis is None:
            raise TailError("Softmax over several axes at once (an old model format) is not supported")
        count = shape[axis]
        inner = int(np.prod(shape[axis + 1:], dtype=np.int64))
        outer = int(np.prod(shape[:axis], dtype=np.int64))
        if self._from_bytes_only(view) and 1 < count <= 1024:
            # exp() of a byte is one of 256 numbers; safe as long as a whole group of them cannot overflow
            # (and the smallest of them is not rounded to 0, so that no group can add up to 0)
            largest = max(float(np.exp(np.float64(self.luts[int(i)].max()))) for i in np.unique(view.lut))
            smallest = min(float(np.exp(np.float64(self.luts[int(i)].min()))) for i in np.unique(view.lut))
            if largest * count < 1e37 and smallest > 1e-30:
                done = self._softmax_from_bytes(node, self._compose(view, ("Exp", (), ()), lambda t: unary("Exp", {}, t)), axis)
                if done is not None:
                    return done
        pointer, _ = self._materialize(view)
        name, sid = self._buffer(view.size)
        out = View(np.full(shape, sid, dtype=np.int64), np.arange(view.size, dtype=np.int64).reshape(shape),
                   np.full(shape, -1, dtype=np.int64))
        scratch = "sum%d" % len(self.buffers)
        self.buffers.append((scratch, inner))
        self.stats["float_ops"] += 2 * view.size
        # exp(x - largest of the group) never overflows; then every group is divided by its sum
        self.body.extend([
            "    {   /* Softmax */", "        const float *x = %s;" % pointer,
            "        for (int o = 0; o < %d; o++) {" % outer,
            "            const float *g = x + o * %d;" % (count * inner),
            "            float *e = %s + o * %d;" % (name, count * inner),
            "            for (int i = 0; i < %d; i++) {" % inner,
            "                float top = g[i];",
            "                for (int k = 1; k < %d; k++) if (g[k * %d + i] > top) top = g[k * %d + i];" % (count, inner, inner),
            "                for (int k = 0; k < %d; k++) e[k * %d + i] = tg_expf(g[k * %d + i] - top);" % (count, inner, inner),
            "            }",
            "            for (int i = 0; i < %d; i++) %s[i] = 0.0f;" % (inner, scratch),
            "            for (int k = 0; k < %d; k++)" % count,
            "                for (int i = 0; i < %d; i++) %s[i] += e[k * %d + i];" % (inner, scratch, inner),
            "            for (int i = 0; i < %d; i++) %s[i] = 1.0f / %s[i];" % (inner, scratch, scratch),
            "            for (int k = 0; k < %d; k++)" % count,
            "                for (int i = 0; i < %d; i++) e[k * %d + i] *= %s[i];" % (inner, inner, scratch),
            "        }", "    }"])
        return out

    def _pointwise_reader(self, node, axis, shape):
        """The 1x1 convolution that is the only reader of this Softmax and sums over the softmax axis, or None."""
        if axis != 1 or len(shape) != 4 or shape[0] != 1 or node.outputs[0] in self._output_names:
            return None
        readers = self.readers.get(node.outputs[0], [])
        if len(readers) != 1 or readers[0].op != "Conv" or readers[0].inputs[0] != node.outputs[0]:
            return None
        conv = readers[0]
        weights = self.values.get(conv.inputs[1])
        bias = self.values.get(conv.inputs[2]) if len(conv.inputs) > 2 and conv.inputs[2] else None
        if not self._is_const(weights) or (bias is not None and not self._is_const(bias)):
            return None
        weights = np.asarray(weights, dtype=FLOAT)
        if not conv_is_pointwise(conv.attrs, weights) or weights.shape[1] != shape[1] or weights.shape[0] > 16:
            return None
        return conv, weights.reshape(weights.shape[0], weights.shape[1]), None if bias is None else np.asarray(bias, dtype=FLOAT).reshape(-1)

    def _softmax_from_bytes(self, node, exps, axis):
        """Softmax of values that are still NPU bytes, one group at a time in the order the bytes lie in memory:
        look up exp() for the group, sum, divide. With a 1x1 convolution behind it (YOLO's DFL: the expected
        position of a distribution) the weighted sum is taken in the same pass and nothing else is stored."""
        shape = exps.shape
        count = shape[axis]
        moved = [np.ascontiguousarray(np.moveaxis(a, axis, -1)) for a in (exps.src, exps.off, exps.lut)]
        order = [i for i in range(len(shape)) if i != axis] + [axis]
        reader = self._pointwise_reader(node, axis, shape)
        if reader is not None:
            # destination index while decomposing: position * count + k (unique, so it can be checked)
            strides = row_major(moved[0].shape)
        else:
            strides = [row_major(shape)[i] for i in order]
        blocks = decompose(moved[0], moved[1], moved[2], 0, strides, order)
        if reader is not None:
            self._checked(blocks, moved[0], moved[1], moved[2])
        else:
            self._checked(blocks, exps.src, exps.off, exps.lut)
        for b in blocks:
            if not b["dims"] or b["dims"][-1][3] != axis or b["dims"][-1][0] != count:
                return None                 # a group is not one stretch of bytes: the general way handles it
            if reader is not None and (b["dst"] % count or any(d[2] % count for d in b["dims"][:-1])):
                return None
        self.stats["gathered"] += exps.size
        self.stats["float_ops"] += 2 * exps.size
        if reader is not None:
            conv, weights, bias = reader
            out_channels = weights.shape[0]
            plane = shape[2] * shape[3]
            name, sid = self._buffer(out_channels * plane)
            w = self.sources[self._const_source(weights.reshape(-1))]["c"]
            bias_name = None if bias is None or out_channels == 1 else self.sources[self._const_source(bias)]["c"]
            self.body.append("    /* Softmax over %d values and the 1x1 convolution behind it (%d output channel%s), in one pass */"
                             % (count, out_channels, "" if out_channels == 1 else "s"))
        else:
            name, sid = self._buffer(exps.size)
            self.body.append("    /* Softmax over %d values, straight from the NPU bytes */" % count)
        for b in blocks:
            source = self.sources[b["sid"]]
            _, k_src, k_dst, _ = b["dims"][-1]
            outer = merged([d for d in b["dims"][:-1] if d[0] > 1])
            outer.sort(key=lambda d: -abs(d[1]))
            lines = ["    {", "        const uint8_t *s = %s + %d;" % (source["c"], b["src"])]
            pad = "        "
            for depth, (n, sstep, dstep, _) in enumerate(outer):
                lines.append("%sfor (int i%d = 0; i%d < %d; i%d++)%s" % (pad + "    " * depth, depth, depth, n, depth,
                                                                       " {" if depth == len(outer) - 1 else ""))
            if not outer:
                lines.append(pad + "{")
            inner = pad + "    " * max(1, len(outer))
            s_at = " + ".join(["s"] + [_term("i%d" % depth, d[1]) for depth, d in enumerate(outer)])
            lines.append("%sfloat e[%d];" % (inner, count))
            lines.append("%stail_gather(%s, %d, e, 1, lut%d, %d);" % (inner, s_at, k_src, b["lut"], count))
            if reader is not None:
                # position = (destination index while decomposing) / count, divided here and not on the board
                at = " + ".join([str(b["dst"] // count)] + [_term("i%d" % depth, d[2] // count) for depth, d in enumerate(outer)])
                lines.append("%sconst float sum = tail_sum(e, %d);" % (inner, count))
                if out_channels == 1:
                    lines.append("%s%s[%s] = tail_dot(e, %s, %d) / sum%s;" % (inner, name, at, w, count,
                                                                              "" if bias is None else " + " + _literal(bias[0])))
                else:
                    lines.append("%sconst int at = %s;" % (inner, at))
                    lines.append("%sfor (int oc = 0; oc < %d; oc++)" % (inner, out_channels))
                    lines.append("%s    %s[oc * %d + at] = tail_dot(e, %s + oc * %d, %d) / sum%s;"
                                 % (inner, name, plane, w, count, count, "" if bias is None else " + %s[oc]" % bias_name))
            else:
                d_at = " + ".join(["%s + %d" % (name, b["dst"])] + [_term("i%d" % depth, d[2]) for depth, d in enumerate(outer)])
                lines.append("%sfloat *d = %s;" % (inner, d_at))
                lines.append("%sconst float r = 1.0f / tail_sum(e, %d);" % (inner, count))
                lines.append("%sfor (int k = 0; k < %d; k++) d[%s] = e[k] * r;" % (inner, count, _term("k", k_dst)))
            lines.append(pad + "    " * max(0, len(outer) - 1) + "}")
            lines.append("    }")
            self.body.extend(lines)
        if reader is not None:
            self._skip[id(conv)] = True
            out_shape = (1, out_channels, shape[2], shape[3])
            self._fused[conv.outputs[0]] = View(np.full(out_shape, sid, dtype=np.int64),
                                                np.arange(out_channels * plane, dtype=np.int64).reshape(out_shape),
                                                np.full(out_shape, -1, dtype=np.int64))
            # only that convolution reads the Softmax, so its own values are never made (source -2: nothing)
            return View(np.full(shape, -2, dtype=np.int64), np.zeros(shape, dtype=np.int64), np.full(shape, -1, dtype=np.int64))
        return View(np.full(shape, sid, dtype=np.int64), np.arange(exps.size, dtype=np.int64).reshape(shape),
                    np.full(shape, -1, dtype=np.int64))

    def _conv(self, attrs, inputs):
        weights = np.asarray(inputs[1], dtype=FLOAT) if self._is_const(inputs[1]) else None
        bias = inputs[2] if len(inputs) > 2 else None
        if weights is None or (bias is not None and not self._is_const(bias)):
            raise TailError("a convolution whose weights are computed at run time is not supported")
        if not conv_is_pointwise(attrs, weights):
            raise TailError("only 1x1 convolutions without stride, padding or groups are supported behind a cut")
        pointer, view = self._materialize(self._view(inputs[0]))
        if len(view.shape) != 4 or view.shape[0] != 1 or view.shape[1] != weights.shape[1]:
            raise TailError("the convolution's input does not have the size its weights expect")
        out_channels, in_channels = weights.shape[0], weights.shape[1]
        plane = view.shape[2] * view.shape[3]
        shape = (1, out_channels, view.shape[2], view.shape[3])
        name, sid = self._buffer(out_channels * plane)
        w = self.sources[self._const_source(weights.reshape(-1))]["c"]
        self.stats["float_ops"] += out_channels * in_channels * plane
        lines = ["    {   /* Conv 1x1: %d -> %d channels */" % (in_channels, out_channels), "        const float *x = %s;" % pointer,
                 "        for (int oc = 0; oc < %d; oc++) {" % out_channels, "            float *o = %s + oc * %d;" % (name, plane)]
        if bias is None:
            lines.append("            for (int p = 0; p < %d; p++) o[p] = 0.0f;" % plane)
        else:
            b = self.sources[self._const_source(np.asarray(bias, dtype=FLOAT).reshape(-1))]["c"]
            lines.append("            for (int p = 0; p < %d; p++) o[p] = %s[oc];" % (plane, b))
        lines.extend(["            for (int ic = 0; ic < %d; ic++) {" % in_channels,
                      "                const float wv = %s[oc * %d + ic];" % (w, in_channels),
                      "                const float *xi = x + ic * %d;" % plane,
                      "                for (int p = 0; p < %d; p++) o[p] += wv * xi[p];" % plane,
                      "            }", "        }", "    }"])
        self.body.extend(lines)
        return View(np.full(shape, sid, dtype=np.int64), np.arange(out_channels * plane, dtype=np.int64).reshape(shape),
                    np.full(shape, -1, dtype=np.int64))

    # ---- the file
    def source(self, extra=""):
        """The whole C file. `extra` is appended before the entry point (stage 다: turning outputs into boxes)."""
        used = sorted(set(int(i) for i in re.findall(r"\blut(\d+)\b", "\n".join(self.body))))
        self.stats["tables"] = len(used)
        self.stats["buffers"] = len(self.buffers)
        self.stats["buffer_bytes"] = 4 * sum(size for _, size in self.buffers)
        n = len(self.inputs)
        out = []
        out.append(HEADER % {"title": self.title, "cuts": ", ".join(i["cut"] for i in self.inputs),
                             "outputs": ", ".join("%s %s" % (o["name"], o["shape"]) for o in self.outputs),
                             "ops": len(self.tail.nodes), "gathered": self.stats["gathered"], "tables": len(used)})
        for i in used:
            # in the file a table is indexed by the byte read as 0..255; here by the value -128..127 (+128)
            out.append("static const float lut%d[256] = {\n%s\n};\n" % (i, _array(np.roll(self.luts[i], 128))))
        for name, values in self.constants:
            out.append("static const float %s[%d] = {\n%s\n};\n" % (name, len(values), _array(values)))
        for name, size in self.buffers:
            out.append("static float %s[%d];" % (name, size))
        out.append("")
        out.append("static void tail_compute(const uint8_t *const in[%d])\n{" % n)
        for i in range(n):
            out.append("    const uint8_t *in%d = in[%d];" % (i, i))
        out.extend(self.body)
        out.append("}\n")
        out.append("static const tail_input_t tail_inputs[%d] = {" % n)
        for i in self.inputs:
            t = i["npu"]
            out.append("    { \"%s\", %d, %d, %d, %d, %s },   /* cut at %s */"
                       % (t.name, t.dims[1], t.dims[2], t.dims[3], t.size, _literal(t.scale), i["cut"]))
        out.append("};\n")
        out.append("static const tail_output_t tail_outputs[%d] = {" % len(self.outputs))
        for o in self.outputs:
            dims = o["shape"] + [0] * (6 - len(o["shape"]))
            out.append("    { \"%s\", %s, %d, %d, { %s } }," % (o["name"], o["c"], int(np.prod(o["shape"], dtype=np.int64)),
                                                              len(o["shape"]), ", ".join(str(d) for d in dims)))
        out.append("};\n")
        out.append(FOOTER % {"inputs": n, "outputs": len(self.outputs), "extra": extra})
        return "\n".join(out)


HEADER = """/*
 * TOPST Toolkit - generated code for the part of the model the NPU does not run (%(title)s).
 * Do not edit: it is written again at every build (engine/helpers/gen_tail.py, docs/design.md section 5.8).
 *
 * The model was cut at: %(cuts)s
 * This file computes, from the NPU's output bytes, what the %(ops)d operators behind the cut would have produced:
 *     %(outputs)s
 * %(gathered)d values are read from the NPU output through %(tables)d tables of 256 numbers (an 8-bit value can only be
 * one of 256 numbers, so sigmoid, exp, scaling and the like cost one lookup); the rest is plain float arithmetic.
 *
 * It takes the place of the compiler's post_process.c in net.so. That file is included unchanged and stays in the
 * library as run_post_process_reference().
 *
 * For a program on the board:
 *     int topst_tail_output_count(void);
 *     const float *topst_tail_output(int index, const char **name, int *rank, const int **dims);
 * The arrays are valid after run_post_process() until its next call.
 *
 * On the board:  TOPST_POST_PROCESS=reference  runs the toolkit's code instead,  =quiet  prints nothing per frame.
 */
#define run_post_process run_post_process_reference
#include "post_process.c"
#undef run_post_process

#include <stdint.h>
#include <stdlib.h>
#include <string.h>

typedef struct { const char *name; int channels, height, width, size; float scale; } tail_input_t;
typedef struct { const char *name; const float *data; int size, rank; int dims[6]; } tail_output_t;

/* exp() without the maths library (net.so links against the C library only). Relative error below 2e-7. */
static inline float tg_expf(float x)
{
    if (x > 88.0f) return 3.4028235e38f;
    if (x < -87.0f) return 0.0f;
    const float t = x * 1.44269504f;
    const int n = (int)(t + (t < 0.0f ? -0.5f : 0.5f));
    float r = x - (float)n * 0.693359375f;
    r -= (float)n * -2.12194440e-4f;
    float p = 1.9875691500e-4f;
    p = p * r + 1.3981999507e-3f;
    p = p * r + 8.3334519073e-3f;
    p = p * r + 4.1665795894e-2f;
    p = p * r + 1.6666665459e-1f;
    p = p * r + 5.0000001201e-1f;
    p = p * r * r + r + 1.0f;
    union { float f; int32_t i; } scale;
    scale.i = (n + 127) << 23;
    return p * scale.f;
}

static inline float tg_tanhf(float x)
{
    if (x > 9.0f) return 1.0f;
    if (x < -9.0f) return -1.0f;
    const float e = tg_expf(2.0f * x);
    return (e - 1.0f) / (e + 1.0f);
}

/* n table lookups, d[i * dstep] = table[s[i * sstep]], eight at a time. The board's cores run their instructions
 * in order: a table load that has to wait for its own byte load holds up everything behind it. So the bytes are
 * fetched first (with a single load when they lie next to each other), then the eight table values, then the
 * stores. Measured on the board for 512,000 values lying in order: 3.2 ms one by one, 0.9 ms this way. */
static inline __attribute__((always_inline)) void tail_gather(const uint8_t *s, int sstep, float *d, int dstep,
                                                              const float *table, int n)
{
    int i = 0;
#if defined(__BYTE_ORDER__) && __BYTE_ORDER__ == __ORDER_LITTLE_ENDIAN__
    if (sstep == 1) {
        for (; i + 8 <= n; i += 8) {
            uint64_t w;
            float *o = d + i * dstep;
            memcpy(&w, s + i, 8);
            const float v0 = table[w & 255], v1 = table[(w >> 8) & 255], v2 = table[(w >> 16) & 255], v3 = table[(w >> 24) & 255];
            const float v4 = table[(w >> 32) & 255], v5 = table[(w >> 40) & 255], v6 = table[(w >> 48) & 255], v7 = table[w >> 56];
            o[0] = v0; o[dstep] = v1; o[2 * dstep] = v2; o[3 * dstep] = v3;
            o[4 * dstep] = v4; o[5 * dstep] = v5; o[6 * dstep] = v6; o[7 * dstep] = v7;
        }
    }
#endif
    for (; i + 8 <= n; i += 8) {
        const uint8_t *b = s + i * sstep;
        float *o = d + i * dstep;
        const unsigned b0 = b[0], b1 = b[sstep], b2 = b[2 * sstep], b3 = b[3 * sstep];
        const unsigned b4 = b[4 * sstep], b5 = b[5 * sstep], b6 = b[6 * sstep], b7 = b[7 * sstep];
        const float v0 = table[b0], v1 = table[b1], v2 = table[b2], v3 = table[b3];
        const float v4 = table[b4], v5 = table[b5], v6 = table[b6], v7 = table[b7];
        o[0] = v0; o[dstep] = v1; o[2 * dstep] = v2; o[3 * dstep] = v3;
        o[4 * dstep] = v4; o[5 * dstep] = v5; o[6 * dstep] = v6; o[7 * dstep] = v7;
    }
    for (; i < n; i++)
        d[i * dstep] = table[s[i * sstep]];
}

/* Sums with four partial sums, so that an addition does not have to wait for the one before it. */
static inline __attribute__((always_inline)) float tail_sum(const float *e, int n)
{
    float s0 = 0.0f, s1 = 0.0f, s2 = 0.0f, s3 = 0.0f;
    int k = 0;
    for (; k + 4 <= n; k += 4) { s0 += e[k]; s1 += e[k + 1]; s2 += e[k + 2]; s3 += e[k + 3]; }
    for (; k < n; k++) s0 += e[k];
    return (s0 + s1) + (s2 + s3);
}

static inline __attribute__((always_inline)) float tail_dot(const float *e, const float *w, int n)
{
    float s0 = 0.0f, s1 = 0.0f, s2 = 0.0f, s3 = 0.0f;
    int k = 0;
    for (; k + 4 <= n; k += 4) { s0 += e[k] * w[k]; s1 += e[k + 1] * w[k + 1]; s2 += e[k + 2] * w[k + 2]; s3 += e[k + 3] * w[k + 3]; }
    for (; k < n; k++) s0 += e[k] * w[k];
    return (s0 + s1) + (s2 + s3);
}
"""

FOOTER = """int topst_tail_output_count(void)
{
    return %(outputs)d;
}

const float *topst_tail_output(int index, const char **name, int *rank, const int **dims)
{
    if (index < 0 || index >= %(outputs)d)
        return 0;
    if (name) *name = tail_outputs[index].name;
    if (rank) *rank = tail_outputs[index].rank;
    if (dims) *dims = tail_outputs[index].dims;
    return tail_outputs[index].data;
}

/* Finds the NPU outputs this code was generated for. Returns 0 only when every one is there with the size and
 * the scale the tables were made from: anything else means network.h is not the one this file belongs to. */
static int tail_locate(enlight_network_t *inst, void *output_base, const uint8_t **in)
{
    for (int k = 0; k < %(inputs)d; k++) {
        const tail_input_t *want = &tail_inputs[k];
        enlight_act_tensor_t *found = 0;
        for (int i = 0; i < inst->output_tensors_num && !found; i++)
            if (strcmp(inst->output_tensors[i]->name, want->name) == 0)
                found = inst->output_tensors[i];
        if (!found || found->output == 0 || found->data_type != ENLIGHT_DTYPE_INT8 || found->size != want->size
            || found->dimensions[1] != want->channels || found->dimensions[2] != want->height
            || found->dimensions[3] != want->width || found->scale != want->scale)
            return -1;
        in[k] = (const uint8_t *)output_base + found->buf;
    }
    return 0;
}
%(extra)s
#ifndef TOPST_TAIL_ENTRY
/* The entry point the board library calls (network.c: run_post). */
int run_post_process(void *net_inst, void *output_base, int num_input, void *reserved)
{
    static int mode = -1;               /* 0 = generated code, 1 = without the log line, 2 = the toolkit's code */
    const uint8_t *in[%(inputs)d];

    if (mode < 0) {
        const char *choice = getenv("TOPST_POST_PROCESS");
        mode = choice == 0 ? 0 : strcmp(choice, "reference") == 0 ? 2 : strcmp(choice, "quiet") == 0 ? 1 : 0;
    }
    if (mode == 2 || tail_locate((enlight_network_t *)net_inst, output_base, in) != 0)
        return run_post_process_reference(net_inst, output_base, num_input, reserved);
    tail_compute(in);
    if (mode == 0) {
        for (int i = 0; i < %(outputs)d; i++) {
            const tail_output_t *o = &tail_outputs[i];
            post_process_log("tail output %%d %%s [", i, o->name);
            for (int d = 0; d < o->rank; d++)
                post_process_log(d ? ", %%d" : "%%d", o->dims[d]);
            post_process_log("] first values");
            for (int d = 0; d < o->size && d < 4; d++)
                post_process_log(" %%g", o->data[d]);
            post_process_log("\\n");
        }
    }
    return num_input;
}
#endif
"""
