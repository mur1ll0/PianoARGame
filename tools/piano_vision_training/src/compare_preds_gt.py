from ultralytics import YOLO
from pathlib import Path
from PIL import Image
import numpy as np

def xywh_norm_to_xyxy(cx, cy, w, h, img_w, img_h):
    x1 = (cx - w/2) * img_w
    y1 = (cy - h/2) * img_h
    x2 = (cx + w/2) * img_w
    y2 = (cy + h/2) * img_h
    return [x1, y1, x2, y2]


def points_norm_to_xyxy(points, img_w, img_h):
    xs = [p[0] for p in points]
    ys = [p[1] for p in points]
    return [min(xs) * img_w, min(ys) * img_h, max(xs) * img_w, max(ys) * img_h]


def parse_gt_line_to_xyxy(line: str, img_w: int, img_h: int):
    parts = line.strip().split()
    if len(parts) < 5:
        return None

    try:
        vals = [float(v) for v in parts[1:]]
    except ValueError:
        return None

    if len(vals) == 4:
        cx, cy, w, h = vals
        return xywh_norm_to_xyxy(cx, cy, w, h, img_w, img_h)

    if len(vals) >= 6 and len(vals) % 2 == 0:
        points = [(vals[i], vals[i + 1]) for i in range(0, len(vals), 2)]
        if len(points) >= 2 and points[0] == points[-1]:
            points = points[:-1]
        if len(points) >= 3:
            return points_norm_to_xyxy(points, img_w, img_h)

    return None

def iou(boxA, boxB):
    xA = max(boxA[0], boxB[0])
    yA = max(boxA[1], boxB[1])
    xB = min(boxA[2], boxB[2])
    yB = min(boxA[3], boxB[3])
    interW = max(0, xB - xA)
    interH = max(0, yB - yA)
    inter = interW * interH
    areaA = max(0, boxA[2]-boxA[0]) * max(0, boxA[3]-boxA[1])
    areaB = max(0, boxB[2]-boxB[0]) * max(0, boxB[3]-boxB[1])
    union = areaA + areaB - inter
    return inter/union if union>0 else 0.0

base = Path('tools') / 'piano_vision_training' / 'datasets' / 'piano_dataset'
labels_dir = base / 'labels' / 'val'
images_dir = base / 'images' / 'val'
model = YOLO(str(Path('tools') / 'piano_vision_training' / 'runs' / 'detect' / 'runs' / 'piano_detector_candidateB' / 'weights' / 'best.pt'))

results = model.predict(source=str(images_dir), imgsz=640, conf=0.001, save=False)

summary = []
for r in results:
    img_path = Path(r.path)
    stem = img_path.stem
    label_file = labels_dir / (stem + '.txt')
    img = Image.open(img_path)
    iw, ih = img.size
    gt_boxes = []
    if label_file.exists():
        with open(label_file, 'r') as fh:
            for line in fh:
                box = parse_gt_line_to_xyxy(line, iw, ih)
                if box is not None:
                    gt_boxes.append(box)
    pred_boxes = []
    if hasattr(r, 'boxes') and r.boxes is not None:
        try:
            xyxy = r.boxes.xyxy.cpu().numpy()
        except Exception:
            xyxy = np.array(r.boxes.xyxy)
        pred_boxes = xyxy.tolist()

    max_iou = 0.0
    for pb in pred_boxes:
        for gb in gt_boxes:
            max_iou = max(max_iou, iou(pb, gb))

    summary.append((img_path.name, len(gt_boxes), len(pred_boxes), max_iou))

for name, ngt, npred, miou in summary:
    print(f'{name}: GT={ngt}, pred={npred}, maxIoU={miou:.3f}')

avg_iou = sum(s[3] for s in summary)/len(summary) if summary else 0.0
count_ge_50 = sum(1 for s in summary if s[3] >= 0.5)
print('Average maxIoU:', avg_iou)
print(f'Images with maxIoU >= 0.5: {count_ge_50}/{len(summary)}')
