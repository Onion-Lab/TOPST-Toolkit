"""Engine self-check: Python, bundled packages, the ENLIGHT SDK and its PyArmor license.

    python engine_check.py --toolkit <toolkit root> --out <json>
"""
import os
import platform
import sys
import time

from _common import add_sdk_path, parser, run


def main(args):
    report = {"ok": False, "python": platform.python_version(), "problems": []}
    sdk_dir = add_sdk_path(args.toolkit)

    t0 = time.time()
    packages = {}
    for name in ("numpy", "torch", "torchvision", "cv2", "PIL", "scipy", "pandas", "matplotlib", "pycocotools", "flatbuffers", "netron"):
        try:
            module = __import__(name)
            packages[name] = str(getattr(module, "__version__", "installed"))
        except Exception as exc:  # keep checking the rest
            report["problems"].append("import %s failed: %s" % (name, exc))
    report["packages"] = packages
    try:
        import torch
        report["cudaAvailable"] = bool(torch.cuda.is_available())
    except Exception:
        pass

    try:
        import enlight
        report["sdkVersion"] = getattr(enlight, "SDK_version", None)
        report["enlightFile"] = getattr(enlight, "__file__", None)
        enlight_file = report["enlightFile"]
        if not enlight_file or not os.path.normcase(os.path.abspath(enlight_file)).startswith(os.path.normcase(sdk_dir)):
            report["problems"].append("enlight was imported from an unexpected place: %r" % (enlight_file,))
        from enlight import pytransform
        license_info = {}
        for fn in ("get_license_info", "get_expired_days"):
            try:
                license_info[fn] = getattr(pytransform, fn)()
            except Exception as exc:
                license_info[fn] = "error: %s" % (exc,)
        report["license"] = license_info
        days = license_info.get("get_expired_days")
        if isinstance(days, int) and 0 <= days < 30:
            report["problems"].append("SDK license expires in %d days" % days)
        import simulator  # noqa: F401  converter / enlight_sim need it (pulls in pycocotools)
        from simulator.helpers import Tracker  # noqa: F401
    except Exception as exc:
        report["problems"].append("import enlight failed: %s: %s" % (type(exc).__name__, exc))

    report["importSeconds"] = round(time.time() - t0, 2)
    report["ok"] = not report["problems"] and report.get("sdkVersion") is not None
    return report


if __name__ == "__main__":
    sys.exit(run(main, parser(__doc__).parse_args()))
