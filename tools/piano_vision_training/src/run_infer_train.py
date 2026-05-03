from ultralytics import YOLO
from pathlib import Path

model_path = Path('tools') / 'piano_vision_training' / 'runs' / 'detect' / 'runs' / 'piano_detector_candidateB' / 'weights' / 'best.pt'
images_dir = Path('tools') / 'piano_vision_training' / 'datasets' / 'piano_dataset' / 'images' / 'train'
out_dir = Path('tools') / 'piano_vision_training' / 'runs' / 'debug_preds'

out_dir.mkdir(parents=True, exist_ok=True)

print('Using model:', model_path)
print('Images dir:', images_dir)
print('Out dir:', out_dir)

model = YOLO(str(model_path))
results = model.predict(source=str(images_dir), imgsz=640, conf=0.1, save=True, project=str(out_dir), name='train_conf01', exist_ok=True)
print('Done.')
