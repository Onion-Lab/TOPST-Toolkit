"""Runs ON THE BOARD (python3, standard library only). Compares the two post-processing functions of a net.so the
app built with its fast code, and times them:

    run_post_process             the app's fast code (engine/board/fast_*.c)
    run_post_process_reference   the toolkit's generated code, kept in the same library

    python3 check_post_process.py --kind yolo|ssd --net ./net.so [--dump <folder of *.oa.bin>] [--frames 12] [--iters 30]

Both are given the same output buffer: the dumped outputs of a real picture (--dump, the files of
"enlight_sim --dump") and made-up frames whose values are mostly low with a few high ones, with copies of cells
so that equal scores and overlapping boxes occur. For a YOLO network the thresholds and the NMS method are also
changed in memory. Every detection must be the same number, bit for bit, in the same order, and the printed
lines must be the same text. Nothing here uses the NPU, the camera or the display.

Exit code 0 = everything the same, 1 = a difference, 2 = could not run.
"""
import argparse
import ctypes
import glob
import os
import random
import struct
import sys
import tempfile
import time

MAX_NUM_OUTPUT = 32
MAX_DETECT_BOX = 131072
POST_NAMES = {0: "none", 1: "classifier", 2: "detector", 3: "custom"}
OBJ = struct.Struct("<4fh2xf2h")          # x_min x_max y_min y_max cls score img_w img_h  (enlight_obj_t)


class Tensor(ctypes.Structure):          # enlight_act_tensor_t
    _fields_ = [("data_type", ctypes.c_int), ("base", ctypes.c_void_p), ("buf", ctypes.c_int),
                ("size", ctypes.c_int), ("fb", ctypes.c_int), ("num_dimension", ctypes.c_int),
                ("dimensions", ctypes.c_int * 4), ("scale", ctypes.c_float), ("zp", ctypes.c_int),
                ("output", ctypes.c_int), ("name", ctypes.c_char * 33)]


class Network(ctypes.Structure):         # enlight_network_t
    _fields_ = [("network_name", ctypes.c_char_p), ("post_proc", ctypes.c_int), ("batch_size", ctypes.c_int),
                ("img_sizes", ctypes.c_int * 3), ("buf_sizes", ctypes.c_void_p),
                ("post_proc_extension", ctypes.c_void_p), ("output_tensors_num", ctypes.c_int),
                ("output_tensors", ctypes.POINTER(Tensor) * MAX_NUM_OUTPUT)]


class Yolo(ctypes.Structure):            # enlight_yolo_detector_t, up to the fields this script changes
    _fields_ = [("softmax_use", ctypes.c_int), ("sigmoid_applied", ctypes.c_int), ("th_conf", ctypes.c_float),
                ("th_iou", ctypes.c_float), ("num_class", ctypes.c_int), ("num_grids", ctypes.c_int * 64),
                ("num_anchor", ctypes.c_int), ("default_box", ctypes.c_void_p), ("exp_tables", ctypes.c_void_p),
                ("sig_tables", ctypes.c_void_p), ("loc_tensors_num", ctypes.c_int),
                ("conf_tensors_num", ctypes.c_int), ("obj_tensors_num", ctypes.c_int),
                ("loc_tensors", ctypes.c_void_p * 8), ("conf_tensors", ctypes.c_void_p * 8),
                ("obj_tensors", ctypes.c_void_p * 8), ("yolo_version", ctypes.c_int),
                ("max_detection", ctypes.c_int), ("nms_method", ctypes.c_int), ("dfl_reg_max", ctypes.c_int)]


