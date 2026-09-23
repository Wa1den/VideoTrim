"""Рисует icon.ico для VideoTrim и лист предпросмотра.

Запуск: python icon/make_icon.py  (из папки videotrim; нужен Pillow)
Каждый размер рисуется отдельно в 8-кратном разрешении и уменьшается: на 16 и 24 точках
кольца и лезвия толще, иначе отверстия колец и просвет между лезвиями пропадают.
"""
from pathlib import Path
from PIL import Image, ImageDraw

HERE = Path(__file__).resolve().parent
SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
SS = 8

TOP = (139, 124, 255)
BOTTOM = (84, 64, 224)
WHITE = (255, 255, 255, 255)


def lerp(a, b, t):
    return tuple(round(x + (y - x) * t) for x, y in zip(a, b))


def draw(size):
    n = size * SS
    bold = size <= 24
    img = Image.new("RGBA", (n, n), (0, 0, 0, 0))

    # фон: скруглённый квадрат с вертикальным градиентом
    grad = Image.new("RGBA", (n, n))
    gd = ImageDraw.Draw(grad)
    for y in range(n):
        gd.line([(0, y), (n, y)], fill=lerp(TOP, BOTTOM, y / (n - 1)) + (255,))
    mask = Image.new("L", (n, n), 0)
    pad = 0 if size <= 24 else n * 0.03
    ImageDraw.Draw(mask).rounded_rectangle([pad, pad, n - 1 - pad, n - 1 - pad], radius=n * 0.22, fill=255)
    img.paste(grad, (0, 0), mask)

    d = ImageDraw.Draw(img)
    u = lambda x, y: (x * n, y * n)

    pivot = (0.5, 0.50)
    ring_y = 0.745
    ring_dx = 0.175
    r_out = 0.155 if bold else 0.14
    r_in = 0.07 if bold else 0.08
    arm = 0.10 if bold else 0.075

    for side in (-1, 1):
        tip = (0.5 + side * 0.23, 0.13)
        ring = (0.5 - side * ring_dx, ring_y)

        # лезвие: от кольца через ось к острию, сужается к концу
        px, py = pivot
        tx, ty = tip
        vx, vy = tx - px, ty - py
        ln = (vx * vx + vy * vy) ** 0.5
        nx, ny = -vy / ln, vx / ln
        w0 = (0.11 if bold else 0.085) / 2
        w1 = (0.035 if bold else 0.018) / 2
        blade = [
            (px + nx * w0, py + ny * w0),
            (tx + nx * w1, ty + ny * w1),
            (tx - nx * w1, ty - ny * w1),
            (px - nx * w0, py - ny * w0),
        ]
        d.polygon([u(*p) for p in blade], fill=WHITE)

        # плечо от оси к кольцу
        d.line([u(*pivot), u(*ring)], fill=WHITE, width=round(arm * n))

        # кольцо
        cx, cy = ring
        d.ellipse([u(cx - r_out, cy - r_out), u(cx + r_out, cy + r_out)], fill=WHITE)

    # отверстия колец вырезаются после плеч, чтобы плечо не заливало отверстие
    for side in (-1, 1):
        cx, cy = 0.5 - side * ring_dx, ring_y
        hole = Image.new("L", (n, n), 0)
        ImageDraw.Draw(hole).ellipse([u(cx - r_in, cy - r_in), u(cx + r_in, cy + r_in)], fill=255)
        img.paste(grad, (0, 0), hole)

    # винт на оси
    if not bold:
        r = 0.028
        c = lerp(TOP, BOTTOM, pivot[1]) + (255,)
        d.ellipse([u(pivot[0] - r, pivot[1] - r), u(pivot[0] + r, pivot[1] + r)], fill=c)

    return img.resize((size, size), Image.LANCZOS)


def main():
    frames = {s: draw(s) for s in SIZES}
    big = frames[256]
    big.save(HERE.parent / "icon.ico", format="ICO", sizes=[(s, s) for s in SIZES],
             append_images=[frames[s] for s in SIZES if s != 256])

    # лист: все размеры на светлом и тёмном фоне, и мелкие увеличенные по точкам
    w = sum(SIZES) + 12 * len(SIZES) + 12
    sheet = Image.new("RGBA", (max(w, 700), 2 * 280 + 200), (0, 0, 0, 255))
    for row, bg in enumerate([(243, 243, 243, 255), (32, 32, 32, 255)]):
        y0 = row * 280
        ImageDraw.Draw(sheet).rectangle([0, y0, sheet.width, y0 + 279], fill=bg)
        x = 12
        for s in SIZES:
            sheet.alpha_composite(frames[s], (x, y0 + 12))
            x += s + 12
    y0 = 560
    x = 12
    for s in (16, 20, 24, 32):
        z = frames[s].resize((s * 5, s * 5), Image.NEAREST)
        sheet.alpha_composite(z, (x, y0 + 12))
        x += s * 5 + 16
    sheet.save(HERE / "preview.png")


if __name__ == "__main__":
    main()
