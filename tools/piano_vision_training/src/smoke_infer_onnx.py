import argparse
from pathlib import Path
import os
import site
import cv2
import numpy as np


def configure_windows_cuda_dll_paths() -> None:
    if os.name != "nt" or not hasattr(os, "add_dll_directory"):
        return

    candidates = [
        Path(r"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.6\bin"),
        Path(r"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.8\bin"),
        Path(r"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.9\bin"),
    ]

    for site_path in site.getusersitepackages(), *site.getsitepackages():
        site_dir = Path(site_path)
        candidates.append(site_dir / "nvidia" / "cublas" / "bin")
        candidates.append(site_dir / "nvidia" / "cudnn" / "bin")
        candidates.append(site_dir / "nvidia" / "cuda_nvrtc" / "bin")

    for path in candidates:
        if path.exists():
            try:
                os.add_dll_directory(str(path))
            except OSError:
                pass


configure_windows_cuda_dll_paths()
import onnxruntime as ort


def preprocess(image_path: Path, input_size: int):
    image_bgr = cv2.imread(str(image_path))
    if image_bgr is None:
        raise RuntimeError(f"Failed to load image: {image_path}")

    image_rgb = cv2.cvtColor(image_bgr, cv2.COLOR_BGR2RGB)
    resized = cv2.resize(image_rgb, (input_size, input_size), interpolation=cv2.INTER_LINEAR)
    tensor = resized.astype(np.float32) / 255.0
    tensor = np.transpose(tensor, (2, 0, 1))
    tensor = np.expand_dims(tensor, axis=0)
    return tensor


def main():
    parser = argparse.ArgumentParser(description="Run ONNX Runtime smoke inference")
    parser.add_argument("--model", required=True, help="Path to ONNX model")
    parser.add_argument("--image", required=True, help="Path to test image")
    parser.add_argument("--size", type=int, default=640, help="Model input size")
    args = parser.parse_args()

    model_path = Path(args.model)
    image_path = Path(args.image)

    if not model_path.exists():
        raise FileNotFoundError(f"Model not found: {model_path}")
    if not image_path.exists():
        raise FileNotFoundError(f"Image not found: {image_path}")

    session = ort.InferenceSession(str(model_path), providers=["CPUExecutionProvider"])
    input_name = session.get_inputs()[0].name
    output_name = session.get_outputs()[0].name

    input_tensor = preprocess(image_path, args.size)
    outputs = session.run([output_name], {input_name: input_tensor})

    output = outputs[0]
    print(f"Input name: {input_name}")
    print(f"Output name: {output_name}")
    print(f"Output shape: {list(output.shape)}")
    print(f"Output dtype: {output.dtype}")
    print(f"Output stats: min={float(output.min()):.6f} max={float(output.max()):.6f}")


if __name__ == "__main__":
    main()
