import argparse
from pathlib import Path
import yaml
from ultralytics import YOLO


def load_yaml(path: Path):
    with path.open("r", encoding="utf-8") as f:
        return yaml.safe_load(f)


def main():
    parser = argparse.ArgumentParser(description="Export trained YOLO weights to ONNX")
    parser.add_argument("--weights", required=True, help="Path to best.pt")
    parser.add_argument("--config", default="configs/train_config.yaml", help="Path to config yaml")
    args = parser.parse_args()

    weights = Path(args.weights)
    config = Path(args.config)

    if not weights.exists():
        raise FileNotFoundError(f"Weights not found: {weights}")
    if not config.exists():
        raise FileNotFoundError(f"Config not found: {config}")

    cfg = load_yaml(config)
    export_cfg = cfg.get("export", {})

    model = YOLO(str(weights))

    export_kwargs = {
        "format": export_cfg.get("format", "onnx"),
        "imgsz": int(export_cfg.get("imgsz", 640)),
        "opset": int(export_cfg.get("opset", 12)),
        "simplify": bool(export_cfg.get("simplify", True)),
        "dynamic": bool(export_cfg.get("dynamic", False)),
        "nms": bool(export_cfg.get("nms", False)),
        "half": bool(export_cfg.get("half", False)),
    }

    # Optional NMS/export knobs used by newer ultralytics versions.
    # Keep them conditional to remain compatible with older versions.
    optional_keys = ("conf", "iou", "max_det", "agnostic_nms")
    for key in optional_keys:
        if key in export_cfg:
            export_kwargs[key] = export_cfg[key]

    try:
        output = model.export(**export_kwargs)
    except TypeError as ex:
        # Fallback path for ultralytics versions that don't accept some optional args.
        for key in optional_keys:
            export_kwargs.pop(key, None)
        print(f"[WARN] Optional export args not supported by this ultralytics version: {ex}")
        output = model.export(**export_kwargs)

    print("Export complete:")
    print(output)


if __name__ == "__main__":
    main()
