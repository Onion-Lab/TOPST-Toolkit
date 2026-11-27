"""Runs ON THE BOARD (python3, standard library only). Checks the code the app generated for the part of a model
behind a cut (net.so built from engine/helpers/gen_tail.py's output) against what the PC computed for the same
NPU output bytes (engine/helpers/eval_tail.py), and times it.

    python3 check_tail.py --net ./net.so --dump <folder of *.oa.bin> --expect <folder of 0.bin, 1.bin ...> [--iters 30]

Every output value must agree with the PC's within --tolerance (absolute, plus the same relative part): the PC
uses numpy and the board its own arithmetic, so the last bits may differ; values read through tables are equal.
Nothing here uses the NPU, the camera or the display.

Exit code 0 = all outputs agree, 1 = a difference, 2 = could not run.
"""
import argparse
import array
import ctypes
import glob
import os
import sys
import time

MAX_NUM_OUTPUT = 32


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


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--net", default="./net.so")
    ap.add_argument("--dump", required=True, help="folder with the *.oa.bin files of one picture")
    ap.add_argument("--expect", required=True, help="folder with 0.bin, 1.bin ... from eval_tail.py")
    ap.add_argument("--iters", type=int, default=30)
    ap.add_argument("--tolerance", type=float, default=1e-4)
    args = ap.parse_args()

    libc = ctypes.CDLL(None)
    net = ctypes.CDLL(os.path.abspath(args.net))
    try:
        count_outputs, get_output = net.topst_tail_output_count, net.topst_tail_output
    except AttributeError:
        print("this net.so has no generated tail code (topst_tail_output_count is missing)")
        return 2
    get_output.restype = ctypes.POINTER(ctypes.c_float)
    get_output.argtypes = [ctypes.c_int, ctypes.POINTER(ctypes.c_char_p), ctypes.POINTER(ctypes.c_int),
                           ctypes.POINTER(ctypes.POINTER(ctypes.c_int))]
    run = net.run_post_process
    run.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_int, ctypes.c_void_p]
    inst = Network.in_dll(net, "network_instance")
    inst_addr = ctypes.addressof(inst)
    net.enlight_get_buf_size.argtypes = [ctypes.c_void_p, ctypes.c_int]
    out_size = net.enlight_get_buf_size(inst_addr, 4)
    out = ctypes.create_string_buffer(out_size + 64)
    print("network   : %s, output buffer %d bytes" % (inst.network_name.decode(), out_size))
    loaded = 0
    for i in range(inst.output_tensors_num):
        t = inst.output_tensors[i].contents
        name = t.name.decode()
        found = [p for p in glob.glob(os.path.join(args.dump, "*.oa.bin"))
                 if os.path.basename(p).startswith(name + "___") or os.path.basename(p) == name + ".oa.bin"]
        if len(found) == 1 and os.path.getsize(found[0]) == t.size:
            ctypes.memmove(ctypes.addressof(out) + t.buf, open(found[0], "rb").read(), t.size)
            loaded += 1
    print("dump      : %d of %d NPU outputs loaded" % (loaded, inst.output_tensors_num))

    # a result buffer in case the library presents itself as a detector (131072 detections of 28 bytes)
    result = ctypes.create_string_buffer(4 + 131072 * 28)
    null = os.open("/dev/null", os.O_WRONLY)
    saved = os.dup(1)

    def call():
        sys.stdout.flush()
        os.dup2(null, 1)
        started = time.perf_counter()
        run(inst_addr, out, 1, result)
        elapsed = (time.perf_counter() - started) * 1e3
        libc.fflush(None)
        os.dup2(saved, 1)
        return elapsed

    call()
    failures = 0
    for index in range(count_outputs()):
        name = ctypes.c_char_p()
        rank = ctypes.c_int()
        dims = ctypes.POINTER(ctypes.c_int)()
        data = get_output(index, ctypes.byref(name), ctypes.byref(rank), ctypes.byref(dims))
        shape = [dims[i] for i in range(rank.value)]
        size = 1
        for n in shape:
            size *= n
        got = array.array("f", ctypes.string_at(data, 4 * size))
        path = os.path.join(args.expect, "%d.bin" % index)
        if not os.path.exists(path) or os.path.getsize(path) != 4 * size:
            print("DIFFERENT : output %d %s %s: the PC's file is missing or has another size" % (index, name.value.decode(), shape))
            failures += 1
            continue
        want = array.array("f")
        with open(path, "rb") as fp:
            want.fromfile(fp, size)
        worst, worst_at, bad, exact = 0.0, -1, 0, 0
        for i in range(size):
            a, b = got[i], want[i]
            if a == b:
                exact += 1
                continue
            d = abs(a - b)
            if not d <= args.tolerance * (1.0 + abs(b)):        # also catches NaN
                bad += 1
            if not d <= worst:
                worst, worst_at = d, i
        if bad:
            failures += 1
            print("DIFFERENT : output %d %s %s: %d of %d values off, the worst at %d: board %r, PC %r" % (
                index, name.value.decode(), shape, bad, size, worst_at, got[worst_at], want[worst_at]))
        else:
            print("same      : output %d %s %s: %d values, %d exactly equal, largest difference %.3g" % (
                index, name.value.decode(), shape, size, exact, worst))

    times = [call() for _ in range(args.iters)]
    print("time      : best %8.3f ms   mean %8.3f ms   (%d calls, one core)" % (min(times), sum(times) / len(times), args.iters))
    try:
        reference = net.run_post_process_reference
        reference.argtypes = run.argtypes
        sys.stdout.flush()
        os.dup2(null, 1)
        started = time.perf_counter()
        reference(inst_addr, out, 1, result)
        elapsed = (time.perf_counter() - started) * 1e3
        libc.fflush(None)
        os.dup2(saved, 1)
        print("            the toolkit's own post-processing for this network type: %.3f ms" % elapsed)
    except AttributeError:
        pass
    print("RESULT    : %s" % ("all outputs agree with the PC" if failures == 0 else "%d DIFFERENT" % failures))
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
