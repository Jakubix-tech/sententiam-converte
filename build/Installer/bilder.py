# Erzeugt die Bilder des Windows-Installers aus dem Programmsymbol (einmalig; Ergebnis liegt im Repo).
#   python build/Installer/bilder.py
from pathlib import Path
from PIL import Image

here = Path(__file__).resolve().parent
icon = Image.open(here.parent.parent / "SententiamConverte" / "Assets" / "Icon.png").convert("RGBA")
navy = (22, 58, 97)        # NavBackground der Oberfläche
accent = (255, 193, 77)    # NavAccent


def place(canvas, size, xy):
    canvas.alpha_composite(icon.resize((size, size), Image.LANCZOS), xy)


# Begrüßung/Abschluss (493 × 312): links die dunkle Leiste mit dem Symbol, rechts Platz für den Text
dialog = Image.new("RGBA", (493, 312), (255, 255, 255, 255))
dialog.paste(Image.new("RGBA", (164, 312), navy + (255,)), (0, 0))
dialog.paste(Image.new("RGBA", (4, 312), accent + (255,)), (160, 0))
place(dialog, 112, (24, 88))
dialog.convert("RGB").save(here / "Dialog.bmp")

# Kopfleiste der übrigen Seiten (493 × 58): weiß, Symbol rechts
banner = Image.new("RGBA", (493, 58), (255, 255, 255, 255))
place(banner, 46, (493 - 46 - 8, 6))
banner.convert("RGB").save(here / "Banner.bmp")
print("Dialog.bmp, Banner.bmp")
