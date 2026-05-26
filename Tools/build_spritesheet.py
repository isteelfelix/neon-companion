import cv2
import math
import json
from pathlib import Path
from PIL import Image
from rembg import remove, new_session

VIDEO_PATH  = r"C:\Users\iFlex\Downloads\animation.mp4"
OUTPUT_DIR  = r"D:\Proj\neon-companion\Assets\UI\Avatars"
OUTPUT_NAME = "animation_idle_sheet.png"
META_NAME   = "animation_idle_sheet.json"
FRAME_W     = 512
FRAME_H     = 683
TARGET_FPS  = 12
COLS        = 8

cap = cv2.VideoCapture(VIDEO_PATH)
source_fps   = cap.get(cv2.CAP_PROP_FPS)
total_frames = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))
step         = max(1, round(source_fps / TARGET_FPS))
expected     = total_frames // step

print(f"Source: {source_fps}fps  |  step: every {step} frames  |  ~{expected} output frames")
print(f"Frame size: {FRAME_W}x{FRAME_H}  |  Cols: {COLS}")
print("Loading rembg model...", flush=True)

session = new_session()
frames  = []
idx     = 0

while True:
    ret, frame = cap.read()
    if not ret:
        break
    if idx % step == 0:
        rgb = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)
        img = Image.fromarray(rgb).resize((FRAME_W, FRAME_H), Image.LANCZOS)
        result = remove(img, session=session)
        frames.append(result)
        print(f"  [{len(frames):>3}/{expected}] frame extracted", flush=True)
    idx += 1

cap.release()

num_frames = len(frames)
rows  = math.ceil(num_frames / COLS)
sheet = Image.new("RGBA", (FRAME_W * COLS, FRAME_H * rows), (0, 0, 0, 0))

for i, f in enumerate(frames):
    sheet.paste(f, ((i % COLS) * FRAME_W, (i // COLS) * FRAME_H))

out_png  = str(Path(OUTPUT_DIR) / OUTPUT_NAME)
out_json = str(Path(OUTPUT_DIR) / META_NAME)

sheet.save(out_png, "PNG")

Path(out_json).write_text(json.dumps({
    "frameWidth":  FRAME_W,
    "frameHeight": FRAME_H,
    "cols":        COLS,
    "rows":        rows,
    "frameCount":  num_frames,
    "fps":         TARGET_FPS,
}, indent=2))

print(f"\nDone!  {num_frames} frames in {COLS}x{rows} grid")
print(f"Sheet: {out_png}")
print(f"Meta:  {out_json}")
