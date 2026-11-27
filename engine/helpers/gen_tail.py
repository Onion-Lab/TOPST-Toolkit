"""Writes the C code that computes, on the board's CPU, the part of a model behind a cut (design section 5.8).

    python gen_tail.py --toolkit <toolkit root> --out <json> --model <original.onnx> --nodes <cut operators...>
                       --network-h <compiler output>/network.h
                       (--layers-json <read_enlight.json> | --layers <file> | --dump-dir <folder>)
                       --c-out <tail.c> [--title <name>]

The toolkit's full layer names (<Kind>_<n>___<operator name>) connect the cut operators of the ONNX file to the
output tensors of network.h. They come from the answer of read_enlight.py for the quantized network
(--layers-json, what the app uses: the names are known before anything has been simulated), from a text file with
one name per line (--layers), or from the names of the *.oa.bin files of an INT8 dump (--dump-dir).

The answer says what was generated, or which operator could not be turned into code and why.
Uses onnx from <runtime>/extras/onnx and never imports enlight.
"""
import os

from _common import parser, run


def main(args):
    import tail_graph as tg

    result = {"ok": False, "problems": [], "unsupported": [], "model": os.path.abspath(args.model), "nodes": list(args.nodes)}
    try:
        tail = tg.load_tail(args.model, args.nodes)
        tensors = tg.read_network_header(args.network_h)
        pairs = tg.match_inputs(tail, tensors, tg.layer_names(args.layers, args.layers_json, args.dump_dir))
        generator = tg.TailGenerator(tail, pairs, args.title or os.path.splitext(os.path.basename(args.model))[0])
        result["ops"] = len(tail.nodes)
        result["opTypes"] = sorted(set(n.op for n in tail.nodes))
        result["inputs"] = [{"cut": i["cut"], "tensor": i["tensor"], "shape": i["shape"], "npu": i["npu"].to_dict()}
                            for i in generator.inputs]
        result["keptOutputs"] = tail.kept_outputs
        if not generator.run():
            result["unsupported"] = [{"node": n, "op": op, "why": why} for n, op, why in generator.unsupported]
            result["problems"].append("%s (%s): %s" % generator.unsupported[0])
            return result
        source = generator.source()
    except tg.TailError as error:
        result["problems"].append(str(error))
        return result
    with open(args.c_out, "w", encoding="utf-8", newline="\n") as fp:
        fp.write(source)
    result.update({
        "ok": True,
        "cFile": os.path.abspath(args.c_out),
        "sourceBytes": len(source.encode("utf-8")),
        "outputs": [{"name": o["name"], "shape": o["shape"]} for o in generator.outputs],
        "stats": {"gathered": generator.stats["gathered"], "floatOps": generator.stats["float_ops"],
                  "tables": generator.stats["tables"], "buffers": generator.stats["buffers"],
                  "bufferBytes": generator.stats["buffer_bytes"]},
    })
    return result


if __name__ == "__main__":
    ap = parser(__doc__)
    ap.add_argument("--model", required=True)
    ap.add_argument("--nodes", nargs="+", required=True)
    ap.add_argument("--network-h", required=True)
    ap.add_argument("--layers")
    ap.add_argument("--layers-json")
    ap.add_argument("--dump-dir")
    ap.add_argument("--c-out", required=True)
    ap.add_argument("--title")
    parsed = ap.parse_args()
    if not parsed.layers and not parsed.layers_json and not parsed.dump_dir:
        ap.error("give --layers-json, --layers or --dump-dir")
    raise SystemExit(run(main, parsed))
