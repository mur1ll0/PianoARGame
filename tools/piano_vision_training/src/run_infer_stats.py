from ultralytics import YOLO
from pathlib import Path

model_path = Path('tools') / 'piano_vision_training' / 'runs' / 'detect' / 'runs' / 'piano_detector_candidateB' / 'weights' / 'best.pt'
images_dir = Path('tools') / 'piano_vision_training' / 'datasets' / 'piano_dataset' / 'images' / 'train'

model = YOLO(str(model_path))
print('Running inference (conf=0.1) on training images...')
results = model.predict(source=str(images_dir), imgsz=640, conf=0.1, save=False)
for r in results:
    imgpath = getattr(r, 'path', None)
    boxes = getattr(r, 'boxes', None)
    n = 0
    if boxes is not None:
        try:
            n = len(boxes.xyxy) if hasattr(boxes, 'xyxy') else len(boxes)
        except Exception:
            n = len(boxes)
    print(f'{imgpath}: detections={n}')
