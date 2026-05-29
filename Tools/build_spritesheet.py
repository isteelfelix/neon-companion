import cv2
import math
import json
from pathlib import Path
from PIL import Image
from rembg import remove, new_session

INPUT_DIR  = r"D:\dw"
OUTPUT_DIR = r"D:\Proj\neon-companion\Assets\UI\Avatars"
STATES     = ["thinking", "talking", "listening", "smile", "confused"]
FRAME_W    = 512
FRAME_H    = 683
TARGET_FPS = 12
COLS       = 8

print("Loading rembg model...", flush=True)
session = new_session()

for state in STATES:
    video_path = str(Path(INPUT_DIR) / f"{state}.mp4")
    out_png    = str(Path(OUTPUT_DIR) / f"{state}_sheet.png")
    out_json   = str(Path(OUTPUT_DIR) / f"{state}_sheet.json")

    print(f"\n{'='*40}")
    print(f"  {state.upper()}: {video_path}")
    print(f"{'='*40}", flush=True)

    cap          = cv2.VideoCapture(video_path)
    source_fps   = cap.get(cv2.CAP_PROP_FPS)
    total_frames = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))
    step         = max(1, round(source_fps / TARGET_FPS))
    expected     = total_frames // step

    print(f"  {source_fps:.0f}fps -> step {step} -> ~{expected} frames", flush=True)

    frames = []
    idx    = 0

    while True:
        ret, frame = cap.read()
        if not ret:
            break
        if idx % step == 0:
            rgb    = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)
            img    = Image.fromarray(rgb).resize((FRAME_W, FRAME_H), Image.LANCZOS)
            result = remove(img, session=session)
            frames.append(result)
            print(f"  [{len(frames):>3}/{expected}] done", flush=True)
        idx += 1

    cap.release()

    num_frames = len(frames)
    rows  = math.ceil(num_frames / COLS)
    sheet = Image.new("RGBA", (FRAME_W * COLS, FRAME_H * rows), (0, 0, 0, 0))

    for i, f in enumerate(frames):
        sheet.paste(f, ((i % COLS) * FRAME_W, (i // COLS) * FRAME_H))

    sheet.save(out_png, "PNG")
    Path(out_json).write_text(json.dumps({
        "frameWidth":  FRAME_W,
        "frameHeight": FRAME_H,
        "cols":        COLS,
        "rows":        rows,
        "frameCount":  num_frames,
        "fps":         TARGET_FPS,
    }, indent=2))

    size_mb = Path(out_png).stat().st_size / 1_048_576
    print(f"  Saved: {out_png}  ({size_mb:.1f} MB)", flush=True)

print("\nAll done!")
