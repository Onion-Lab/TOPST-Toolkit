"""The estimated NPU time on the TOPST AI-G board: the toolkit simulator's value corrected with what the board showed.

    python board_time.py --toolkit <toolkit root> --out <json> --perf <perf json> --coefficients <json>
                         [--timeline <perf_sim_out.csv>] [--model <file.onnx>]

The simulator (perf_simulator.py with the board's clocks) misses what the board does differently, found by measuring
the models of the model zoo on the board (design section 11, 2026-11-28):
  * average pooling the NPU runs as an mlx command takes no time in the simulator; on the board it grows with the
    output values times the kernel side,
  * stride-2 max pooling run as an mlx command is faster on the board than the simulator thinks,
  * a first convolution with a kernel larger than 3x3 on the picture (YOLOv5's 6x6, the 7x7 of ResNet, DenseNet, DLA)
    costs the board per output pixel and 32 output channels,
  * depthwise convolutions (MobileNet, EfficientNet) are slower on the board than the simulator thinks,
and every run has a small fixed cost. The coefficients come from engine/board/topst-ai-g-time.json.

The pooling and the depthwise work are read from the simulator's own timeline (its --debug CSV: one row per start, the
ops of the DMA engine and the two cores with their parameters and run time). The first convolution is read from the
ONNX model, because the compiler splits a large kernel into small pieces. Without a timeline only the other terms apply
(and the answer says so). Uses onnx from <runtime>/extras/onnx and never imports enlight.
"""
import csv
import json
import math
import os
import re

from _common import add_onnx_path, parser, run

POOL = re.compile(r"mlx_cmd_(avg|max)_pool_ih1_m1_(\d+)_iw1_m1_(\d+)_ic1_m1_(\d+)_stride_w_m1_(\d+)_stride_h_m1_(\d+)"
                  r"_kernel_h_m1_(\d+)_kernel_w_m1_(\d+)_pad_t_(\d+)_pad_b_(\d+)_pad_l_(\d+)_pad_r_(\d+)")
CORE_COLUMNS = (7, 12)     # name, op, param, run time (s), waits for: CORE_0 at 7, CORE_1 at 12 (DMA at 2)


