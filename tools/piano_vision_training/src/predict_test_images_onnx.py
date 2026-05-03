import argparse
from pathlib import Path
from typing import List, Tuple
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


Detection = Tuple[float, float, float, float, float]  # x1, y1, x2, y2, score


def list_images(path: Path) -> List[Path]:
    exts = {".jpg", ".jpeg", ".png", ".bmp", ".webp"}
    return sorted([p for p in path.rglob("*") if p.is_file() and p.suffix.lower() in exts])


def preprocess_image(image_bgr: np.ndarray, input_size: int) -> np.ndarray:
    image_rgb = cv2.cvtColor(image_bgr, cv2.COLOR_BGR2RGB)
    resized = cv2.resize(image_rgb, (input_size, input_size), interpolation=cv2.INTER_LINEAR)
    tensor = resized.astype(np.float32) / 255.0
    tensor = np.transpose(tensor, (2, 0, 1))
    return np.expand_dims(tensor, axis=0)


def compute_iou(a: Detection, b: Detection) -> float:
    x1 = max(a[0], b[0])
    y1 = max(a[1], b[1])
    x2 = min(a[2], b[2])
    y2 = min(a[3], b[3])
    inter_w = max(0.0, x2 - x1)
    inter_h = max(0.0, y2 - y1)
    inter = inter_w * inter_h
    if inter <= 0.0:
        return 0.0

    area_a = max(0.0, a[2] - a[0]) * max(0.0, a[3] - a[1])
    area_b = max(0.0, b[2] - b[0]) * max(0.0, b[3] - b[1])
    union = area_a + area_b - inter
    return 0.0 if union <= 0.0 else inter / union


def nms(detections: List[Detection], iou_threshold: float) -> List[Detection]:
    if not detections:
        return []

    ordered = sorted(detections, key=lambda d: d[4], reverse=True)
    kept: List[Detection] = []

    for det in ordered:
        overlap = any(compute_iou(det, k) > iou_threshold for k in kept)
        if not overlap:
            kept.append(det)
    return kept


def convert_box_to_xyxy(
    cx: float,
    cy: float,
    w: float,
    h: float,
    image_w: int,
    image_h: int,
    input_size: int,
) -> Tuple[float, float, float, float]:
    normalized = all(abs(v) <= 2.0 for v in (cx, cy, w, h))
    scale_x = image_w if normalized else image_w / float(max(1, input_size))
    scale_y = image_h if normalized else image_h / float(max(1, input_size))

    center_x = cx * scale_x
    center_y = cy * scale_y
    box_w = abs(w * scale_x)
    box_h = abs(h * scale_y)

    x1 = max(0.0, min(image_w - 1.0, center_x - box_w * 0.5))
    y1 = max(0.0, min(image_h - 1.0, center_y - box_h * 0.5))
    x2 = max(x1 + 1.0, min(float(image_w), center_x + box_w * 0.5))
    y2 = max(y1 + 1.0, min(float(image_h), center_y + box_h * 0.5))
    return x1, y1, x2, y2


def decode_output(
    output: np.ndarray,
    image_w: int,
    image_h: int,
    input_size: int,
    num_classes: int,
    conf_threshold: float,
    iou_threshold: float,
) -> List[Detection]:
    if output.ndim != 3 or output.shape[0] != 1:
        raise ValueError(f"Unexpected output shape: {list(output.shape)}")

    dim1 = output.shape[1]
    dim2 = output.shape[2]

    features_first = dim1 <= 256 and dim2 > dim1
    candidate_count = dim2 if features_first else dim1
    feature_count = dim1 if features_first else dim2

    if feature_count < 5:
        raise ValueError(f"Unexpected feature count: {feature_count}")

    data = output.reshape(-1)

    def get_value(candidate: int, feature: int) -> float:
        if features_first:
            return float(data[feature * candidate_count + candidate])
        return float(data[candidate * feature_count + feature])

    detections: List[Detection] = []
    class_count = int(num_classes)
    if feature_count < 4 + class_count:
        raise ValueError(
            f"Unexpected feature count {feature_count} for num_classes={class_count}. "
            "Expected at least 4 + num_classes features."
        )

    for c in range(candidate_count):
        cx = get_value(c, 0)
        cy = get_value(c, 1)
        bw = get_value(c, 2)
        bh = get_value(c, 3)

        best_score = 0.0
        for cls_idx in range(class_count):
            score = get_value(c, 4 + cls_idx)
            if score > best_score:
                best_score = score

        if best_score < conf_threshold:
            continue

        x1, y1, x2, y2 = convert_box_to_xyxy(cx, cy, bw, bh, image_w, image_h, input_size)
        if (x2 - x1) <= 1.0 or (y2 - y1) <= 1.0:
            continue

        detections.append((x1, y1, x2, y2, best_score))

    return nms(detections, iou_threshold)


