"""Generates assets/micboost.ico (16-256 px) with Pillow.

Usage:  python tools/make_icon.py
"""
from pathlib import Path

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "assets" / "micboost.ico"
BG = (23, 24, 28, 255)
ACCENT = (54, 211, 153, 255)
LIGHT = (236, 236, 239, 255)
S = 1024  # draw large, then downsample for clean anti-aliasing


def draw(with_chevron: bool) -> Image.Image:
    k = S / 256
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle([8 * k, 8 * k, 248 * k, 248 * k], radius=56 * k, fill=BG)
    # Microphone capsule, stand and base
    d.rounded_rectangle([92 * k, 40 * k, 164 * k, 156 * k], radius=36 * k, fill=ACCENT)
    w = int(16 * k)
    d.arc([64 * k, 84 * k, 192 * k, 188 * k], start=0, end=180, fill=ACCENT, width=w)
    d.line([128 * k, 188 * k, 128 * k, 214 * k], fill=ACCENT, width=w)
    d.line([96 * k, 214 * k, 160 * k, 214 * k], fill=ACCENT, width=w)
    for x, y in ((128, 214), (96, 214), (160, 214), (128, 188)):
        r = w / 2
        d.ellipse([x * k - r, y * k - r, x * k + r, y * k + r], fill=ACCENT)
    if with_chevron:
        w2 = int(14 * k)
        pts = [(186 * k, 80 * k), (208 * k, 56 * k), (230 * k, 80 * k)]
        d.line(pts, fill=LIGHT, width=w2, joint="curve")
        for x, y in pts:
            r = w2 / 2
            d.ellipse([x - r, y - r, x + r, y + r], fill=LIGHT)
    return img


def main() -> None:
    big = draw(with_chevron=True)
    small = draw(with_chevron=False)
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    frames = [(small if s < 32 else big).resize((s, s), Image.LANCZOS) for s in sizes]
    OUT.parent.mkdir(parents=True, exist_ok=True)
    frames[-1].save(OUT, format="ICO", sizes=[(s, s) for s in sizes], append_images=frames[:-1])
    big.resize((256, 256), Image.LANCZOS).save(ROOT / "assets" / "micboost-256.png")
    print(f"Wrote {OUT}")


if __name__ == "__main__":
    main()
