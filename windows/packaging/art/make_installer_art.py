"""Renders the installer's pose art at 100, 150 and 200% scale from art/source.

Run from anywhere with Pillow installed: python windows/packaging/art/make_installer_art.py
Outputs sit next to this script and are committed, so building the installer needs no Python.

- welcome / installed: the large wizard image on the first and last page (164 x 314 at 100%).
- the other poses: the band at the left of the inner pages (150 x 220 at 100%), drawn by MasonAgent.iss.
Backgrounds stay transparent; Setup paints the band colour behind them in light and dark mode.
"""
from pathlib import Path
from PIL import Image

HERE = Path(__file__).resolve().parent
OUTER = {"welcome", "installed"}
SCALES = (100, 150, 200)


def render(pose: Path, scale: int) -> Image.Image:
    outer = pose.stem in OUTER
    width, height = (164, 314) if outer else (150, 220)
    margin = 10
    canvas = Image.new("RGBA", (width * scale // 100, height * scale // 100), (0, 0, 0, 0))
    art = Image.open(pose).convert("RGBA")
    room_w = canvas.width - 2 * margin * scale // 100
    room_h = canvas.height - 2 * margin * scale // 100
    ratio = min(room_w / art.width, room_h / art.height)
    art = art.resize((max(1, round(art.width * ratio)), max(1, round(art.height * ratio))), Image.LANCZOS)
    canvas.alpha_composite(art, ((canvas.width - art.width) // 2, (canvas.height - art.height) // 2))
    return canvas


def main() -> None:
    for pose in sorted((HERE / "source").glob("*.png")):
        for scale in SCALES:
            render(pose, scale).save(HERE / f"{pose.stem}-{scale}.png", optimize=True)
            print(f"{pose.stem}-{scale}.png")


if __name__ == "__main__":
    main()
