"""Generates every branded asset.

    python branding/make-branding.py

Sources
  branding/badge-source.png       the DLSS5 MASTER badge (logo, icon, installer images)
  branding/artwork-source.png     the scene; its badge is painted out for the window background

Outputs
  src/DLSS5Master/Assets/DLSS5Master.ico        app + installer icon (badge for 48px+, the "5" for 16-32px)
  src/DLSS5Master/Assets/DLSS5Master.png        256px icon preview
  src/DLSS5Master/Assets/Logo.png               header logo (soft-edged badge)
  src/DLSS5Master/Assets/Background.jpg         window background
  installer/WizardImage*.bmp, WizardSmall*.bmp  Inno Setup wizard images for 100%-250% DPI
"""
from pathlib import Path
from PIL import Image, ImageDraw, ImageFilter, ImageEnhance

ROOT = Path(__file__).resolve().parent.parent
SCENE = Image.open(ROOT / "branding" / "artwork-source.png").convert("RGB")
ASSETS = ROOT / "src" / "DLSS5Master" / "Assets"
INSTALLER = ROOT / "installer"

BADGE_SRC = Image.open(ROOT / "branding" / "badge-source.png").convert("RGB")   # the DLSS5 MASTER badge, tightly cropped
BADGE_BG = (6, 16, 26)                   # the scene's dark backdrop colour (icon canvas)
FIVE_BOX = (758, 28, 968, 238)           # the green "5" (badge coordinates), for tiny icon sizes
FIVE_TIGHT = (34, 2, 194, 208)             # the "5" inside FIVE_BOX, without the edge of the neighbouring "S"
SCENE_LOGO_AREA = (240, 170, 1400, 780)  # where the badge sits in the scene


def rounded_mask(size, radius):
    m = Image.new("L", size, 0)
    ImageDraw.Draw(m).rounded_rectangle((0, 0, size[0] - 1, size[1] - 1), radius=radius, fill=255)
    return m


def badge():
    return BADGE_SRC.copy()


def badge_scaled(width):
    b = badge()
    return b.resize((width, round(b.height * width / b.width)), Image.LANCZOS)


def plain_background():
    """The scene with its original logo, frame and pixel trails replaced by a soft glow."""
    x0, y0, x1, y1 = SCENE_LOGO_AREA
    base = SCENE.copy()
    tone = SCENE.crop((40, 860, 300, 940)).resize((1, 1), Image.LANCZOS).getpixel((0, 0))
    ImageDraw.Draw(base).rectangle((x0, y0, x1, y1), fill=tone)
    soft = base.filter(ImageFilter.GaussianBlur(90))
    glow = Image.new("RGB", SCENE.size, (0, 0, 0))
    g = ImageDraw.Draw(glow)
    for i in range(0, x1 - x0, 4):
        t = i / (x1 - x0)
        g.line((x0 + i, y0 + 60, x0 + i, y1 - 60), fill=(int(20 + 10 * t), int(50 + 60 * t), int(110 - 50 * t)))
    soft = Image.blend(soft, glow.filter(ImageFilter.GaussianBlur(80)), 0.35)
    mask = Image.new("L", SCENE.size, 0)
    ImageDraw.Draw(mask).rounded_rectangle((x0, y0, x1, y1), radius=80, fill=255)
    mask = mask.filter(ImageFilter.GaussianBlur(22))
    bg = SCENE.copy()
    bg.paste(soft, (0, 0), mask)
    return bg


def soft_edged(img, radius_frac=0.06, feather_frac=0.02):
    out = img.convert("RGBA")
    r = round(min(img.size) * radius_frac)
    out.putalpha(rounded_mask(img.size, r).filter(ImageFilter.GaussianBlur(max(1, round(min(img.size) * feather_frac)))))
    return out


def square_icon(size, lift=0.0):
    """Square icon art on a transparent background. Up to 32 px it is the green "5" on a small rounded
    tile; larger sizes show the whole badge. `lift` moves the badge up by that fraction of the size
    (the window title bar draws icons a little low next to its text)."""
    if size <= 32:
        # The green "5" alone: its dark backdrop is made transparent (alpha from brightness).
        img = BADGE_SRC.crop(FIVE_BOX)
        img = ImageEnhance.Contrast(img).enhance(1.15).convert("RGBA")
        r, g, b, _ = img.split()
        bright = Image.eval(Image.merge("RGB", (r, g, b)).convert("L"), lambda v: max(0, min(255, (v - 45) * 4)))
        img.putalpha(bright)
        # Keep only the "5" itself (no edge of the neighbouring "S"), centred on a square, aspect kept.
        img = img.crop(img.getbbox() if FIVE_TIGHT is None else FIVE_TIGHT)
        scale = size / max(img.size)
        img = img.resize((max(1, round(img.width * scale)), max(1, round(img.height * scale))), Image.LANCZOS)
        out = Image.new("RGBA", (size, size), (0, 0, 0, 0))
        out.alpha_composite(img, ((size - img.width) // 2, (size - img.height) // 2))
        return out
    b = badge_scaled(size)
    out = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    top = max(0, (size - b.height) // 2 - round(size * lift))
    out.paste(b.convert("RGBA"), (0, top))
    return out


def save_ico(path, sizes=(16, 20, 24, 32, 40, 48, 64, 96, 128, 256), lift=0.0):
    images = [square_icon(s, lift) for s in sizes]
    images[-1].save(path, format="ICO", sizes=[(s, s) for s in sizes], append_images=images[:-1])


def wizard_image(w, h):
    """Setup wizard side panel: just the badge on a transparent background (the wizard's own colour shows)."""
    img = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    b = soft_edged(badge_scaled(round(w * 0.94)), radius_frac=0.03, feather_frac=0.01)
    img.alpha_composite(b, ((w - b.width) // 2, round(h * 0.40) - b.height // 2))
    return img


def save_bmp32(img, path):
    """32-bit BI_RGB bitmap with a straight (non-premultiplied) alpha channel, bottom-up,
    as Inno Setup reads it with WizardImageAlphaFormat=defined."""
    import struct
    img = img.convert("RGBA")
    w, h = img.size
    px = img.tobytes("raw", "BGRA", 0, -1)          # bottom-up rows, BGRA order
    header = struct.pack("<2sIHHI", b"BM", 14 + 40 + len(px), 0, 0, 14 + 40)
    info = struct.pack("<IiiHHIIiiII", 40, w, h, 1, 32, 0, len(px), 2835, 2835, 0, 0)
    Path(path).write_bytes(header + info + px)


def main():
    ASSETS.mkdir(parents=True, exist_ok=True)
    save_ico(ASSETS / "DLSS5Master.ico")
    # Window title-bar icon: same art, badge raised so it lines up with the title text.
    save_ico(ASSETS / "TitleBar.ico", lift=0.24)
    square_icon(256).save(ASSETS / "DLSS5Master.png")
    soft_edged(badge_scaled(1200)).save(ASSETS / "Logo.png")
    plain_background().resize((2560, 1430), Image.LANCZOS).save(ASSETS / "Background.jpg", quality=90)
    for w, h in [(164, 314), (192, 386), (246, 459), (273, 556), (328, 604), (355, 700), (410, 797)]:
        save_bmp32(wizard_image(w, h), INSTALLER / f"WizardImage{w}.bmp")
    for s in [55, 64, 83, 92, 110, 119, 138]:
        save_bmp32(square_icon(s), INSTALLER / f"WizardSmall{s}.bmp")
    print("branding generated")


if __name__ == "__main__":
    main()
