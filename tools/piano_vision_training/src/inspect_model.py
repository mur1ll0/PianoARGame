from ultralytics import YOLO
from pathlib import Path

model_path = Path('tools') / 'piano_vision_training' / 'runs' / 'detect' / 'runs' / 'piano_detector_candidateB' / 'weights' / 'best.pt'
model = YOLO(str(model_path))
print('Model names:', model.names)
try:
    print('Model class count (nc):', model.model.model[-1].nc)
except Exception as e:
    print('Could not read nc:', e)
