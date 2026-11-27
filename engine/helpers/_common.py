"""Shared plumbing for TOPST Toolkit helper scripts.

Every helper:
  * takes --toolkit <absolute toolkit root> and --out <json file>
  * puts <toolkit>/EnlightSDK on sys.path by ABSOLUTE path. A job folder holds an empty
    EnlightSDK/enlight/code_emission/cfg/ tree; a cwd-relative path would import that empty
    folder as a namespace package (feature/09 section 6-1).
  * writes its answer as JSON (stdout belongs to the toolkit's own progress output)
  * prints the banner "TOPST helper. start." and the marker "TOPST helper. done." when it finishes
"""
import argparse
import json
import os
import sys
import traceback

START_BANNER = "TOPST helper. start."
DONE_MARKER = "TOPST helper. done."


def parser(description):
    ap = argparse.ArgumentParser(description=description)
    ap.add_argument("--toolkit", required=True, help="absolute path of the toolkit root (holds EnlightSDK)")
    ap.add_argument("--out", required=True, help="JSON file to write")
    return ap


def add_sdk_path(toolkit_root):
    sdk = os.path.join(os.path.abspath(toolkit_root), "EnlightSDK")
    if not os.path.isdir(sdk):
        raise SystemExit("EnlightSDK not found under %s" % toolkit_root)
    sys.path.insert(0, sdk)
    return sdk


def add_onnx_path():
    """onnx lives in <runtime>/extras/onnx, off the default sys.path: the ENLIGHT SDK bundles its own onnx_pb2
    and protobuf refuses both in one process. A helper that calls this must never import enlight."""
    if "enlight" in sys.modules:
        raise RuntimeError("onnx and the ENLIGHT SDK cannot be used in the same process")
    runtime_root = os.path.dirname(os.path.dirname(os.path.abspath(sys.executable)))
    extras = os.path.join(runtime_root, "extras", "onnx")
    if not os.path.isdir(extras):
        raise RuntimeError("onnx is not bundled with this runtime (%s)" % extras)
    sys.path.insert(0, extras)
    return extras


def to_jsonable(value):
    """model.info holds numpy scalars, tuples and torch values; make them JSON friendly."""
    if value is None or isinstance(value, (bool, int, float, str)):
        return value
    if isinstance(value, dict):
        return {str(k): to_jsonable(v) for k, v in value.items()}
    if isinstance(value, (list, tuple, set)):
        return [to_jsonable(v) for v in value]
    for attr in ("tolist", "item"):
        if hasattr(value, attr):
            try:
                return to_jsonable(getattr(value, attr)())
            except Exception:
                pass
    return str(value)


def run(main, args):
    """Calls main(args) -> dict, writes it to args.out, prints the done marker. Errors still produce JSON."""
    print(START_BANNER, flush=True)
    try:
        result = main(args)
        code = 0
    except SystemExit:
        raise
    except Exception as exc:
        result = {"ok": False, "problems": ["%s: %s" % (type(exc).__name__, exc)], "traceback": traceback.format_exc()}
        code = 1
    with open(args.out, "w", encoding="utf-8") as fp:
        json.dump(to_jsonable(result), fp, ensure_ascii=False, indent=2)
    print("", flush=True)
    print(DONE_MARKER if code == 0 else "TOPST helper. failed.", flush=True)
    return code