class Ssd(ctypes.Structure):             # enlight_ssd_detector_t, up to the fields this script changes
    _fields_ = [("th_conf", ctypes.c_float), ("th_iou", ctypes.c_float), ("num_class", ctypes.c_int),
                ("num_box", ctypes.c_int), ("default_box", ctypes.c_void_p), ("loc_exp_tables", ctypes.c_void_p),
                ("conf_logistic_tables", ctypes.c_void_p), ("loc_tensors_num", ctypes.c_int),
                ("conf_tensors_num", ctypes.c_int), ("bg_in_cls", ctypes.c_int), ("logistic", ctypes.c_int),
                ("nms_method", ctypes.c_int)]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--net", default="./net.so")
    ap.add_argument("--kind", choices=["yolo", "ssd"], required=True, help="which detector the network uses")
    ap.add_argument("--dump", help="folder with the *.oa.bin files of one picture")
    ap.add_argument("--frames", type=int, default=12, help="made-up frames")
    ap.add_argument("--iters", type=int, default=30, help="timed calls per function")
    ap.add_argument("--seed", type=int, default=20261122)
    ap.add_argument("--show", type=int, default=6, help="detections of the dumped picture to print")
    args = ap.parse_args()

    libc = ctypes.CDLL(None)
    net = ctypes.CDLL(os.path.abspath(args.net))
    try:
        fast, reference = net.run_post_process, net.run_post_process_reference
    except AttributeError:
        print("this net.so has no run_post_process_reference: it was not built with the app's fast code")
        return 2
    for f in (fast, reference):
        f.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_int, ctypes.c_void_p]
        f.restype = ctypes.c_int
    inst = Network.in_dll(net, "network_instance")
    inst_addr = ctypes.addressof(inst)
    net.enlight_get_buf_size.argtypes = [ctypes.c_void_p, ctypes.c_int]
    out_size = net.enlight_get_buf_size(inst_addr, 4)         # ENLIGHT_BUF_OUTPUT
    tensors = [inst.output_tensors[i].contents for i in range(inst.output_tensors_num)]
    print("network   : %s, post-processing type %s, input %s, output buffer %d bytes" % (
        inst.network_name.decode(), POST_NAMES.get(inst.post_proc, inst.post_proc), list(inst.img_sizes), out_size))
    for t in tensors:
        print("tensor    : %-12s offset 0x%06x size %7d dims %s scale %.4f" % (
            t.name.decode(), t.buf, t.size, list(t.dimensions), t.scale))
    if inst.post_proc != 2:
        print("not a detector: nothing to compare")
        return 2
    params = (Yolo if args.kind == "yolo" else Ssd).from_address(inst.post_proc_extension)
    original = (params.th_conf, params.th_iou, params.nms_method)
    if args.kind == "yolo":
        print("detector  : YOLO version %d, classes %d, anchors %d, DFL %d, tensors loc %d conf %d obj %d, "
              "th %.3f / %.3f, nms %d" % (params.yolo_version, params.num_class, params.num_anchor,
                                         params.dfl_reg_max, params.loc_tensors_num, params.conf_tensors_num,
                                         params.obj_tensors_num, params.th_conf, params.th_iou, params.nms_method))
    else:
        print("detector  : SSD, classes %d, boxes %d, background %d, logistic %d, th %.3f / %.3f, nms %d" % (
            params.num_class, params.num_box, params.bg_in_cls, params.logistic, params.th_conf, params.th_iou,
            params.nms_method))

    out = ctypes.create_string_buffer(out_size + 64)
    objs = [ctypes.create_string_buffer(4 + MAX_DETECT_BOX * OBJ.size) for _ in range(2)]
    log_file = tempfile.TemporaryFile(dir=os.path.dirname(os.path.abspath(args.net)))
    saved_stdout = os.dup(1)
    null = os.open("/dev/null", os.O_WRONLY)
    sys.stdout.flush()

    def call(function, result, capture):
        """One call; returns the detections and, when asked, the text the function printed (it prints every
        detection, so its output goes to a file or to /dev/null, never to this script's own output)."""
        ctypes.memset(result, 0xA5, 4 + 64 * OBJ.size)         # stale data must not look like a result
        text = None
        sys.stdout.flush()
        if capture:
            log_file.seek(0)
            log_file.truncate()
        os.dup2(log_file.fileno() if capture else null, 1)
        function(inst_addr, out, 1, result)
        libc.fflush(None)
        os.dup2(saved_stdout, 1)
        if capture:
            log_file.seek(0)
            text = log_file.read()
        count = struct.unpack_from("<i", result, 0)[0]
        if not 0 <= count <= MAX_DETECT_BOX:
            return count, [], text
        return count, [result.raw[4 + i * OBJ.size: 4 + (i + 1) * OBJ.size] for i in range(count)], text

    def fields(raw):
        x1, x2, y1, y2, cls, score, w, h = OBJ.unpack(raw)
        return "cls %3d score %.6f box %.3f %.3f %.3f %.3f (%dx%d)" % (cls, score, x1, y1, x2, y2, w, h)

    def same(a, b):
        # bit for bit, leaving out the two padding bytes after cls
        return a[:18] == b[:18] and a[20:] == b[20:]

    failures = 0
    totals = {"frames": 0, "detections": 0, "with detections": 0}

    def compare(label, capture=True, show=0):
        nonlocal failures
        count_r, det_r, text_r = call(reference, objs[0], capture)
        count_f, det_f, text_f = call(fast, objs[1], capture)
        totals["frames"] += 1
        totals["detections"] += max(count_r, 0)
        totals["with detections"] += 1 if count_r > 0 else 0
        problem = None
        if count_r != count_f:
            problem = "the toolkit code found %d, the fast code %d" % (count_r, count_f)
        else:
            for i, (a, b) in enumerate(zip(det_r, det_f)):
                if not same(a, b):
                    problem = "detection %d differs\n      toolkit: %s\n      fast   : %s" % (i, fields(a), fields(b))
                    break
        if problem is None and capture and text_r != text_f:
            problem = "the printed lines differ (%d and %d bytes)" % (len(text_r), len(text_f))
        if problem:
            failures += 1
            print("DIFFERENT : %s: %s" % (label, problem))
        else:
            print("same      : %s: %d detections" % (label, count_r))
        for raw in det_r[:show]:
            print("            " + fields(raw))
        sys.stdout.flush()
        return count_r

    # ---- 1. the dumped outputs of a real picture
    have_dump = False
    if args.dump:
        # An SSD network lists the outputs of its layers as well as the two tensors that join them; those share
        # the same memory and only the joined ones are dumped. So: load every file there is, and require that
        # every byte of the output buffer that belongs to a tensor is covered by one of them.
        covered = []
        for t in tensors:
            name = t.name.decode()
            found = [p for p in glob.glob(os.path.join(args.dump, "*.oa.bin"))
                     if os.path.basename(p).startswith(name + "___") or os.path.basename(p) == name + ".oa.bin"]
            if len(found) == 1 and os.path.getsize(found[0]) == t.size:
                ctypes.memmove(ctypes.addressof(out) + t.buf, open(found[0], "rb").read(), t.size)
                covered.append((t.buf, t.buf + t.size))
        missing = [t.name.decode() for t in tensors
                   if not any(a <= t.buf and t.buf + t.size <= b for a, b in covered)]
        have_dump = bool(covered) and not missing
        if have_dump:
            print("dump      : %d files" % len(covered))
            compare("the dumped picture", show=args.show)
            dumped = out.raw
        else:
            print("dump      : no usable file for %s, skipped" % ", ".join(missing or ["any tensor"]))

    # ---- 2. made-up frames. Bytes come from a table: most entries low, a few high. Whole cells are copied
    #         to their neighbours so that equal scores with overlapping boxes occur.
    rng = random.Random(args.seed)
    settings = [original]
    if args.kind == "yolo":
        settings += [(0.25, 0.45, 0), (0.6, 0.3, 1), (0.5, 0.5, 1 - original[2]), (0.05, 0.6, original[2])]
    else:
        settings += [(0.25, 0.45, original[2]), (0.6, 0.3, original[2]), (0.5, 0.5, 1 - original[2])]

    def make_frame(high_in_65536, high_bottom):
        # Low values stay far below every threshold used here, so only the "high" ones can become candidates:
        # the toolkit's NMS compares every pair of candidates of a class and would run for minutes otherwise.
        # A byte is high when two random bytes both say so, which gives steps of 1/65536 instead of 1/256.
        first = max(1, min(255, high_in_65536 // 256))
        second = max(1, min(256, high_in_65536 // first))
        value = bytes(rng.randint(high_bottom, 127) & 0xFF if i < first else rng.randint(-128, -72) & 0xFF for i in range(256))
        keep = bytes(0xFF if i < second else 0 for i in range(256))
        low = bytes(rng.randint(-128, -72) & 0xFF for i in range(256))
        salt = rng.getrandbits(32)
        for t in tensors:
            c_pad = (t.dimensions[1] + 31) & 0xFFE0
            h, w = t.dimensions[2], t.dimensions[3]
            a = int.from_bytes(rng.randbytes(t.size).translate(value), "little")
            b = int.from_bytes(rng.randbytes(t.size).translate(keep), "little")
            c = int.from_bytes(rng.randbytes(t.size).translate(low), "little")
            everywhere = (1 << (8 * t.size)) - 1
            data = bytearray(((a & b) | (c & (b ^ everywhere))).to_bytes(t.size, "little"))
            cells = random.Random((h * 100003 + w) ^ salt)      # the same cells for every tensor of a scale
            if c_pad * h * w == t.size and h * w > 4:
                for _ in range(max(2, (h * w) // 40)):
                    src = cells.randrange(h * w - 1) * c_pad      # a cell (or an SSD box) and the one after it
                    dst = src + c_pad
                    data[dst:dst + c_pad] = data[src:src + c_pad]
            ctypes.memmove(ctypes.addressof(out) + t.buf, bytes(data), t.size)

    # How many of 65,536 byte values are "high" and where the high values start. A network that picks cells by
    # objectness needs more of them: a cell counts only when its objectness and a class value are both high.
    # One frame in six is dense (thousands of detections); the others stay near what a busy picture gives.
    by_objectness = args.kind == "yolo" and params.yolo_version not in (6, 8)
    if by_objectness:
        densities = [(768, 40), (1536, 0), (1024, -40), (2048, -20), (3072, 20), (1024, 90)]
    else:
        densities = [(48, 40), (96, 0), (64, -40), (160, -20), (512, 90), (32, -60)]
    for frame in range(args.frames):
        high, high_bottom = densities[frame % len(densities)]
        make_frame(high, high_bottom)
        th_conf, th_iou, nms = settings[frame % len(settings)]
        params.th_conf, params.th_iou, params.nms_method = th_conf, th_iou, nms
        started = time.perf_counter()
        compare("frame %2d (th %.2f / %.2f, nms %d, %d of 65536 values from %d up)" % (
            frame, th_conf, th_iou, nms, high, high_bottom), capture=frame < 6)
        if time.perf_counter() - started > 60:
            print("            (that frame took %.0f s)" % (time.perf_counter() - started))
    params.th_conf, params.th_iou, params.nms_method = original

    # ---- 3. time, with the functions' own printing sent to /dev/null
    if have_dump:
        ctypes.memmove(out, dumped, len(dumped))
        what = "the dumped picture"
    else:
        rng = random.Random(args.seed)
        make_frame(*densities[0])
        what = "a made-up frame"
    timings = {}
    for name, function in (("toolkit", reference), ("fast", fast)):
        sys.stdout.flush()
        os.dup2(null, 1)
        times = []
        for _ in range(args.iters):
            start = time.perf_counter()
            function(inst_addr, out, 1, objs[0])
            times.append((time.perf_counter() - start) * 1e3)
        libc.fflush(None)
        os.dup2(saved_stdout, 1)
        timings[name] = (min(times), sum(times) / len(times))
    count = struct.unpack_from("<i", objs[0], 0)[0]
    print("time      : %s (%d detections), %d calls each, one core" % (what, count, args.iters))
    for name in ("toolkit", "fast"):
        print("            %-8s best %8.3f ms   mean %8.3f ms" % ((name,) + timings[name]))
    print("            %.1f times faster" % (timings["toolkit"][0] / max(timings["fast"][0], 1e-9)))

    print("RESULT    : %s (%d frames, %d with detections, %d detections compared)" % (
        "all the same" if failures == 0 else "%d DIFFERENT" % failures,
        totals["frames"], totals["with detections"], totals["detections"]))
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
