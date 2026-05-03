from ultralytics import YOLO
from pathlib import Path

model_path = 'yolov8n.pt'
images_dir = Path('tools') / 'piano_vision_training' / 'datasets' / 'piano_dataset' / 'images' / 'train'

model = YOLO(model_path)
print('Running baseline inference (yolov8n.pt)')
results = model.predict(source=str(images_dir), imgsz=640, conf=0.1, save=False)
cnt = 0
for r in results:
    boxes = getattr(r, 'boxes', None)
    n = 0
    if boxes is not None:
        try:
            n = len(boxes.xyxy) if hasattr(boxes, 'xyxy') else len(boxes)
        except Exception:
            n = len(boxes)
    print(f'{getattr(r,"path",None)}: detections={n}')
    cnt += n
print('Total detections:', cnt)