def draw_detections(image_bgr: np.ndarray, detections: List[Detection]) -> np.ndarray:
    out = image_bgr.copy()
    for x1, y1, x2, y2, score in detections:
        p1 = (int(round(x1)), int(round(y1)))
        p2 = (int(round(x2)), int(round(y2)))
        cv2.rectangle(out, p1, p2, (0, 255, 255), 2)
        label = f"keyboard_area {score:.2f}"
        cv2.putText(out, label, (p1[0], max(20, p1[1] - 8)), cv2.FONT_HERSHEY_SIMPLEX, 0.6, (0, 255, 255), 2)
    return out


def main():
    parser = argparse.ArgumentParser(description="Run ONNX inference over test_images and save rendered detections")
    parser.add_argument(
        "--model",
        default="runs/detect/runs/piano_detector_candidateB/weights/best.onnx",
        help="Path to ONNX model",
    )
    parser.add_argument("--images-dir", default="test_images", help="Folder with real test images")
    parser.add_argument("--out-dir", default="runs/test_predictions", help="Output folder for rendered images")
    parser.add_argument("--size", type=int, default=640, help="Model input size")
    parser.add_argument("--num-classes", type=int, default=1, help="Number of model classes")
    parser.add_argument("--conf", type=float, default=0.25, help="Confidence threshold")
    parser.add_argument("--iou", type=float, default=0.45, help="NMS IoU threshold")
    args = parser.parse_args()

    model_path = Path(args.model)
    images_dir = Path(args.images_dir)
    out_dir = Path(args.out_dir)

    if not model_path.exists():
        raise FileNotFoundError(f"Model not found: {model_path}")
    if not images_dir.exists():
        raise FileNotFoundError(f"Images dir not found: {images_dir}")

    images = list_images(images_dir)
    if not images:
        raise RuntimeError(f"No images found in {images_dir}")

    out_dir.mkdir(parents=True, exist_ok=True)

    providers = ["CUDAExecutionProvider", "CPUExecutionProvider"]
    session = ort.InferenceSession(str(model_path), providers=providers)
    input_name = session.get_inputs()[0].name
    output_infos = session.get_outputs()
    output_names = [o.name for o in output_infos]

    print(f"Using providers: {session.get_providers()}")
    print(f"Model input: {input_name} | outputs: {output_names}")

    total = 0
    with_detection = 0

    for img_path in images:
        image_bgr = cv2.imread(str(img_path))
        if image_bgr is None:
            print(f"[WARN] Failed to read image: {img_path}")
            continue

        h, w = image_bgr.shape[:2]
        tensor = preprocess_image(image_bgr, args.size)
        raw_outputs = session.run(output_names, {input_name: tensor})
        output = None
        for out in raw_outputs:
            if isinstance(out, np.ndarray) and out.ndim == 3:
                output = out
                break

        if output is None:
            raise RuntimeError(
                "No 3D output tensor found for detection decode. "
                "This script expects at least one output shaped like [1, C, N] or [1, N, C]."
            )

        detections = decode_output(
            output=output,
            image_w=w,
            image_h=h,
            input_size=args.size,
            num_classes=args.num_classes,
            conf_threshold=args.conf,
            iou_threshold=args.iou,
        )

        if detections:
            with_detection += 1

        rendered = draw_detections(image_bgr, detections)
        out_file = out_dir / f"pred_{img_path.name}"
        cv2.imwrite(str(out_file), rendered)
        total += 1

    print(f"Processed images: {total}")
    print(f"Images with detections: {with_detection}")
    print(f"Saved rendered outputs to: {out_dir}")


if __name__ == "__main__":
    main()
