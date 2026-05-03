from ultralytics import YOLO
from pathlib import Path

# Resolve base to the piano_vision_training folder (two levels up: src -> piano_vision_training)
base = Path(__file__).resolve().parents[1]
model_path = base / 'runs' / 'detect' / 'runs' / 'piano_detector_candidateB' / 'weights' / 'best.pt'
images_dir = base / 'test_images'
out_dir = base / 'runs' / 'debug_preds'

out_dir.mkdir(parents=True, exist_ok=True)

print('Using model:', model_path)
print('Images dir:', images_dir)
print('Out dir:', out_dir)

model = YOLO(str(model_path))

# run predict on all images
results = model.predict(source=str(images_dir), imgsz=640, conf=0.25, save=True, project=str(out_dir), name='preds', exist_ok=True)
print('Done. Results saved in', out_dir)