def pooling(path):
    """What the timeline says about the parts the board does differently: average pooling as output values x kernel
    side (millions), the simulator's own time for stride-2 max pooling and for depthwise convolutions (ms, both cores
    added up)."""
    avg_side = 0.0
    max_s2_ms = 0.0
    dw_ms = 0.0
    avg_ops = max_ops = dw_ops = 0
    started = False
    with open(path, encoding="utf-8", errors="replace") as fp:
        for cells in csv.reader(fp):
            if not started:
                started = len(cells) > 1 and cells[0] == "Group name"
                continue
            if not cells:
                continue
            if cells[0] == "DONE":
                break
            if len(cells) < 17:
                continue
            for base in CORE_COLUMNS:
                if cells[base + 1] == "DW":
                    dw_ms += float(cells[base + 3] or 0) * 1e3
                    dw_ops += 1
                    continue
                m = POOL.search(cells[base + 2])
                if not m:
                    continue
                ih, iw, ic, sw, sh, kh, kw = [int(x) + 1 for x in m.groups()[1:8]]
                pt, pb, pl, pr = [int(x) for x in m.groups()[8:12]]
                oh = max(0, (ih + pt + pb - kh) // sh + 1)
                ow = max(0, (iw + pl + pr - kw) // sw + 1)
                if m.group(1) == "avg":
                    avg_side += oh * ow * ic * kh / 1e6
                    avg_ops += 1
                elif sh > 1:
                    max_s2_ms += float(cells[base + 3] or 0) * 1e3
                    max_ops += 1
    if not started:
        raise ValueError("not a perf_simulator timeline: %s" % path)
    return {"avgPoolSideM": avg_side, "avgPoolOps": avg_ops, "maxPoolStride2SimMs": max_s2_ms, "maxPoolStride2Ops": max_ops,
            "depthwiseSimMs": dw_ms, "depthwiseOps": dw_ops}


def stem(model_path):
    """The first Conv that reads the picture: kernel, stride, output channels and pixels; bigM = output pixels x
    32-channel passes (millions) when the kernel is larger than 3x3, else 0."""
    add_onnx_path()
    import onnx
    model = onnx.load(model_path, load_external_data=False)
    graph = model.graph
    weights = {t.name: t for t in graph.initializer}
    inputs = [i for i in graph.input if i.name not in weights]
    if not inputs:
        return None
    dims = [d.dim_value for d in inputs[0].type.tensor_type.shape.dim]
    if len(dims) != 4 or dims[2] <= 0 or dims[3] <= 0:
        return None
    h, w = dims[2], dims[3]
    feeds = {inputs[0].name}
    for node in graph.node:
        # what does not change the picture's size on the way to the first convolution (normalization, casts)
        if node.op_type in ("Identity", "Cast", "Mul", "Add", "Sub", "Div") and any(i in feeds for i in node.input):
            feeds.update(node.output)
            continue
        if node.op_type != "Conv" or node.input[0] not in feeds:
            continue
        attrs = {a.name: onnx.helper.get_attribute_value(a) for a in node.attribute}
        weight = weights.get(node.input[1])
        if weight is None:
            return None
        shape = list(weight.dims)
        kernel = list(attrs.get("kernel_shape", shape[2:]))
        strides = list(attrs.get("strides", [1, 1]))
        pads = list(attrs.get("pads", [0, 0, 0, 0]))
        if attrs.get("auto_pad", b"NOTSET") not in (b"NOTSET", "NOTSET"):
            pads = [kernel[0] // 2, kernel[1] // 2, (kernel[0] - 1) // 2, (kernel[1] - 1) // 2]
        oh = (h + pads[0] + pads[2] - kernel[0]) // strides[0] + 1
        ow = (w + pads[1] + pads[3] - kernel[1]) // strides[1] + 1
        passes = int(math.ceil(shape[0] / 32.0))
        big = max(kernel) > 3
        return {"kernel": int(max(kernel)), "stride": int(strides[0]), "channels": int(shape[0]), "pixels": int(oh * ow),
                "bigM": (oh * ow * passes / 1e6) if big else 0.0}
    return None


def main(args):
    with open(args.coefficients, encoding="utf-8") as fp:
        c = json.load(fp)
    with open(args.perf, encoding="utf-8") as fp:
        simulator = float(json.load(fp)["Inference_Time(msec)"])
    problems = []
    pools = None
    if args.timeline and os.path.isfile(args.timeline):
        pools = pooling(args.timeline)
    else:
        problems.append("no simulator timeline: pooling not corrected")
    first = None
    if args.model and os.path.isfile(args.model):
        try:
            first = stem(args.model)
        except Exception as exc:          # a model onnx cannot read leaves the stem term out, not the answer
            problems.append("first convolution not read: %s" % exc)
    else:
        problems.append("no model: first convolution not corrected")

    terms = {
        "fixedMs": c["fixedMs"],
        "avgPoolMs": c["avgPoolMsPerMillion"] * pools["avgPoolSideM"] if pools else 0.0,
        "maxPoolMs": c["maxPoolStride2Scale"] * pools["maxPoolStride2SimMs"] / 2 if pools else 0.0,
        "depthwiseMs": c.get("depthwiseScale", 0.0) * pools["depthwiseSimMs"] / 2 if pools else 0.0,
        "bigStemMs": c["bigStemMsPerMillion"] * first["bigM"] if first else 0.0,
    }
    estimate = max(simulator + sum(terms.values()), simulator * 0.3)
    return {"ok": True, "simulatorMs": round(simulator, 4), "estimateMs": round(estimate, 4),
            "terms": {k: round(v, 4) + 0.0 for k, v in terms.items()}, "pooling": pools, "stem": first,
            "complete": pools is not None and first is not None, "coefficients": os.path.basename(args.coefficients),
            "problems": problems}


if __name__ == "__main__":
    ap = parser(__doc__.splitlines()[0])
    ap.add_argument("--perf", required=True, help="perf_simulator's JSON (Inference_Time(msec))")
    ap.add_argument("--coefficients", required=True, help="engine/board/topst-ai-g-time.json")
    ap.add_argument("--timeline", help="perf_simulator's --debug CSV")
    ap.add_argument("--model", help="the ONNX model the network was compiled from")
    run(main, ap.parse_args())
