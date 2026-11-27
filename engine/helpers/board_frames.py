"""Pictures and video frames for the board runner (engine/board/topst_runner.py), prepared the way the toolkit's
simulator prepares a picture, so the board gets what the PC simulation got:

  EnlightSDK/simulator/transforms/detector_transforms.py
    LetterBox: pads the picture to a square of its longer side with (123, 116, 103), centred
               (left/top get the smaller half), before the resize
    Resize:    PIL bilinear to the network's width x height

Runs with the engine's Python (its Pillow and OpenCV).

    python board_frames.py images --w 640 --h 640 [--letterbox] a.png b.jpg ...
    python board_frames.py video  --w 640 --h 640 [--letterbox] --video in.mp4 [--every 3] [--preview 640]
    python board_frames.py probe  in.mp4        one JSON line (text): {"W", "H", "fps", "frames"} or {"error"}

stdout is binary. Each record: b"TBF1", uint32 JSON length, the JSON, uint32 data length, the RGB data (w*h*3,
rows packed), uint32 preview length, the preview (a JPEG of the original frame, video only). A video starts with
one record whose JSON holds "video" (size, frames per second, frame count) and no data. Errors: a record whose JSON
holds "error". The last record holds "end".
"""
import argparse
import json
import os
import struct
import sys

from PIL import Image, ImageOps

FILL = (123, 116, 103)
MAGIC = b"TBF1"


def prepare(img, w, h, letterbox):
    """(packed RGB bytes of w x h, the padding left/top/right/bottom added before the resize)."""
    img = img.convert("RGB")
    width, height = img.size
    pad = (0, 0, 0, 0)
    if letterbox and width != height:
        side = max(width, height)
        pw, ph = side - width, side - height
        pad = (pw // 2, ph // 2, pw - pw // 2, ph - ph // 2)
        img = ImageOps.expand(img, pad, fill=FILL)
    return img.resize((w, h), Image.BILINEAR).tobytes(), pad


def write_record(out, info, data=b"", preview=b""):
    text = json.dumps(info, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    out.write(MAGIC + struct.pack("<I", len(text)) + text + struct.pack("<I", len(data)))
    if data:
        out.write(data)
    out.write(struct.pack("<I", len(preview)))
    if preview:
        out.write(preview)
    out.flush()


def cmd_images(args, out):
    for index, path in enumerate(args.files):
        try:
            with Image.open(path) as img:
                width, height = img.size
                data, pad = prepare(img, args.w, args.h, args.letterbox)
        except (OSError, ValueError) as e:
            write_record(out, {"i": index, "src": path, "error": str(e)})
            continue
        write_record(out, {"i": index, "src": path, "W": width, "H": height, "pad": list(pad),
                           "w": args.w, "h": args.h}, data)
    write_record(out, {"end": True})


def cmd_video(args, out):
    import cv2   # the engine's OpenCV, with its own FFmpeg

    cap = cv2.VideoCapture(args.video)
    if not cap.isOpened():
        write_record(out, {"error": "could not open the video", "src": args.video})
        return 2
    fps = cap.get(cv2.CAP_PROP_FPS) or 30.0
    count = int(cap.get(cv2.CAP_PROP_FRAME_COUNT) or 0)
    width = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH))
    height = int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT))
    every = max(1, args.every)
    write_record(out, {"video": {"src": args.video, "W": width, "H": height, "fps": round(fps, 3), "frames": count,
                                 "every": every}})
    index = 0
    sent = 0
    while True:
        if index % every:
            if not cap.grab():
                break
            index += 1
            continue
        ok, frame = cap.read()
        if not ok:
            break
        rgb = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)
        data, pad = prepare(Image.fromarray(rgb), args.w, args.h, args.letterbox)
        preview = b""
        if args.preview:
            scale = args.preview / float(max(width, height))
            small = cv2.resize(frame, (max(1, int(width * scale)), max(1, int(height * scale))), interpolation=cv2.INTER_AREA) if scale < 1 else frame
            ok, jpg = cv2.imencode(".jpg", small, [int(cv2.IMWRITE_JPEG_QUALITY), 82])
            preview = jpg.tobytes() if ok else b""
        write_record(out, {"i": index, "t": round(index / fps, 3), "W": width, "H": height, "pad": list(pad),
                           "w": args.w, "h": args.h}, data, preview)
        sent += 1
        index += 1
    cap.release()
    write_record(out, {"end": True, "frames": sent, "read": index})
    return 0


def cmd_probe(args):
    import cv2

    cap = cv2.VideoCapture(args.video)
    if not cap.isOpened():
        print(json.dumps({"error": "could not open the video", "src": args.video}))
        return 2
    info = {"src": args.video, "W": int(cap.get(cv2.CAP_PROP_FRAME_WIDTH)), "H": int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT)),
            "fps": round(cap.get(cv2.CAP_PROP_FPS) or 30.0, 3), "frames": int(cap.get(cv2.CAP_PROP_FRAME_COUNT) or 0)}
    ok, frame = cap.read()
    cap.release()
    if not ok:
        print(json.dumps({"error": "no frame in the video", "src": args.video}))
        return 2
    print(json.dumps(info, ensure_ascii=False))
    return 0


def main():
    ap = argparse.ArgumentParser()
    sub = ap.add_subparsers(dest="cmd")
    sub.add_parser("probe").add_argument("video")
    for name in ("images", "video"):
        p = sub.add_parser(name)
        p.add_argument("--w", type=int, required=True)
        p.add_argument("--h", type=int, required=True)
        p.add_argument("--letterbox", action="store_true")
        if name == "images":
            p.add_argument("files", nargs="+")
        else:
            p.add_argument("--video", required=True)
            p.add_argument("--every", type=int, default=1, help="send every n-th frame")
            p.add_argument("--preview", type=int, default=0, help="longer side of the preview JPEG, 0 = none")
    args = ap.parse_args()
    if args.cmd == "probe":
        return cmd_probe(args)
    out = sys.stdout.buffer
    try:
        if args.cmd == "images":
            cmd_images(args, out)
            return 0
        if args.cmd == "video":
            return cmd_video(args, out)
    except BrokenPipeError:
        return 0       # the app stopped reading: the run was stopped
    ap.print_help(sys.stderr)
    return 2


if __name__ == "__main__":
    sys.exit(main())
