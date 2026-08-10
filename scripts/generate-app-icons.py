#!/usr/bin/env python3
"""Generate OpenSteam app icon assets from a source PNG."""
from __future__ import annotations

import argparse
import struct
from pathlib import Path

from PIL import Image


def save_png(img: Image.Image, path: Path) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    img.save(path, format="PNG", optimize=True)


def fit_square(source: Image.Image, size: int) -> Image.Image:
    src = source.convert("RGBA")
    return src.resize((size, size), Image.Resampling.LANCZOS)


def fit_wide(source: Image.Image, width: int, height: int) -> Image.Image:
    src = source.convert("RGBA")
    scale = min(width / src.width, height / src.height)
    new_w = max(1, int(src.width * scale))
    new_h = max(1, int(src.height * scale))
    resized = src.resize((new_w, new_h), Image.Resampling.LANCZOS)
    canvas = Image.new("RGBA", (width, height), (0, 0, 0, 255))
    canvas.paste(resized, ((width - new_w) // 2, (height - new_h) // 2), resized)
    return canvas


def write_ico(path: Path, sizes: list[int], source: Image.Image) -> None:
    images = [fit_square(source, size) for size in sizes]
    header = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    entries = []
    blobs = []

    for img, size in zip(images, sizes):
        png = img.convert("RGBA")
        import io

        buf = io.BytesIO()
        png.save(buf, format="PNG")
        data = buf.getvalue()
        entries.append(
            struct.pack(
                "<BBBBHHII",
                size if size < 256 else 0,
                size if size < 256 else 0,
                0,
                0,
                1,
                32,
                len(data),
                offset,
            )
        )
        blobs.append(data)
        offset += len(data)

    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("wb") as f:
        f.write(header)
        for entry in entries:
            f.write(entry)
        for blob in blobs:
            f.write(blob)


def generate(source: Path, assets_dir: Path) -> None:
    img = Image.open(source)

    save_png(fit_square(img, 512), assets_dir / "OpenSteamAppLogo.png")
    save_png(fit_square(img, 256), assets_dir / "OpenSteamMark.png")
    save_png(fit_square(img, 300), assets_dir / "Square150x150Logo.scale-200.png")
    save_png(fit_square(img, 88), assets_dir / "Square44x44Logo.scale-200.png")
    save_png(fit_square(img, 24), assets_dir / "Square44x44Logo.targetsize-24_altform-unplated.png")
    save_png(fit_square(img, 48), assets_dir / "Square44x44Logo.targetsize-48_altform-lightunplated.png")
    save_png(fit_square(img, 96), assets_dir / "LockScreenLogo.scale-200.png")
    save_png(fit_square(img, 50), assets_dir / "StoreLogo.png")
    save_png(fit_wide(img, 620, 300), assets_dir / "Wide310x150Logo.scale-200.png")
    save_png(fit_wide(img, 620, 300), assets_dir / "SplashScreen.scale-200.png")
    write_ico(assets_dir / "AppIcon.ico", [16, 24, 32, 48, 64, 128, 256], img)

    print(f"Generated icons in {assets_dir}")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("source", type=Path)
    parser.add_argument(
        "--assets",
        type=Path,
        default=Path(__file__).resolve().parents[1] / "ManifestApp" / "Assets",
    )
    args = parser.parse_args()
    generate(args.source.resolve(), args.assets.resolve())


if __name__ == "__main__":
    main()
