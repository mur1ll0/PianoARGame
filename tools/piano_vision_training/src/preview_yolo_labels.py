import argparse
from pathlib import Path
import cv2
import numpy as np


COLORS = {
    0: (0, 180, 255),  # keyboard_area
}

NAMES = {
    0: "keyboard_area",
}


def yolo_to_xyxy(xc, yc, w, h, img_w, img_h):
    x1 = int(round((xc - w / 2.0) * img_w))
    y1 = int(round((yc - h / 2.0) * img_h))
    x2 = int(round((xc + w / 2.0) * img_w))
    y2 = int(round((yc + h / 2.0) * img_h))
    x1 = max(0, min(img_w - 1, x1))
    y1 = max(0, min(img_h - 1, y1))
    x2 = max(0, min(img_w - 1, x2))
    y2 = max(0, min(img_h - 1, y2))
    return x1, y1, x2, y2


def parse_yolo_label_line(raw: str):
    parts = raw.strip().split()
    if len(parts) < 5:
        return None

    try:
        cls = int(parts[0])
        vals = [float(v) for v in parts[1:]]
    except ValueError:
        return None

    # YOLO detection format: class xc yc w h
    if len(vals) == 4:
        xc, yc, bw, bh = vals
        return {
            "type": "bbox",
            "cls": cls,
            "xc": xc,
            "yc": yc,
            "bw": bw,
            "bh": bh,
        }

    # YOLO segmentation/polygon-like format: class x1 y1 x2 y2 ...
    # Also covers many OBB exports that are represented by points.
    if len(vals) >= 6 and len(vals) % 2 == 0:
        points = [(vals[i], vals[i + 1]) for i in range(0, len(vals), 2)]
        if len(points) >= 2 and points[0] == points[-1]:
            points = points[:-1]

        if len(points) >= 3:
            return {
                "type": "polygon",
                "cls": cls,
                "points": points,
            }

    return None


def polygon_to_xy(points, img_w, img_h):
    out = []
    for x, y in points:
        px = int(round(x * img_w))
        py = int(round(y * img_h))
        px = max(0, min(img_w - 1, px))
        py = max(0, min(img_h - 1, py))
        out.append((px, py))
    return out


def draw_labels(image, label_path: Path, allowed_classes: set[int] | None = None):
    h, w = image.shape[:2]
    stats = {
        "drawn": 0,
        "invalid_lines": 0,
        "polygon_lines": 0,
        "bbox_lines": 0,
        "outside_range_lines": 0,
    }

    if not label_path.exists():
        return image, stats

    lines = label_path.read_text(encoding="utf-8").splitlines()
    for raw in lines:
        if not raw.strip():
            continue

        parts = raw.strip().split()
        parsed = parse_yolo_label_line(raw)
        if parsed is None:
            stats["invalid_lines"] += 1
            continue

        cls = parsed["cls"]
        if allowed_classes is not None and cls not in allowed_classes:
            continue

        color = COLORS.get(cls, (255, 255, 255))
        name = NAMES.get(cls, str(cls))

        if parsed["type"] == "bbox":
            stats["bbox_lines"] += 1
            xc = parsed["xc"]
            yc = parsed["yc"]
            bw = parsed["bw"]
            bh = parsed["bh"]
            if min(xc, yc, bw, bh) < 0.0 or max(xc, yc, bw, bh) > 1.0:
                stats["outside_range_lines"] += 1

            x1, y1, x2, y2 = yolo_to_xyxy(xc, yc, bw, bh, w, h)
            cv2.rectangle(image, (x1, y1), (x2, y2), color, 2)
            cv2.putText(
                image,
                f"{name} ({cls}) [bbox]",
                (x1, max(20, y1 - 6)),
                cv2.FONT_HERSHEY_SIMPLEX,
                0.6,
                color,
                2,
                cv2.LINE_AA,
            )
        else:
            stats["polygon_lines"] += 1
            points = parsed["points"]
            flat_vals = [v for p in points for v in p]
            if min(flat_vals) < 0.0 or max(flat_vals) > 1.0:
                stats["outside_range_lines"] += 1

            poly = np.array(polygon_to_xy(points, w, h), dtype=np.int32)
            if len(poly) >= 3:
                # Filled overlay helps validate the annotated area, not only edges.
                overlay = image.copy()
                cv2.fillPoly(overlay, [poly], color)
                image = cv2.addWeighted(overlay, 0.2, image, 0.8, 0.0)
                cv2.polylines(image, [poly], True, color, 2)

                tx, ty = int(poly[0][0]), int(poly[0][1])
                cv2.putText(
                    image,
                    f"{name} ({cls}) [poly]",
                    (tx, max(20, ty - 6)),
                    cv2.FONT_HERSHEY_SIMPLEX,
                    0.6,
                    color,
                    2,
                    cv2.LINE_AA,
                )
        stats["drawn"] += 1

    return image, stats


def list_images(path: Path):
    exts = {".jpg", ".jpeg", ".png", ".bmp", ".webp"}
    return sorted([p for p in path.rglob("*") if p.is_file() and p.suffix.lower() in exts])


def main():
    parser = argparse.ArgumentParser(description="Generate preview images with YOLO labels (bbox and polygons)")
    parser.add_argument("--images", required=True, help="Images folder")
    parser.add_argument("--labels", required=True, help="Labels folder")
    parser.add_argument("--out", required=True, help="Output folder")
    parser.add_argument("--limit", type=int, default=200, help="Max images to render")
    parser.add_argument(
        "--only-class",
        type=int,
        default=None,
        help="Draw only one class id (e.g. 0 for keyboard_area)",
    )
    args = parser.parse_args()

    images_dir = Path(args.images)
    labels_dir = Path(args.labels)
    out_dir = Path(args.out)

    if not images_dir.exists():
        raise FileNotFoundError(f"Images folder not found: {images_dir}")
    if not labels_dir.exists():
        raise FileNotFoundError(f"Labels folder not found: {labels_dir}")

    out_dir.mkdir(parents=True, exist_ok=True)

    images = list_images(images_dir)
    count = 0
    total_boxes = 0
    invalid_lines = 0
    polygon_lines = 0
    bbox_lines = 0
    outside_range_lines = 0

    allowed_classes = None
    if args.only_class is not None:
        allowed_classes = {args.only_class}

    for img_path in images:
        if count >= args.limit:
            break

        rel = img_path.relative_to(images_dir)
        label_path = (labels_dir / rel).with_suffix(".txt")

        image = cv2.imread(str(img_path))
        if image is None:
            continue

        image, stats = draw_labels(image, label_path, allowed_classes=allowed_classes)
        total_boxes += stats["drawn"]
        invalid_lines += stats["invalid_lines"]
        polygon_lines += stats["polygon_lines"]
        bbox_lines += stats["bbox_lines"]
        outside_range_lines += stats["outside_range_lines"]

        out_path = out_dir / rel
        out_path.parent.mkdir(parents=True, exist_ok=True)
        cv2.imwrite(str(out_path), image)
        count += 1

    print(f"Rendered {count} preview images at: {out_dir}")
    print(f"Drawn labels: {total_boxes}")
    print(f"Detection bbox lines: {bbox_lines}")
    print(f"Polygon/point lines: {polygon_lines}")
    print(f"Invalid label lines skipped: {invalid_lines}")
    print(f"Label lines with values outside [0,1]: {outside_range_lines}")


if __name__ == "__main__":
    main()
