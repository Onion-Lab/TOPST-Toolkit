"""Reads a .enlight network's metadata: model.info, input size, type, layer list.

    python read_enlight.py --toolkit <toolkit root> --out <json> --model <file.enlight>

Uses the SDK's EnlightParser (feature/03 section 8). Loading prints "parsing Nth layer" progress on stdout.
"""
import os
import sys

from _common import add_sdk_path, parser, run, to_jsonable


def main(args):
    add_sdk_path(args.toolkit)
    from enlight import EnlightParser

    path = os.path.abspath(args.model)
    with EnlightParser() as enlight_parser:
        model = enlight_parser.parse(path)

    result = {"ok": True, "path": path, "problems": []}
    info = getattr(model, "info", {}) or {}
    result["info"] = {str(k): to_jsonable(v) for k, v in dict(info).items()}

    def call(name, *a):
        try:
            return to_jsonable(getattr(model, name)(*a))
        except Exception as exc:
            result["problems"].append("%s() failed: %s" % (name, exc))
            return None

    result["type"] = call("get_type")
    result["isQuantized"] = bool(call("is_quantized"))
    result["isTracked"] = bool(call("is_tracked"))
    result["hasPostProcess"] = call("has_post_process")
    result["inputSize"] = call("get_input_size")
    result["sourceModel"] = call("get_source_model_name")

    layers = []
    try:
        for index, layer in enumerate(model.module_list):
            layers.append({"index": index, "name": str(getattr(layer, "name", "")), "kind": type(layer).__name__})
    except Exception as exc:
        result["problems"].append("layer list unavailable: %s" % (exc,))
    result["layers"] = layers
    return result


if __name__ == "__main__":
    ap = parser(__doc__)
    ap.add_argument("--model", required=True, help=".enlight file")
    sys.exit(run(main, ap.parse_args()))
