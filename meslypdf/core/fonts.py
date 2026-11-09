"""Türkçe karakter destekli yazı tiplerini reportlab'a kaydeder."""
import os
import sys

from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont


def asset_path(*parts: str) -> str:
    base = getattr(sys, "_MEIPASS", None)
    if base:
        p = os.path.join(base, "meslypdf", "assets", *parts)
        if os.path.exists(p):
            return p
    return os.path.join(os.path.dirname(os.path.dirname(__file__)), "assets", *parts)


FONTS = {
    "Sans": "DejaVuSans.ttf",
    "Sans-Bold": "DejaVuSans-Bold.ttf",
    "Serif": "DejaVuSerif.ttf",
    "Mono": "DejaVuSansMono.ttf",
}
_registered = False


def register_fonts():
    global _registered
    if _registered:
        return
    for name, fn in FONTS.items():
        path = asset_path("fonts", fn)
        if os.path.exists(path):
            pdfmetrics.registerFont(TTFont(name, path))
    _registered = True


def font_name(bold: bool = False, family: str = "Sans") -> str:
    register_fonts()
    if bold and family == "Sans":
        return "Sans-Bold"
    return family
