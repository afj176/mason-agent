"""Renders the macOS installer's art from windows/packaging/art/source.

Run from anywhere with Pillow installed: python macos/Packaging/Installer/make_installer_art.py
Outputs sit next to this script and are committed, so building the package needs no Python.

- background.png: the welcome pose under the step list at the bottom left of every page, 164 x 200 points,
  stored at 2x (144 dpi) so it is sharp on Retina screens. The background stays transparent for light and dark.
- conclusion.html: the Summary page, with the installed pose embedded as a data URI so Installer always finds it.
"""
import base64
import io
from pathlib import Path
from PIL import Image

HERE = Path(__file__).resolve().parent
SOURCE = HERE.parents[2] / "windows" / "packaging" / "art" / "source"


def fit(pose: Path, width: int, height: int, margin: int) -> Image.Image:
    canvas = Image.new("RGBA", (width, height), (0, 0, 0, 0))
    art = Image.open(pose).convert("RGBA")
    ratio = min((width - 2 * margin) / art.width, (height - 2 * margin) / art.height)
    art = art.resize((max(1, round(art.width * ratio)), max(1, round(art.height * ratio))), Image.LANCZOS)
    # Bottom-aligned, so the pose stands on the window's lower edge under the list of steps.
    canvas.alpha_composite(art, ((width - art.width) // 2, height - margin - art.height))
    return canvas


def main() -> None:
    fit(SOURCE / "welcome.png", 328, 400, 20).save(HERE / "background.png", optimize=True, dpi=(144, 144))
    print("background.png")

    installed = fit(SOURCE / "installed.png", 440, 290, 0)
    buffer = io.BytesIO()
    installed.save(buffer, format="PNG", optimize=True)
    picture = base64.b64encode(buffer.getvalue()).decode("ascii")
    template = (HERE / "conclusion.template.html").read_text(encoding="utf8")
    (HERE / "conclusion.html").write_text(template.replace("{{INSTALLED_PNG}}", picture), encoding="utf8", newline="\n")
    print("conclusion.html")


if __name__ == "__main__":
    main()
