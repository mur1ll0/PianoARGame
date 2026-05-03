import argparse
from pathlib import Path
import yaml
from ultralytics import YOLO


def load_yaml(path: Path):
    with path.open("r", encoding="utf-8") as f:
        return yaml.safe_load(f)


def resolve_device(value: str):
    if value == "auto":
        return None
    return value


def main():
    parser = argparse.ArgumentParser(description="Train piano detector with Ultralytics YOLO")
    parser.add_argument("--dataset", required=True, help="Path to dataset yaml")
    parser.add_argument("--config", default="configs/train_config.yaml", help="Path to training config yaml")
    parser.add_argument("--project", default="runs", help="Output project folder")
    args = parser.parse_args()

    config_path = Path(args.config)
    dataset_path = Path(args.dataset)

    if not config_path.exists():
        raise FileNotFoundError(f"Config not found: {config_path}")
    if not dataset_path.exists():
        raise FileNotFoundError(f"Dataset yaml not found: {dataset_path}")

    cfg = load_yaml(config_path)
    model = YOLO(cfg["model"])

    train_kwargs = {
        "data": str(dataset_path),
        "epochs": int(cfg["epochs"]),
        "imgsz": int(cfg["imgsz"]),
        "batch": cfg["batch"],
        "workers": int(cfg["workers"]),
        "patience": int(cfg["patience"]),
        "seed": int(cfg["seed"]),
        "project": args.project,
        "name": cfg["project_name"],
        "exist_ok": True,
        "device": resolve_device(str(cfg["device"])),
        **cfg.get("augment", {}),
    }

    results = model.train(**train_kwargs)
    print("Training finished.")
    print(results)


if __name__ == "__main__":
    main()
