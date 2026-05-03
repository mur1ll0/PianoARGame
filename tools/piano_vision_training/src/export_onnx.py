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
    output = model.export(
        format=export_cfg.get("format", "onnx"),
        imgsz=int(export_cfg.get("imgsz", 640)),
        opset=int(export_cfg.get("opset", 12)),
        simplify=bool(export_cfg.get("simplify", True)),
        dynamic=bool(export_cfg.get("dynamic", False)),
        nms=bool(export_cfg.get("nms", False)),
        half=bool(export_cfg.get("half", False)),
    )

    print("Export complete:")
    print(output)


if __name__ == "__main__":
    main()
