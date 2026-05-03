import argparse
from pathlib import Path
import json
import yaml
from ultralytics import YOLO


def load_yaml(path: Path):
    with path.open("r", encoding="utf-8") as f:
        return yaml.safe_load(f)


def metric_block(metric_obj):
    if metric_obj is None:
        return None
    return {
        "map50_95": float(metric_obj.map),
        "map50": float(metric_obj.map50),
        "map75": float(metric_obj.map75),
        "per_class_map50_95": [float(v) for v in metric_obj.maps],
    }


def main():
    parser = argparse.ArgumentParser(description="Evaluate trained checkpoint")
    parser.add_argument("--weights", required=True, help="Path to best.pt")
    parser.add_argument("--dataset", required=True, help="Path to dataset yaml")
    parser.add_argument("--config", default="configs/train_config.yaml", help="Path to train config yaml")
    parser.add_argument("--out", default="runs/eval_metrics.json", help="Output JSON path")
    args = parser.parse_args()

    weights = Path(args.weights)
    dataset = Path(args.dataset)
    config = Path(args.config)

    if not weights.exists():
        raise FileNotFoundError(f"Weights not found: {weights}")
    if not dataset.exists():
        raise FileNotFoundError(f"Dataset yaml not found: {dataset}")
    if not config.exists():
        raise FileNotFoundError(f"Config not found: {config}")

    cfg = load_yaml(config)
    model = YOLO(str(weights))

    val_cfg = cfg.get("val", {})
    metrics = model.val(
        data=str(dataset),
        imgsz=int(cfg["imgsz"]),
        conf=float(val_cfg.get("conf", 0.25)),
        iou=float(val_cfg.get("iou", 0.7)),
        max_det=int(val_cfg.get("max_det", 300)),
    )

    metrics_dict = {"box": metric_block(getattr(metrics, "box", None))}

    seg_block = metric_block(getattr(metrics, "seg", None))
    if seg_block is not None:
        metrics_dict["seg"] = seg_block

    out_path = Path(args.out)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    with out_path.open("w", encoding="utf-8") as f:
        json.dump(metrics_dict, f, indent=2)

    print("Evaluation complete:")
    print(json.dumps(metrics_dict, indent=2))


if __name__ == "__main__":
    main()
