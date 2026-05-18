PianoARGame
=================

Overview
--------
PianoARGame is an Augmented Reality (AR) learning game that helps users learn piano by overlaying interactive visual guidance on a real keyboard. The project includes Unity scenes, training tools for computer-vision models that detect the keyboard area, and runtime integration for ONNX models used inside the Unity application.

![pianoAR](pianoAR.gif)

Dependencies
------------
- Unity: 6.4.1f1 (project tested/targeted for Unity 6.4 1f1)
- Platform targets: Android (primary), Windows (supported)
- Python (for training & tools) — recommended 3.10+ for training scripts
- Optional: Ultralytics / YOLOv8 or your chosen training stack for detection model training

Roboflow model reference
------------------------
The current model used/optimized for keyboard area detection is published at Roboflow:

https://app.roboflow.com/murillos-workspace-ukdsd/find-keyboard_area/7

Training dataset and config
---------------------------
This project uses the dataset exported from the Roboflow model above as the basis for local training and optimization. The training configuration used is in:

`tools/piano_vision_training/configs/train_config_unity_focus_nms.yaml`

That YAML contains hyperparameters / augmentations / NMS settings used to produce the detector used in the game. To reproduce or continue training locally, follow the steps below.

How the model was generated (example workflow)
--------------------------------------------
1. Export dataset from Roboflow in YOLOv8 (or YOLO) format and download it. Place the exported dataset in:

   `tools/piano_vision_training/datasets/keyboard_area/`

   The dataset should contain a `data.yaml` (or `dataset.yaml`) manifest pointing to `train/`, `val/`, and `names`.

2. Install Python dependencies (example):

   ```powershell
   cd tools/piano_vision_training
   pip install -r requirements.txt
   ```

3. Train using the provided config (example using Ultralytics YOLOv8):

   ```bash
   # example (adapt to your training tool):
   python -m ultralytics.yolo.train data=tools/piano_vision_training/datasets/keyboard_area/data.yaml \
       model=yolov8n.pt cfg=tools/piano_vision_training/configs/train_config_unity_focus_nms.yaml \
       epochs=100 imgsz=640 project=outputs/train_unity_focus_nms
   ```

   - If you use a different training entrypoint, point it to the same `data` and `cfg`/`config` file.

4. Export the trained model to ONNX for Unity runtime (example Ultralytics export):

   ```bash
   # adjust path to the produced best.pt
   python -m ultralytics export model=outputs/train_unity_focus_nms/weights/best.pt format=onnx
   ```

5. The exported ONNX file (for example `keyboard_area.onnx`) is the artifact to copy into Unity as described below.

Compiling / running the tools in `tools/piano_vision_training`
-----------------------------------------------------------
- The scripts under `tools/piano_vision_training` are standard Python utilities for dataset handling, training, conversion, and evaluation.
- Typical commands:

  ```powershell
  cd tools/piano_vision_training
  pip install -r requirements.txt
  python train.py --config configs/train_config_unity_focus_nms.yaml
  python export.py --weights outputs/train_unity_focus_nms/weights/best.pt --format onnx
  ```

- If the project does not include `train.py` / `export.py`, use your training tool's CLI and point it to the `data` and `config` files in this folder. See the example Ultralytics commands above.

Where to place the model inside the Unity project
-----------------------------------------------
- Place the exported ONNX model file into the Unity assets folder: `Assets/AIModels/`.

  Example path in Unity project view: `Assets/AIModels/keyboard_area.onnx`

Configuring the model in the Gameplay scene
------------------------------------------
- Open the Gameplay scene (named `Gameplay` in the project). Locate the GameObject named `Ar Piano Game` (the object that manages AR runtime logic and inference).
- Select the `Ar Piano Game` object in the Hierarchy and find the `Onnx Model` component (this is the runtime script/component that consumes the ONNX model).
- Configure the `Onnx Model` component fields:
  - `Model`: assign the ONNX asset located at `Assets/AIModels/keyboard_area.onnx`.
  - `Input Name` / `Output Name`: ensure names match the exported ONNX model (inspect the export log or use an ONNX viewer if needed).
  - `Preprocessing` / `Image size`: set to the same image size used in training (for example, 640x640) and match normalization used during training.

Notes about runtime & platforms
-------------------------------
- The project is designed primarily for Android (AR on mobile) but can be built for Windows. When targeting Android:
  - Include any required ONNX runtime/native plugins for Android (ARMv7/ARM64) if your project uses a native runtime.
  - Configure Player Settings for Android (Graphics APIs, IL2CPP if required, Internet/Camera permissions).
- When targeting Windows, copy the same ONNX model into `Assets/AIModels/` and configure the `Onnx Model` component similarly.

Tips & troubleshooting
----------------------
- If detections are poor after export, verify that input preprocessing (resize, normalization, channel order) in Unity matches the training pipeline.
- If the `Onnx Model` component fails to load the model at runtime, check the Unity console for import errors and ensure the ONNX file is compatible with the runtime plugin used.
- For best mobile performance, consider using a quantized or optimized ONNX export (e.g., float16 or int8) and test on target hardware.

Where to look in this repo
-------------------------
- Training config: `tools/piano_vision_training/configs/train_config_unity_focus_nms.yaml`
- Training tools & scripts: `tools/piano_vision_training/`
- Runtime model folder (where to place exported ONNX): `Assets/AIModels/`
- Scene and runtime component to configure: open the Gameplay scene and inspect `Ar Piano Game` → `Onnx Model` component.


