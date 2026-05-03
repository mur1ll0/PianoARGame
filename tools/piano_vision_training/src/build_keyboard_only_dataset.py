import argparse
import shutil
from pathlib import Path
import yaml


SCRIPT_DIR = Path(__file__).resolve().parent
PROJECT_ROOT = SCRIPT_DIR.parent
DEFAULT_OUT = PROJECT_ROOT / "datasets" / "piano_dataset"
DEFAULT_INPUTS = [
    PROJECT_ROOT / "datasets" / "baixados" / "My First Project.v1i.yolov8",
    PROJECT_ROOT / "datasets" / "baixados" / "piano3.v1i.yolov8",
    PROJECT_ROOT / "datasets" / "baixados" / "pianokeyboard.v7i.yolov8",
]


def load_data_yaml(dataset_root: Path):
    candidates = [dataset_root / "data.yaml", dataset_root / "dataset.yaml"]
    for c in candidates:
        if c.exists():
            with c.open("r", encoding="utf-8") as f:
                return yaml.safe_load(f), c
    raise FileNotFoundError(f"No data.yaml found in {dataset_root}")


def normalize_names(names):
    if isinstance(names, dict):
        return {int(k): str(v) for k, v in names.items()}
    if isinstance(names, list):
        return {i: str(v) for i, v in enumerate(names)}
    raise ValueError("Unsupported names format in data.yaml")


def find_split_dir(root: Path, key: str):
    # Roboflow exports usually use train/valid/test.
    aliases = {
        "train": ["train"],
        "val": ["val", "valid", "validation"],
        "test": ["test"],
    }
    for name in aliases[key]:
        candidate = root / name
        if candidate.exists():
            return candidate
    return None


def list_images(path: Path):
    exts = {".jpg", ".jpeg", ".png", ".bmp", ".webp"}
    if not path.exists():
        return []
    return sorted([p for p in path.rglob("*") if p.is_file() and p.suffix.lower() in exts])


def parse_label_line(line: str):
    parts = line.strip().split()
    if len(parts) < 5:
        return None
    try:
        cls = int(parts[0])
        vals = [float(v) for v in parts[1:]]
    except ValueError:
        return None

    # Detection label: class xc yc w h
    if len(vals) == 4:
        return cls, {"type": "bbox", "values": vals}

    # Segmentation/polygon-like label: class x1 y1 x2 y2 ...
    if len(vals) >= 6 and len(vals) % 2 == 0:
        points = [(vals[i], vals[i + 1]) for i in range(0, len(vals), 2)]
        if len(points) >= 2 and points[0] == points[-1]:
            points = points[:-1]
        if len(points) >= 3:
            return cls, {"type": "polygon", "points": points}

    return None


def sanitize_yolo_box(vals):
    """Clamp/repair normalized YOLO box and return a valid box or None."""
    xc, yc, w, h = [float(v) for v in vals]

    # Reject clearly invalid boxes.
    if w <= 0.0 or h <= 0.0:
        return None

    # Compute corners and clip to image bounds in normalized space.
    x1 = xc - w / 2.0
    y1 = yc - h / 2.0
    x2 = xc + w / 2.0
    y2 = yc + h / 2.0

    x1 = max(0.0, min(1.0, x1))
    y1 = max(0.0, min(1.0, y1))
    x2 = max(0.0, min(1.0, x2))
    y2 = max(0.0, min(1.0, y2))

    # Discard degenerate boxes after clipping.
    if x2 <= x1 or y2 <= y1:
        return None

    new_w = x2 - x1
    new_h = y2 - y1
    new_xc = (x1 + x2) / 2.0
    new_yc = (y1 + y2) / 2.0

    # Format with fixed precision for stable files/diffs.
    return [
        f"{new_xc:.6f}",
        f"{new_yc:.6f}",
        f"{new_w:.6f}",
        f"{new_h:.6f}",
    ]


def sanitize_polygon_points(points):
    clean = []
    for x, y in points:
        sx = max(0.0, min(1.0, float(x)))
        sy = max(0.0, min(1.0, float(y)))
        clean.append((sx, sy))

    if len(clean) >= 2 and clean[0] == clean[-1]:
        clean = clean[:-1]

    if len(clean) < 3:
        return None

    return clean


def bbox_xywh_to_polygon(vals):
    clean_box = sanitize_yolo_box([str(v) for v in vals])
    if clean_box is None:
        return None

    xc, yc, w, h = [float(v) for v in clean_box]
    x1 = max(0.0, min(1.0, xc - w / 2.0))
    y1 = max(0.0, min(1.0, yc - h / 2.0))
    x2 = max(0.0, min(1.0, xc + w / 2.0))
    y2 = max(0.0, min(1.0, yc + h / 2.0))

    points = [(x1, y1), (x2, y1), (x2, y2), (x1, y2)]
    return sanitize_polygon_points(points)


def polygon_to_yolo_seg_line(cls: int, points):
    flat = []
    for x, y in points:
        flat.append(f"{x:.6f}")
        flat.append(f"{y:.6f}")
    return f"{cls} " + " ".join(flat)


