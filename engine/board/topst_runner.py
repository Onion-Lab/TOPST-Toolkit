#!/usr/bin/env python3
"""TOPST Toolkit board runner. The app installs it on the TOPST AI-G board as /usr/bin/topst-runner (our own code).

Runs on the board with its python3 (standard library only) and the board's NPU library, libtcndnpu.so.1, whose API
is public (github.com/topst-development/libtc-ndnpu, lib/include/npu_api.h; unchanged since its first commit).
It does what the board's tcnnapp does for one network (npu_open -> network_load_from_file -> network_run ->
network_run_postprocess), but keeps the network loaded and takes frames over stdin, so a video does not reload the
model for every frame, and it reports the NPU's own time (npu_perf.elapsed_in_us) that tcnnapp only draws on a
picture. One NPU cluster runs one model.

    topst-runner version
    topst-runner monitor [--interval 2]          board status, one JSON line per interval
    topst-runner serve --model DIR [--npu 0|1]   load once; frames in on stdin, one JSON line out per frame
    topst-runner bench --model DIR [--npu 0|1] [--count 100]
    topst-runner soak --model DIR [--npu 0|1] --seconds S [--every 5]   runs without a pause; speed and temperature
    topst-runner models [--root /usr/share/topst-toolkit]

Frames (serve): a 16-byte header "<4sIII" = (b"TPF1", seq, length, kind) and `length` bytes. kind 0 = one picture,
RGB, 8 bits per channel, exactly width x height of the network (rows packed); kind 1 = stop. The picture must be
prepared like the toolkit's simulator does it (letterbox and resize on the PC), so the board sees what the PC saw.

Output is JSON lines on the original stdout. The network's post-processing (net.so) prints every detection with
printf, so the C library's stdout goes to /dev/null and only this script writes to the channel.
"""
import argparse
import ctypes
import glob
import json
import os
import socket
import struct
import sys
import time

VERSION = "2"
LIB = "libtcndnpu.so.1"
LIB_DIRS = ("/usr/lib", "/lib", "/usr/lib64", "/lib64", "/usr/local/lib")
MODEL_FILES = ("net.so", "npu_cmd.bin", "quantized_network.bin")
DEFAULT_ROOT = "/usr/share/topst-toolkit"
STATE_DIR = "/tmp/topst-runner"           # serve writes how busy its NPU is; monitor reads it
NPU_RUN_ASYNC = 1                         # npu_run_mode_t, as tcnnapp runs by default
TIMEOUT_MS = 3000
MAX_DETECT_BOX = 131072                   # the toolkit's enlight_objs_t (net.so writes into it)
OBJ = struct.Struct("<4fh2xf2h")          # enlight_obj_t: x_min x_max y_min y_max cls score img_w img_h
HEADER = struct.Struct("<4sIII")
POST_TYPES = {0: "none", 1: "class", 2: "obj", 3: "custom"}

_channel = None


def emit(obj):
    """One JSON line on the channel (the original stdout)."""
    data = (json.dumps(obj, separators=(",", ":")) + "\n").encode("utf-8")
    os.write(_channel, data)


def open_channel():
    """Keeps the original stdout for JSON and sends whatever C code prints to /dev/null."""
    global _channel
    sys.stdout.flush()
    _channel = os.dup(1)
    null = os.open("/dev/null", os.O_WRONLY)
    os.dup2(null, 1)
    os.close(null)


# ------------------------------------------------------------------------------------------------- the NPU library

def preload_libm():
    """A net.so the app built with its own C code may leave libm symbols to the loader: make them available."""
    try:
        ctypes.CDLL("libm.so.6", mode=ctypes.RTLD_GLOBAL)
    except OSError:
        pass


def load_library():
    preload_libm()
    lib = ctypes.CDLL(LIB)
    p, i = ctypes.c_void_p, ctypes.c_int
    signatures = {
        "npu_open": ([i], p), "npu_close": ([p], i),
        "network_load_from_file": ([p, ctypes.c_char_p, ctypes.c_char_p, ctypes.c_char_p], p),
        "network_get_input_width": ([p], i), "network_get_input_height": ([p], i),
        "network_get_output_size": ([p], i), "network_get_type": ([p], i),
        "buffer_alloc": ([p, i], p), "buffer_get_addr": ([p], p), "buffer_close": ([p], i),
        "network_run": ([p, p, p, p, i, p, i], i),
        "network_run_postprocess": ([p, p, p], i), "network_close": ([p], i),
    }
    for name, (args, res) in signatures.items():
        f = getattr(lib, name)
        f.argtypes, f.restype = args, res
    return lib


