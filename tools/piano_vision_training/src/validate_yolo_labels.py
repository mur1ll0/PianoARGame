import argparse
from pathlib import Path


def parse_line(line: str):
    parts = line.strip().split()
    if len(parts) < 5:
        raise ValueError(f"Expected at least 5 values, got {len(parts)}")

    cls = int(parts[0])
    vals = [float(v) for v in parts[1:]]

    if len(vals) == 4:
        return cls, {"type": "bbox", "vals": vals}

    if len(vals) >= 6 and len(vals) % 2 == 0:
        points = [(vals[i], vals[i + 1]) for i in range(0, len(vals), 2)]
        if len(points) >= 2 and points[0] == points[-1]:
            points = points[:-1]
        if len(points) >= 3:
            return cls, {"type": "polygon", "points": points}

    raise ValueError(
        "Expected detection line (cls xc yc w h) or polygon line "
        "(cls x1 y1 x2 y2 ... with at least 3 points)"
    )


def check_file(label_file: Path, num_classes: int):
    eps = 1e-6
    errors = []
    if not label_file.exists():
        return errors

    lines = label_file.read_text(encoding="utf-8").splitlines()
    for i, raw in enumerate(lines, start=1):
        if not raw.strip():
            continue
        try:
            cls, payload = parse_line(raw)
        except Exception as ex:
            errors.append(f"{label_file}: line {i} invalid format ({ex})")
            continue

        if cls < 0 or cls >= num_classes:
            errors.append(f"{label_file}: line {i} class {cls} outside [0,{num_classes - 1}]")

        if payload["type"] == "bbox":
            xc, yc, w, h = payload["vals"]
            for name, val in (("x_center", xc), ("y_center", yc), ("width", w), ("height", h)):
                if val < -eps or val > 1.0 + eps:
                    errors.append(f"{label_file}: line {i} {name}={val:.6f} outside [0,1]")

            if w <= eps or h <= eps:
                errors.append(f"{label_file}: line {i} width/height must be > 0")

            x_min = xc - w / 2.0
            x_max = xc + w / 2.0
            y_min = yc - h / 2.0
            y_max = yc + h / 2.0
            if x_min < -eps or y_min < -eps or x_max > 1.0 + eps or y_max > 1.0 + eps:
                errors.append(f"{label_file}: line {i} box exceeds image boundaries in normalized space")
        else:
            points = payload["points"]
            for p_idx, (x, y) in enumerate(points, start=1):
                if x < -eps or x > 1.0 + eps:
                    errors.append(f"{label_file}: line {i} point {p_idx} x={x:.6f} outside [0,1]")
                if y < -eps or y > 1.0 + eps:
                    errors.append(f"{label_file}: line {i} point {p_idx} y={y:.6f} outside [0,1]")

    return errors


def list_images(images_dir: Path):
    exts = {".jpg", ".jpeg", ".png", ".bmp", ".webp"}
    return [p for p in images_dir.rglob("*") if p.is_file() and p.suffix.lower() in exts]


def main():
    parser = argparse.ArgumentParser(description="Validate YOLO labels for a dataset root")
    parser.add_argument("--dataset", required=True, help="Path to dataset root (contains images/ and labels/)")
    parser.add_argument("--num-classes", type=int, default=1, help="Number of classes")
    args = parser.parse_args()

    root = Path(args.dataset)
    if not root.exists():
        raise FileNotFoundError(f"Dataset not found: {root}")

    total_images = 0
    total_missing_labels = 0
    all_errors = []

    for split in ("train", "val", "test"):
        images_dir = root / "images" / split
        labels_dir = root / "labels" / split

        if not images_dir.exists():
            continue

        images = list_images(images_dir)
        total_images += len(images)

        for img in images:
            rel = img.relative_to(images_dir)
            label = labels_dir / rel.with_suffix(".txt")

            if not label.exists():
                total_missing_labels += 1
                all_errors.append(f"Missing label: {label}")
                continue

            all_errors.extend(check_file(label, args.num_classes))

    print(f"Images checked: {total_images}")
    print(f"Missing labels: {total_missing_labels}")
    print(f"Total issues: {len(all_errors)}")

    for err in all_errors[:200]:
        print(err)

    if all_errors:
        raise SystemExit(1)

    print("Validation passed.")


if __name__ == "__main__":
    main()