def is_keyboard_class(name: str):
    n = name.strip().lower()
    positive = {
        "keyboard",
        "keyboard_area",
        "musical keyboard",
        "piano keyboard",
        "piano_key_area",
        "piano",
        "teclado",
        "area_de_teclas",
    }
    negative = {
        "head",
        "cabeca",
    }
    if n in negative:
        return False
    return n in positive


def ensure_output_dirs(out_root: Path):
    for split in ("train", "val", "test"):
        (out_root / "images" / split).mkdir(parents=True, exist_ok=True)
        (out_root / "labels" / split).mkdir(parents=True, exist_ok=True)


def merge_one_dataset(dataset_root: Path, out_root: Path, keep_negatives: bool):
    data, _ = load_data_yaml(dataset_root)
    names = normalize_names(data["names"])

    keyboard_ids = {cid for cid, cname in names.items() if is_keyboard_class(cname)}

    if not keyboard_ids and len(names) == 1:
        # Fallback: if dataset has a single class, assume it is keyboard area.
        keyboard_ids = {next(iter(names.keys()))}

    if not keyboard_ids:
        print(f"[WARN] No keyboard-like class found in {dataset_root}. Names={names}")

    dataset_tag = dataset_root.name.replace(" ", "_")

    stats = {
        "images": 0,
        "labeled": 0,
        "negative": 0,
        "seg_from_poly": 0,
        "seg_from_bbox": 0,
    }

    for split in ("train", "val", "test"):
        split_root = find_split_dir(dataset_root, split)
        if split_root is None:
            continue

        img_dir = split_root / "images"
        lbl_dir = split_root / "labels"
        images = list_images(img_dir)

        for img in images:
            rel = img.relative_to(img_dir)
            src_label = (lbl_dir / rel).with_suffix(".txt")

            # Flatten name with dataset prefix to avoid collisions across datasets.
            stem = rel.stem
            dst_name = f"{dataset_tag}__{stem}{img.suffix.lower()}"
            dst_img = out_root / "images" / split / dst_name
            dst_lbl = out_root / "labels" / split / f"{dataset_tag}__{stem}.txt"

            lines_out = []
            if src_label.exists():
                for raw in src_label.read_text(encoding="utf-8").splitlines():
                    parsed = parse_label_line(raw)
                    if parsed is None:
                        continue
                    cls, payload = parsed
                    if cls in keyboard_ids:
                        if payload["type"] == "polygon":
                            clean_points = sanitize_polygon_points(payload["points"])
                            if clean_points is not None:
                                lines_out.append(polygon_to_yolo_seg_line(0, clean_points))
                                stats["seg_from_poly"] += 1
                        else:
                            poly = bbox_xywh_to_polygon(payload["values"])
                            if poly is not None:
                                lines_out.append(polygon_to_yolo_seg_line(0, poly))
                                stats["seg_from_bbox"] += 1

            if lines_out:
                shutil.copy2(img, dst_img)
                dst_lbl.write_text("\n".join(lines_out) + "\n", encoding="utf-8")
                stats["images"] += 1
                stats["labeled"] += 1
            elif keep_negatives:
                shutil.copy2(img, dst_img)
                dst_lbl.write_text("", encoding="utf-8")
                stats["images"] += 1
                stats["negative"] += 1

    return stats


def write_output_yaml(out_root: Path):
    data_yaml = {
        "path": str(out_root).replace("\\", "/"),
        "train": "images/train",
        "val": "images/val",
        "test": "images/test",
        "names": {0: "keyboard_area"},
    }
    out_file = out_root.parent / f"{out_root.name}.yaml"
    with out_file.open("w", encoding="utf-8") as f:
        yaml.safe_dump(data_yaml, f, sort_keys=False, allow_unicode=False)
    print(f"Wrote dataset yaml: {out_file}")


def main():
    parser = argparse.ArgumentParser(
        description="Merge Roboflow datasets into keyboard_area-only YOLO segmentation dataset"
    )
    parser.add_argument(
        "--out",
        default=str(DEFAULT_OUT),
        help="Output dataset root (default: tools/piano_vision_training/datasets/piano_dataset)",
    )
    parser.add_argument(
        "--inputs",
        nargs="+",
        default=[str(p) for p in DEFAULT_INPUTS],
        help="Input dataset roots exported by Roboflow",
    )
    parser.add_argument("--keep-negatives", action="store_true", help="Keep images without keyboard boxes")
    args = parser.parse_args()

    out_root = Path(args.out)
    input_roots = [Path(p) for p in args.inputs]

    ensure_output_dirs(out_root)

    total = {"images": 0, "labeled": 0, "negative": 0}
    for root in input_roots:
        if not root.exists():
            raise FileNotFoundError(f"Input dataset not found: {root}")
        stats = merge_one_dataset(root, out_root, args.keep_negatives)
        print(f"Merged {root.name}: {stats}")
        for k in total:
            total[k] += stats[k]

    write_output_yaml(out_root)
    print(f"Done. Total: {total}")


if __name__ == "__main__":
    main()