class Network:
    """One network on one NPU cluster, with its input and output buffers."""

    def __init__(self, lib, model_dir, cluster):
        self.lib, self.cluster = lib, cluster
        self.npu = self.net = self.inbuf = self.outbuf = None
        files = [os.path.join(os.path.abspath(model_dir), f) for f in MODEL_FILES]
        missing = [os.path.basename(f) for f in files if not os.path.isfile(f)]
        if missing:
            raise RunnerError("files", "missing in %s: %s" % (model_dir, ", ".join(missing)))
        self.npu = lib.npu_open(cluster)
        if not self.npu:
            raise RunnerError("npu", "could not open NPU cluster %d" % cluster)
        self.net = lib.network_load_from_file(self.npu, *[f.encode() for f in files])
        if not self.net:
            raise RunnerError("load", "the NPU library could not load the network")
        self.width = lib.network_get_input_width(self.net)
        self.height = lib.network_get_input_height(self.net)
        self.type = lib.network_get_type(self.net)
        # an RGB input buffer is as wide as the network rounded up to 16 pixels (tcnnapp does the same)
        self.stride = ((self.width + 15) // 16 * 16) * 3
        self.inbuf = lib.buffer_alloc(self.npu, self.stride * self.height)
        out_size = lib.network_get_output_size(self.net)
        self.outbuf = lib.buffer_alloc(self.npu, out_size)
        if not self.inbuf or not self.outbuf:
            raise RunnerError("buffer", "could not allocate NPU buffers (input %d, output %d bytes)" % (self.stride * self.height, out_size))
        self.inaddr = lib.buffer_get_addr(self.inbuf)
        ctypes.memset(self.inaddr, 0, self.stride * self.height)
        self.status = ctypes.create_string_buffer(64)        # npu_err_bits_t (a 32-bit word; room to spare)
        self.perf = (ctypes.c_uint * 4)()                     # npu_perf_t: elapsed_in_us, dma, comp, all
        self.result = ctypes.create_string_buffer(4 + MAX_DETECT_BOX * OBJ.size)

    def put(self, rgb):
        """Copies one packed RGB picture of the network's size into the input buffer."""
        row = self.width * 3
        if len(rgb) != row * self.height:
            raise RunnerError("frame", "a frame must be %dx%d RGB (%d bytes), got %d" % (self.width, self.height, row * self.height, len(rgb)))
        if row == self.stride:
            ctypes.memmove(self.inaddr, rgb, len(rgb))
        else:
            for y in range(self.height):
                ctypes.memmove(self.inaddr + y * self.stride, rgb[y * row:(y + 1) * row], row)

    def run(self):
        """One inference; the NPU's own time in ms, or raises."""
        rc = self.lib.network_run(self.net, self.inbuf, self.outbuf, self.status, NPU_RUN_ASYNC, self.perf, TIMEOUT_MS)
        if rc < 0:
            raise RunnerError("run", "network_run returned %d" % rc)
        return self.perf[0] / 1000.0

    def post(self):
        """Runs the network's post-processing (net.so); returns (result dict, ms)."""
        started = time.perf_counter()
        if self.type == 2:
            ctypes.memset(self.result, 0, 4)
            self.lib.network_run_postprocess(self.net, self.outbuf, self.result)
            ms = (time.perf_counter() - started) * 1e3
            count = struct.unpack_from("<i", self.result, 0)[0]
            objs = []
            for k in range(max(0, min(count, 1000))):
                x1, x2, y1, y2, cls, score, _, _ = OBJ.unpack_from(self.result, 4 + k * OBJ.size)
                objs.append([round(x1, 2), round(y1, 2), round(x2, 2), round(y2, 2), cls, round(score, 4)])
            return {"objs": objs}, ms
        if self.type == 1:
            ctypes.memset(self.result, 0, 32)
            self.lib.network_run_postprocess(self.net, self.outbuf, self.result)
            ms = (time.perf_counter() - started) * 1e3
            return {"cls": struct.unpack_from("<i", self.result, 0)[0]}, ms
        if self.type == 3:
            # "unknown": the network's own code runs (it may fill nothing the app can read)
            self.lib.network_run_postprocess(self.net, self.outbuf, self.result)
            return {}, (time.perf_counter() - started) * 1e3
        return {}, 0.0

    def close(self):
        lib = self.lib
        if self.net:
            lib.network_close(self.net)
        for buf in (self.inbuf, self.outbuf):
            if buf:
                lib.buffer_close(buf)
        if self.npu:
            lib.npu_close(self.npu)
        self.npu = self.net = self.inbuf = self.outbuf = None


class RunnerError(Exception):
    def __init__(self, stage, message):
        super().__init__(message)
        self.stage = stage


class BusyFile:
    """How long the NPU worked, for monitor: the total NPU time in microseconds, written about once a second."""

    def __init__(self, cluster):
        os.makedirs(STATE_DIR, exist_ok=True)
        self.path = os.path.join(STATE_DIR, "npu%d.json" % cluster)
        self.busy_us = 0
        self.frames = 0
        self.written = 0.0

    def add(self, ms):
        self.busy_us += int(ms * 1000)
        self.frames += 1
        now = time.time()
        if now - self.written >= 1.0:
            self.write(now)

    def write(self, now=None):
        now = now or time.time()
        tmp = self.path + ".tmp"
        with open(tmp, "w") as f:
            json.dump({"busyUs": self.busy_us, "frames": self.frames, "t": now, "pid": os.getpid()}, f)
        os.replace(tmp, self.path)
        self.written = now

    def remove(self):
        try:
            os.remove(self.path)
        except OSError:
            pass


def read_exact(stream, n):
    data = bytearray()
    while len(data) < n:
        chunk = stream.read(n - len(data))
        if not chunk:
            return None
        data += chunk
    return bytes(data)


def cmd_serve(args):
    open_channel()
    lib = load_library()
    net = None
    busy = BusyFile(args.npu)
    try:
        net = Network(lib, args.model, args.npu)
        emit({"ready": True, "version": VERSION, "npu": args.npu, "w": net.width, "h": net.height,
              "type": POST_TYPES.get(net.type, str(net.type))})
        stdin = sys.stdin.buffer
        frames = 0
        while True:
            header = read_exact(stdin, HEADER.size)
            if header is None:
                break
            magic, seq, length, kind = HEADER.unpack(header)
            if magic != b"TPF1":
                emit({"error": "frame header", "stage": "frame"})
                return 2
            payload = read_exact(stdin, length) if length else b""
            if payload is None or kind == 1:
                break
            try:
                net.put(payload)
                started = time.perf_counter()
                npu_ms = net.run()
                result, post_ms = net.post()
                wall_ms = (time.perf_counter() - started) * 1e3
            except RunnerError as e:
                emit({"seq": seq, "error": str(e), "stage": e.stage})
                continue
            busy.add(npu_ms)
            frames += 1
            line = {"seq": seq, "ms": round(npu_ms, 3), "post": round(post_ms, 3), "wall": round(wall_ms, 3)}
            line.update(result)
            emit(line)
        emit({"done": True, "frames": frames})
        return 0
    except RunnerError as e:
        emit({"error": str(e), "stage": e.stage})
        return 2
    finally:
        busy.remove()
        if net is not None:
            net.close()


def cmd_bench(args):
    open_channel()
    lib = load_library()
    net = None
    busy = BusyFile(args.npu)
    try:
        net = Network(lib, args.model, args.npu)
        times = []
        for _ in range(args.count):
            ms = net.run()
            times.append(ms)
            busy.add(ms)
        times.sort()
        emit({"count": len(times), "avg": round(sum(times) / len(times), 3), "min": round(times[0], 3),
              "max": round(times[-1], 3), "median": round(times[len(times) // 2], 3), "npu": args.npu,
              "w": net.width, "h": net.height})
        return 0
    except RunnerError as e:
        emit({"error": str(e), "stage": e.stage})
        return 2
    finally:
        busy.remove()
        if net is not None:
            net.close()


def cmd_soak(args):
    """Runs the network without a pause for --seconds; a line every --every seconds: runs, average time, temperature."""
    open_channel()
    lib = load_library()
    net = None
    busy = BusyFile(args.npu)
    try:
        net = Network(lib, args.model, args.npu)
        start = time.time()
        end = start + args.seconds
        report = start + args.every
        runs, window, window_ms = 0, 0, 0.0
        emit({"t": 0, "runs": 0, "avg": 0, "temp": max_temperature(), "npu": args.npu})
        while True:
            ms = net.run()
            busy.add(ms)
            runs += 1
            window += 1
            window_ms += ms
            now = time.time()
            if now >= report or now >= end:
                emit({"t": round(now - start, 1), "runs": runs, "avg": round(window_ms / window, 3), "temp": max_temperature()})
                window, window_ms = 0, 0.0
                report = now + args.every
            if now >= end:
                break
        emit({"done": True, "runs": runs, "seconds": round(time.time() - start, 1)})
        return 0
    except RunnerError as e:
        emit({"error": str(e), "stage": e.stage})
        return 2
    finally:
        busy.remove()
        if net is not None:
            net.close()


# ------------------------------------------------------------------------------------------------- board status

def read_text(path, default=""):
    try:
        with open(path) as f:
            return f.read().strip()
    except OSError:
        return default


def cpu_times():
    cores = {}
    for line in read_text("/proc/stat").splitlines():
        parts = line.split()
        if parts and parts[0].startswith("cpu") and parts[0] != "cpu":
            values = [int(v) for v in parts[1:9]]
            idle = values[3] + values[4]          # idle + iowait
            cores[int(parts[0][3:])] = (sum(values), idle)
    return cores


def meminfo():
    info = {}
    for line in read_text("/proc/meminfo").splitlines():
        key, _, rest = line.partition(":")
        parts = rest.split()
        if parts:
            info[key] = int(parts[0]) * 1024
    return info


def default_interface():
    for line in read_text("/proc/net/route").splitlines()[1:]:
        parts = line.split()
        if len(parts) > 2 and parts[1] == "00000000":
            return parts[0]
    return ""


def npu_busy(cluster, previous):
    """Percent of the last interval the NPU cluster spent on our runner's inferences (0 when nothing runs)."""
    try:
        with open(os.path.join(STATE_DIR, "npu%d.json" % cluster)) as f:
            state = json.load(f)
    except (OSError, ValueError):
        previous.pop(cluster, None)
        return 0
    now = time.time()
    if now - state.get("t", 0) > 3:
        previous.pop(cluster, None)
        return 0
    last = previous.get(cluster)
    previous[cluster] = (state["busyUs"], state["t"])
    if not last or state["t"] <= last[1]:
        return 0
    return max(0, min(100, round((state["busyUs"] - last[0]) / ((state["t"] - last[1]) * 1e6) * 100)))


def temperatures():
    temps = []
    for zone in sorted(glob.glob("/sys/class/thermal/thermal_zone*")):
        t = read_text(zone + "/temp")
        if t.lstrip("-").isdigit():
            temps.append({"name": read_text(zone + "/type", os.path.basename(zone)), "c": round(int(t) / 1000.0, 1)})
    return temps


def max_temperature():
    temps = temperatures()
    return max(t["c"] for t in temps) if temps else 0


def status_line(prev_cpu, prev_npu):
    cpu = cpu_times()
    cores = []
    for k in sorted(cpu):
        total, idle = cpu[k]
        if k in prev_cpu and total > prev_cpu[k][0]:
            dt, di = total - prev_cpu[k][0], idle - prev_cpu[k][1]
            cores.append(round(max(0.0, 100.0 * (dt - di) / dt), 1))
        else:
            cores.append(0.0)
    mem = meminfo()
    temps = temperatures()
    root, tmp = os.statvfs("/"), os.statvfs("/tmp")
    iface = default_interface()
    speed = read_text("/sys/class/net/%s/speed" % iface) if iface else ""
    os_name = ""
    for line in read_text("/etc/os-release").splitlines():
        if line.startswith("PRETTY_NAME="):
            os_name = line.split("=", 1)[1].strip().strip('"')
    npus = []
    for cluster in (0, 1):
        enabled = read_text("/sys/kernel/debug/clk/npubus%d_core/clk_enable_count" % cluster)
        npus.append({"on": enabled.isdigit() and int(enabled) > 0, "busy": npu_busy(cluster, prev_npu)})
    return cpu, {
        "t": round(time.time(), 1), "host": socket.gethostname(), "os": os_name,
        "uptime": int(float(read_text("/proc/uptime", "0 0").split()[0])),
        "cpu": cores, "cpuAll": round(sum(cores) / len(cores), 1) if cores else 0,
        "mem": {"total": mem.get("MemTotal", 0), "avail": mem.get("MemAvailable", 0)},
        "npuMem": {"total": mem.get("CmaTotal", 0), "free": mem.get("CmaFree", 0)},
        "temps": temps,
        "disk": {"total": root.f_blocks * root.f_frsize, "free": root.f_bavail * root.f_frsize},
        "tmp": {"total": tmp.f_blocks * tmp.f_frsize, "free": tmp.f_bavail * tmp.f_frsize},
        "net": {"name": iface, "mbps": int(speed) if speed.lstrip("-").isdigit() else 0},
        "npu": npus,
        # what a model needs on the board: the NPU library and the two NPU devices
        "lib": any(os.path.exists(os.path.join(d, LIB)) for d in LIB_DIRS),
        "dev": [os.path.exists("/dev/npu%d" % n) for n in (0, 1)],
        "runner": VERSION,
    }


def cmd_monitor(args):
    prev_cpu, prev_npu = cpu_times(), {}
    time.sleep(min(0.5, args.interval))
    count = 0
    while True:
        prev_cpu, line = status_line(prev_cpu, prev_npu)
        sys.stdout.write(json.dumps(line, separators=(",", ":")) + "\n")
        sys.stdout.flush()
        count += 1
        if args.once or (args.count and count >= args.count):
            return 0
        time.sleep(args.interval)


# ------------------------------------------------------------------------------------------------- models

class NetworkInstance(ctypes.Structure):     # the start of enlight_network_t (network.h of the toolkit)
    _fields_ = [("network_name", ctypes.c_char_p), ("post_proc", ctypes.c_int), ("batch_size", ctypes.c_int),
                ("img_sizes", ctypes.c_int * 3)]


def describe(folder):
    """What a model folder holds, read from its net.so without touching the NPU."""
    entry = {"path": folder, "name": os.path.basename(folder.rstrip("/"))}
    so = os.path.join(folder, "net.so")
    try:
        net = ctypes.CDLL(so)
        inst = NetworkInstance.in_dll(net, "network_instance")
        entry["type"] = POST_TYPES.get(inst.post_proc, str(inst.post_proc))
        entry["input"] = [inst.img_sizes[1], inst.img_sizes[2]] if inst.img_sizes[1] and inst.img_sizes[2] else []
    except OSError as e:
        entry["error"] = str(e)
    entry["bytes"] = sum(os.path.getsize(os.path.join(folder, f)) for f in MODEL_FILES if os.path.isfile(os.path.join(folder, f)))
    manifest = os.path.join(folder, "topst.json")
    if os.path.isfile(manifest):
        try:
            with open(manifest) as f:
                entry["manifest"] = json.load(f)
        except (OSError, ValueError):
            pass
    try:
        entry["mtime"] = int(os.path.getmtime(os.path.join(folder, "net.so")))
    except OSError:
        pass
    return entry


def cmd_models(args):
    preload_libm()
    found = []
    for folder in sorted(glob.glob(os.path.join(args.root, "*"))):
        if os.path.isdir(folder) and os.path.isfile(os.path.join(folder, "net.so")):
            entry = describe(folder)
            entry["app"] = True
            found.append(entry)
    for folder in sorted(glob.glob("/usr/share/*_quantized")):
        if os.path.isfile(os.path.join(folder, "net.so")):
            entry = describe(folder)
            entry["app"] = False
            found.append(entry)
    print(json.dumps({"models": found}, separators=(",", ":")))
    return 0


def main():
    ap = argparse.ArgumentParser(prog="topst-runner")
    sub = ap.add_subparsers(dest="cmd")
    sub.add_parser("version")
    m = sub.add_parser("monitor")
    m.add_argument("--interval", type=float, default=2.0)
    m.add_argument("--once", action="store_true")
    m.add_argument("--count", type=int, default=0)
    for name in ("serve", "bench", "soak"):
        s = sub.add_parser(name)
        s.add_argument("--model", required=True)
        s.add_argument("--npu", type=int, choices=(0, 1), default=0)
        if name == "bench":
            s.add_argument("--count", type=int, default=100)
        if name == "soak":
            s.add_argument("--seconds", type=float, required=True)
            s.add_argument("--every", type=float, default=5.0)
    md = sub.add_parser("models")
    md.add_argument("--root", default=DEFAULT_ROOT)
    args = ap.parse_args()
    if args.cmd == "version":
        print(VERSION)
        return 0
    if args.cmd == "monitor":
        return cmd_monitor(args)
    if args.cmd == "serve":
        return cmd_serve(args)
    if args.cmd == "bench":
        return cmd_bench(args)
    if args.cmd == "soak":
        return cmd_soak(args)
    if args.cmd == "models":
        return cmd_models(args)
    ap.print_help()
    return 2


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        sys.exit(130)
