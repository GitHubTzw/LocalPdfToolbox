"""Draw the original application mark using local shapes and fonts only."""
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont

root = Path(__file__).resolve().parents[1]
assets = root / "assets"
assets.mkdir(exist_ok=True)
scale = 4
canvas = Image.new("RGBA", (512 * scale, 512 * scale), (0, 0, 0, 0))
draw = ImageDraw.Draw(canvas)
def box(values): return tuple(int(v * scale) for v in values)
def polygon(points, fill): draw.polygon([(int(x * scale), int(y * scale)) for x, y in points], fill=fill)
draw.rounded_rectangle(box((16, 16, 496, 496)), radius=110 * scale, fill="#165E50")
draw.rounded_rectangle(box((98, 132, 344, 436)), radius=24 * scale, fill="#7FCAB4")
draw.rounded_rectangle(box((142, 78, 404, 400)), radius=22 * scale, fill="#FFFFFF")
polygon([(320, 78), (404, 78), (404, 162)], "#165E50")
polygon([(320, 78), (320, 162), (404, 162)], "#D6EEE6")
font = ImageFont.truetype(r"C:\Windows\Fonts\arialbd.ttf", 78 * scale)
draw.text((172 * scale, 201 * scale), "PDF", font=font, fill="#185D50")
draw.rounded_rectangle(box((177, 311, 365, 324)), radius=6 * scale, fill="#A0C9BB")
draw.rounded_rectangle(box((177, 344, 306, 357)), radius=6 * scale, fill="#A0C9BB")
draw.rounded_rectangle(box((83, 346, 176, 359)), radius=6 * scale, fill="#F1C678")
polygon([(156, 328), (184, 352), (156, 377)], "#F1C678")
image = canvas.resize((512, 512), Image.Resampling.LANCZOS)
image.save(assets / "app-logo.png")
image.save(assets / "app.ico", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
svg = '''<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 512">
<title>本地 PDF 工作台原创标志：双页与合并箭头</title>
<rect x="16" y="16" width="480" height="480" rx="110" fill="#165E50"/>
<rect x="98" y="132" width="246" height="304" rx="24" fill="#7FCAB4"/>
<path d="M164 78H320L404 162V378Q404 400 382 400H164Q142 400 142 378V100Q142 78 164 78Z" fill="white"/>
<path d="M320 78V162H404Z" fill="#D6EEE6"/>
<text x="172" y="273" font-family="Arial, sans-serif" font-size="78" font-weight="700" fill="#185D50">PDF</text>
<rect x="177" y="311" width="188" height="13" rx="6" fill="#A0C9BB"/>
<rect x="177" y="344" width="129" height="13" rx="6" fill="#A0C9BB"/>
<rect x="83" y="346" width="93" height="13" rx="6" fill="#F1C678"/>
<path d="M156 328L184 352L156 377Z" fill="#F1C678"/>
</svg>'''
(assets / "app-logo.svg").write_text(svg, encoding="utf-8")
print("Created app-logo.svg, app-logo.png and multi-resolution app.ico locally.")
