"""Reads an ONNX model's interface without the ENLIGHT SDK: inputs, outputs, opsets, operators, parameters.

    python inspect_model.py --toolkit <toolkit root> --out <json> --model <file.onnx>

Uses onnx from <runtime>/extras/onnx and never imports enlight (the two cannot share a process).
The model-family guess (YOLO head names, SSD, classification) of design section 4.7 comes in M1.
"""
import collections
import os
import sys

from _common import add_onnx_path, parser, run


def main(args):
    add_onnx_path()
    import onnx

    path = os.path.abspath(args.model)
    result = {"ok": False, "path": path, "problems": []}
    model = onnx.load(path, load_external_data=False)
    graph = model.graph
    initializers = {t.name for t in graph.initializer}

    def tensor(value_info):
        t = value_info.type.tensor_type
        dims = []
        for d in t.shape.dim:
            if d.HasField("dim_value"):
                dims.append(d.dim_value)
            elif d.HasField("dim_param"):
                dims.append(d.dim_param)
            else:
                dims.append(None)
        try:
            dtype = onnx.TensorProto.DataType.Name(t.elem_type)
        except ValueError:
            dtype = str(t.elem_type)
        return {"name": value_info.name, "shape": dims, "dtype": dtype}

    parameters = 0
    for t in graph.initializer:
        count = 1
        for d in t.dims:
            count *= d
        parameters += count

    result.update({
        "irVersion": model.ir_version,
        "producer": ("%s %s" % (model.producer_name, model.producer_version)).strip(),
        "opsets": [{"domain": o.domain or "ai.onnx", "version": o.version} for o in model.opset_import],
        "inputs": [tensor(v) for v in graph.input if v.name not in initializers],
        "outputs": [tensor(v) for v in graph.output],
        "operators": dict(sorted(collections.Counter(n.op_type for n in graph.node).items())),
        "nodeCount": len(graph.node),
        "parameterCount": parameters,
    })
    try:
        onnx.checker.check_model(model)
    except Exception as exc:  # report, the toolkit may still read it
        result["problems"].append("onnx checker: %s" % (str(exc).splitlines()[0] if str(exc) else type(exc).__name__))
    result["ok"] = True
    return result


if __name__ == "__main__":
    ap = parser(__doc__)
    ap.add_argument("--model", required=True, help=".onnx file")
    sys.exit(run(main, ap.parse_args()))
