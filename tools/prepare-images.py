#!/usr/bin/env python3
"""Prepare the college's demo images for the web.

Usage:
    python tools/prepare-images.py ["path/to/Re__Demo_images_.zip"]

Reads the original images straight from the college's zip and writes, to
college-content/images/ (relative to the repo root):
  - {slug}-480/-960/-1600 .jpg (JPEG quality 78, progressive) and .webp
    (quality 72) for each photo, scaled to that width (never upscaled);
  - logo-colour.png and logo-white.png, losslessly optimised.

Requires Pillow (pip install Pillow).
"""

import io
import os
import sys
import zipfile
from pathlib import Path

try:
    from PIL import Image, ImageOps
except ImportError:
    sys.exit("Pillow is not installed. Run: pip install Pillow")

PHOTOS = {
    "Carpentiry.jpg": "carpentry",  # sic: the college's file name
    "Catering.jpg": "catering",
    "Example News Story.jpg": "news-story",
    "Fashion Textiles.jpg": "fashion-textiles",
    "Group of Learners.jpg": "group-of-learners",
    "Hairdressing.jpg": "hairdressing",
    "Students Learning.jpg": "students-learning",
}

LOGOS = {
    "CC Logo - Colour MEDIUM.png": "logo-colour.png",
    "CC logo WHITE.png": "logo-white.png",
}

WIDTHS = (480, 960, 1600)
JPEG_QUALITY = 78
WEBP_QUALITY = 72

DEFAULT_ZIP = os.path.join(os.path.expanduser("~"), "Downloads", "Re__Demo_images_.zip")
REPO_ROOT = Path(__file__).resolve().parent.parent
OUT_DIR = REPO_ROOT / "college-content" / "images"


def find_member(zf, name):
    """Find a file in the zip by its base name, ignoring any folders."""
    for info in zf.infolist():
        if not info.is_dir() and Path(info.filename).name == name:
            return info
    return None


def resize_to_width(im, width):
    if im.width <= width:
        return im.copy()
    height = round(im.height * width / im.width)
    return im.resize((width, height), Image.Resampling.LANCZOS)


def main(argv):
    src = Path(os.path.expandvars(argv[1])) if len(argv) > 1 else Path(DEFAULT_ZIP)
    if not src.is_file():
        sys.exit(f"Zip not found: {src}")

    OUT_DIR.mkdir(parents=True, exist_ok=True)
    written = 0
    missing = []

    with zipfile.ZipFile(src) as zf:
        for name, slug in PHOTOS.items():
            info = find_member(zf, name)
            if info is None:
                missing.append(name)
                continue
            with Image.open(io.BytesIO(zf.read(info))) as original:
                im = ImageOps.exif_transpose(original).convert("RGB")
            print(f"  {name:<28} {im.width}x{im.height} -> {slug}-*")
            for width in WIDTHS:
                r = resize_to_width(im, width)
                r.save(OUT_DIR / f"{slug}-{width}.jpg", "JPEG",
                       quality=JPEG_QUALITY, optimize=True, progressive=True)
                r.save(OUT_DIR / f"{slug}-{width}.webp", "WEBP", quality=WEBP_QUALITY)
                written += 2

        for name, out_name in LOGOS.items():
            info = find_member(zf, name)
            if info is None:
                missing.append(name)
                continue
            with Image.open(io.BytesIO(zf.read(info))) as im:
                im.load()
                im.save(OUT_DIR / out_name, "PNG", optimize=True)
            print(f"  {name:<28} -> {out_name}")
            written += 1

    print(f"Wrote {written} files to {OUT_DIR.relative_to(REPO_ROOT)}")
    if missing:
        sys.exit("Not found in the zip: " + ", ".join(missing))


if __name__ == "__main__":
    main(sys.argv)
