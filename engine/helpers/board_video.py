"""Draws the board's boxes onto a video and writes an mp4. Runs with the engine's Python: OpenCV (with its own
FFmpeg) reads and writes the video, Pillow draws the labels so that Korean names show.

    python board_video.py --video in.mp4 --results results.jsonl --labels labels.json --font font.otf --out out.mp4

results.jsonl: one line per frame the board ran, {"i": frame index, "objs": [[x1, y1, x2, y2, cls, score], ...]},
in the video's own pixels. Frames in between (a run that sent every n-th frame) keep the boxes of the frame before.
labels.json: {"<cls>": "name", ...}. Progress: JSON lines {"done": n, "total": m} on stdout; the last one holds "out".
"""
import argparse
import json
import sys

import cv2
import numpy as np
from PIL import Image, ImageDraw, ImageFont

# the result screen's colours (DetectionView), by class number
PALETTE = [(61, 138, 237), (217, 119, 6), (30, 158, 117), (200, 49, 42), (113, 132, 253), (219, 39, 119),
           (13, 148, 136), (202, 138, 4), (124, 58, 237), (5, 150, 105)]


def colour(cls):
    return PALETTE[(cls - 1) % len(PALETTE)] if cls > 0 else PALETTE[0]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--video", required=True)
    ap.add_argument("--results", required=True)
    ap.add_argument("--labels", required=True)
    ap.add_argument("--font", required=True)
    ap.add_argument("--out", required=True)
    args = ap.parse_args()

    with open(args.labels, encoding="utf-8") as f:
        labels = {int(k): v for k, v in json.load(f).items()}
    boxes = {}
    with open(args.results, encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if line:
                r = json.loads(line)
                boxes[int(r["i"])] = r.get("objs") or []

    cap = cv2.VideoCapture(args.video)
    if not cap.isOpened():
        print(json.dumps({"error": "could not open the video"}), flush=True)
        return 2
    fps = cap.get(cv2.CAP_PROP_FPS) or 30.0
    total = int(cap.get(cv2.CAP_PROP_FRAME_COUNT) or 0)
    width, height = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH)), int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT))
    writer = cv2.VideoWriter(args.out, cv2.VideoWriter_fourcc(*"mp4v"), fps, (width, height))
    if not writer.isOpened():
        print(json.dumps({"error": "could not write the video"}), flush=True)
        return 2
    size = max(14, int(round(min(width, height) / 36)))
    font = ImageFont.truetype(args.font, size)
    thickness = max(2, int(round(min(width, height) / 360)))

    current = []
    index = 0
    while True:
        ok, frame = cap.read()
        if not ok:
            break
        if index in boxes:
            current = boxes[index]
        if current:
            img = Image.fromarray(cv2.cvtColor(frame, cv2.COLOR_BGR2RGB))
            draw = ImageDraw.Draw(img)
            for x1, y1, x2, y2, cls, score in current:
                c = colour(int(cls))
                draw.rectangle([x1, y1, x2, y2], outline=c, width=thickness)
                text = "%s %d%%" % (labels.get(int(cls), str(cls)), int(round(score * 100)))
                tw, th = draw.textsize(text, font=font)
                top = max(0, y1 - th - 6)
                draw.rectangle([x1, top, x1 + tw + 10, top + th + 6], fill=c)
                draw.text((x1 + 5, top + 2), text, fill=(255, 255, 255), font=font)
            frame = cv2.cvtColor(np.asarray(img), cv2.COLOR_RGB2BGR)
        writer.write(frame)
        index += 1
        if index % 30 == 0:
            print(json.dumps({"done": index, "total": total}), flush=True)
    cap.release()
    writer.release()
    print(json.dumps({"done": index, "total": total, "out": args.out}), flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
