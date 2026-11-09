"""Testler için örnek belgeler üretir."""
import os
from reportlab.pdfgen import canvas
from reportlab.lib.pagesizes import A4
from meslypdf.core.fonts import font_name


def make_text_pdf(path, pages=3, extra=""):
    c = canvas.Canvas(path, pagesize=A4)
    for i in range(pages):
        c.setFont(font_name(True), 20)
        c.drawString(72, 770, f"Bölüm {i + 1}: Sözleşme Şartları")
        c.setFont(font_name(), 12)
        y = 730
        lines = [
            "Bu belge Mesly PDF test belgesidir. Çığ, şeker, ıhlamur, İstanbul, Ödeme.",
            f"Sayfa numarası {i + 1}. Toplam tutar 1.250,00 TL olarak belirlenmiştir.",
            "İletişim: ornek@mesly.com.tr  Tel: 0532 123 45 67  TC: 12345678901",
            "Taraflar işbu sözleşmeyi okuyup kabul etmiştir." + extra,
        ]
        for ln in lines:
            c.drawString(72, y, ln)
            y -= 20
        c.showPage()
    c.save()
    return path


def make_table_pdf(path):
    c = canvas.Canvas(path, pagesize=A4)
    c.setFont(font_name(True), 16)
    c.drawString(72, 780, "Fatura Tablosu")
    c.setFont(font_name(), 11)
    rows = [["Ürün", "Adet", "Fiyat"], ["Kalem", "10", "25,00"], ["Defter", "5", "40,00"], ["Silgi", "20", "5,00"]]
    x0, y0, cw, rh = 72, 740, 120, 24
    for r, row in enumerate(rows):
        for k, cell in enumerate(row):
            c.rect(x0 + k * cw, y0 - r * rh, cw, rh)
            c.drawString(x0 + k * cw + 6, y0 - r * rh + 8, cell)
    c.showPage()
    c.save()
    return path


def make_scan_image(path, text_lines=None, angle=0):
    from PIL import Image, ImageDraw, ImageFont
    from meslypdf.core.fonts import asset_path
    img = Image.new("RGB", (1654, 2339), "white")  # A4 @200dpi
    d = ImageDraw.Draw(img)
    f = ImageFont.truetype(asset_path("fonts", "DejaVuSans.ttf"), 34)
    fb = ImageFont.truetype(asset_path("fonts", "DejaVuSans-Bold.ttf"), 52)
    d.text((150, 150), "TARANMIŞ BELGE", font=fb, fill="black")
    y = 300
    for ln in text_lines or ["Merhaba dünya, bu bir OCR testidir.",
                             "Türkçe karakterler: çğıöşü ÇĞİÖŞÜ",
                             "Fatura numarası 2026-0042 tarih 09.11.2026"]:
        d.text((150, y), ln, font=f, fill="black")
        y += 70
    if angle:
        img = img.rotate(angle, expand=False, fillcolor="white")
    img.save(path, dpi=(200, 200))
    return path
