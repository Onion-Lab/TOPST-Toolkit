"""Computes on the PC what the generated board code computes: the part of a model behind a cut, from the NPU
output bytes of an INT8 dump (design section 5.8). The reference the board code is compared with, and the
values the result screen shows for a model with a part of its own behind the cut.

    python eval_tail.py --toolkit <toolkit root> --out <json> --model <original.onnx> --nodes <cut operators...>
                        --network-h <compiler output>/network.h
                        --dump <folder with *.oa.bin of one picture>... --out-dir <folder>...
                        [--layers-json <read_enlight.json> | --layers <file>]

One --out-dir per --dump (several pictures in one run: the model is loaded once). Every folder gets <index>.bin
per output (float32, little endian, in the order of the model's outputs); the answer lists them under "runs",
and the first picture's also under "outputs". Without --layers-json / --layers the layer names are taken from
the names of the dump files. The dequantization is the board's: (byte + zero point) / scale with the scale of
network.h. Uses onnx from <runtime>/extras/onnx and never imports enlight.
"""
import glob
import os

from _common import parser, run


def main(args):
    import numpy as np
    import tail_graph as tg

    result = {"ok": False, "problems": [], "outputs": [], "runs": []}
    if len(args.dump) != len(args.out_dir):
        result["problems"].append("give one --out-dir per --dump")
        return result
    try:
        tail = tg.load_tail(args.model, args.nodes)
        tensors = tg.read_network_header(args.network_h)
        names = tg.layer_names(args.layers, args.layers_json, args.dump[0])
        pairs = tg.match_inputs(tail, tensors, names)
        for dump, out_dir in zip(args.dump, args.out_dir):
            values = {}
            for (name, shape, cut), tensor in pairs:
                found = glob.glob(os.path.join(dump, tensor.name + "___*.oa.bin")) + glob.glob(os.path.join(dump, tensor.name + ".oa.bin"))
                if len(found) != 1:
                    raise tg.TailError("the dump %s has no file for the NPU output %s" % (os.path.basename(dump), tensor.name))
                raw = open(found[0], "rb").read()
                if len(raw) != tensor.size:
                    raise tg.TailError("%s holds %d bytes, network.h says %d" % (os.path.basename(found[0]), len(raw), tensor.size))
                values[name] = tg.dequantized(tensor, raw)[tg.npu_offsets(shape, tensor)]
            outputs = tg.evaluate(tail, values)
            os.makedirs(out_dir, exist_ok=True)
            listed = []
            for i, ((_, original), array) in enumerate(zip(tail.outputs, outputs)):
                path = os.path.join(out_dir, "%d.bin" % i)
                array.astype("<f4").tofile(path)
                flat = array.reshape(-1)
                listed.append({"index": i, "name": original, "shape": list(array.shape), "file": os.path.abspath(path),
                               "min": float(np.nanmin(array)), "max": float(np.nanmax(array)),
                               "first": [float(v) for v in flat[:8]]})
            result["runs"].append({"dump": os.path.abspath(dump), "outDir": os.path.abspath(out_dir), "outputs": listed})
    except tg.TailError as error:
        result["problems"].append(str(error))
        return result
    result["outputs"] = result["runs"][0]["outputs"] if result["runs"] else []
    result["ok"] = True
    return result


if __name__ == "__main__":
    ap = parser(__doc__)
    ap.add_argument("--model", required=True)
    ap.add_argument("--nodes", nargs="+", required=True)
    ap.add_argument("--network-h", required=True)
    ap.add_argument("--dump", nargs="+", required=True)
    ap.add_argument("--layers")
    ap.add_argument("--layers-json")
    ap.add_argument("--out-dir", nargs="+", required=True)
    raise SystemExit(run(main, ap.parse_args()))
