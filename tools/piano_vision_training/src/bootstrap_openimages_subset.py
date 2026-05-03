import argparse
from pathlib import Path


def main():
    parser = argparse.ArgumentParser(
        description="Helper notes for building an Open Images subset for piano/keyboard classes"
    )
    parser.add_argument("--out", default="datasets/README_openimages_subset.txt", help="Output instructions file")
    args = parser.parse_args()

    out_path = Path(args.out)
    out_path.parent.mkdir(parents=True, exist_ok=True)

    content = """
Open Images subset strategy (manual recipe)

1) Source classes to prioritize:
   - Piano
   - Musical keyboard

2) Optional class (careful with domain drift):
   - Computer keyboard

3) Use either:
   - FiftyOne Open Images downloader + class filtering
   - Roboflow import + class filtering + relabeling

4) After download:
   - export labels to YOLO format
   - normalize to your two target classes: piano, keyboard_area
   - split by scene (not random frame split)

5) Validate quality:
   - verify label tightness and class consistency
   - ensure val/test are from unseen scenes
""".strip()

    out_path.write_text(content, encoding="utf-8")
    print(f"Wrote instructions to {out_path}")


if __name__ == "__main__":
    main()
