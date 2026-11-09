"""Güvenlik: şifreleme, parola kaldırma, karartma (redaction), gizli veri temizleme."""
from __future__ import annotations

import io
import re

import pikepdf
from PIL import Image, ImageDraw

ENCRYPTIONS = {"RC4-40": (2, 40), "RC4-128": (3, 128), "AES-128": (4, 128), "AES-256": (6, 256)}


def encrypt(path: str, out: str, user_pw: str = "", owner_pw: str = "", method: str = "AES-256",
            allow_print: bool = True, allow_modify: bool = True, allow_copy: bool = True,
            allow_annotate: bool = True, allow_forms: bool = True, allow_assemble: bool = True,
            password: str | None = None) -> str:
    R, _ = ENCRYPTIONS[method]
    perms = pikepdf.Permissions(
        print_lowres=allow_print, print_highres=allow_print, modify_other=allow_modify,
        extract=allow_copy, accessibility=True, modify_annotation=allow_annotate,
        modify_form=allow_forms, modify_assembly=allow_assemble)
    enc = pikepdf.Encryption(user=user_pw, owner=owner_pw or user_pw or "mesly-owner", R=R,
                             allow=perms, aes=method.startswith("AES"), metadata=R >= 4)
    with pikepdf.open(path, password=password or "", allow_overwriting_input=True) as pdf:
        pdf.save(out, encryption=enc)
    return out


def decrypt(path: str, out: str, password: str) -> str:
    with pikepdf.open(path, password=password, allow_overwriting_input=True) as pdf:
        pdf.save(out)
    return out


def encryption_info(path: str, password: str = "") -> dict:
    with pikepdf.open(path, password=password) as pdf:
        if not pdf.is_encrypted:
            return {"encrypted": False}
        e = pdf.encryption
        a = pdf.allow
        return {"encrypted": True, "R": e.R, "bits": e.bits, "method": str(e.stream_method),
                "print": a.print_highres, "modify": a.modify_other, "copy": a.extract,
                "annotate": a.modify_annotation}


# ------------------------------------------------------------------ karartma
PATTERNS = {
    "TC Kimlik No": r"\b[1-9]\d{10}\b",
    "IBAN": r"\bTR\d{2}\s?(?:\d{4}\s?){5}\d{2}\b",
    "E-posta": r"[\w.+-]+@[\w-]+\.[\w.-]+",
    "Telefon": r"(?:\+90\s?)?0?\(?5\d{2}\)?[\s-]?\d{3}[\s-]?\d{2}[\s-]?\d{2}",
    "Kredi Kartı": r"\b(?:\d{4}[\s-]?){3}\d{4}\b",
    "Tarih": r"\b\d{1,2}[./]\d{1,2}[./]\d{2,4}\b",
}


def find_text_rects(path: str, terms: list[str] | None = None, regex: list[str] | None = None,
                    password: str | None = None, match_case: bool = False) -> dict[int, list[tuple]]:
    """Karartılacak bölgeleri bulur (sayfa -> PDF dikdörtgenleri)."""
    from .document import PdfDocument
    found: dict[int, list[tuple]] = {}
    with PdfDocument(path, password) as doc:
        for t in terms or []:
            for h in doc.search(t, match_case=match_case):
                found.setdefault(h.page, []).extend(h.rects)
        if regex:
            for i in range(doc.page_count):
                text = doc.page_text(i)
                for rx in regex:
                    for m in re.finditer(rx, text):
                        for h in doc.search(m.group(0), match_case=True, pages=[i]):
                            found.setdefault(i, []).extend(h.rects)
    for k in found:
        found[k] = list(dict.fromkeys(tuple(round(v, 2) for v in r) for r in found[k]))
    return found


def _intersects(a, b) -> bool:
    return not (a[2] <= b[0] or a[0] >= b[2] or a[3] <= b[1] or a[1] >= b[3])


def redact_regions(path: str, out: str, regions: dict[int, list[tuple]], fill: str = "#000000",
                   rasterize: bool = True, dpi: int = 200, ocr_lang: str | None = "tur+eng",
                   password: str | None = None) -> str:
    """Bölgeleri kalıcı olarak karartır.
    rasterize=True: etkilenen sayfa görüntüye çevrilir, alttaki tüm içerik yok edilir,
    istenirse OCR ile aranabilir metin katmanı geri eklenir (güvenli mod).
    rasterize=False: kesişen metin nesneleri silinir ve üstü boyanır (düzenleme modu)."""
    import pypdfium2 as pdfium
    import pypdfium2.raw as pdfium_c

    from .document import PDFIUM_LOCK
    if not rasterize:
        PDFIUM_LOCK.acquire()
        try:
            _remove_text_objects(path, out, regions, password)
        finally:
            PDFIUM_LOCK.release()
        from .editing import overlay
        tmp_in = out + ".r.pdf"
        from reportlab.lib.colors import HexColor
        with pikepdf.open(tmp_in) as pdf:
            offs = {i: (float(pdf.pages[i].mediabox[0]), float(pdf.pages[i].mediabox[1])) for i in regions}

        def draw(c, i, n, w, h):
            if not fill:
                return
            c.setFillColor(HexColor(fill))
            x0, y0 = offs.get(i, (0, 0))
            for (l, b, r, t) in regions.get(i, []):
                c.rect(l - x0, b - y0, r - l, t - b, stroke=0, fill=1)
        overlay(tmp_in, out, draw, list(regions.keys()), mark="redact", raw=True)
        import os
        os.remove(tmp_in)
        return out
    # Güvenli mod
    from .document import PdfDocument
    from reportlab.pdfgen import canvas
    from reportlab.lib.utils import ImageReader
    scale = dpi / 72.0
    new_pages: dict[int, bytes] = {}
    with PdfDocument(path, password) as doc:
        for pi, rects in regions.items():
            img = doc.render(pi, scale=scale)
            pdoc = doc._doc[pi]
            w, h = pdoc.get_size()
            rot = pdoc.get_rotation()
            pdoc.close()
            d = ImageDraw.Draw(img)
            for (l, b, r, t) in rects:
                pts = _page_rect_to_pixels((l, b, r, t), w, h, rot, scale)
                d.rectangle(pts, fill=fill)
            # Görüntüden yeni sayfa
            vw, vh = (h, w) if rot in (90, 270) else (w, h)
            if ocr_lang:
                try:
                    from .ocr import image_to_searchable_pdf_page
                    new_pages[pi] = image_to_searchable_pdf_page(img, vw, vh, ocr_lang)
                    continue
                except Exception:
                    pass
            buf = io.BytesIO()
            c = canvas.Canvas(buf, pagesize=(vw, vh))
            c.drawImage(ImageReader(img), 0, 0, vw, vh)
            c.showPage()
            c.save()
            new_pages[pi] = buf.getvalue()
    with pikepdf.open(path, password=password or "", allow_overwriting_input=True) as pdf:
        for pi, data in new_pages.items():
            src = pikepdf.open(io.BytesIO(data))
            pdf.pages.insert(pi, src.pages[0])
            del pdf.pages[pi + 1]
            # Bu sayfadaki açıklamaları da temizle (gizli metin içerebilir)
        pdf.save(out)
    return out


def _remove_text_objects(path, out, regions, password):
    import pypdfium2 as pdfium
    import pypdfium2.raw as pdfium_c
    doc = pdfium.PdfDocument(path, password=password)
    for pi, rects in regions.items():
        page = doc[pi]
        removed = False
        for obj in list(page.get_objects(max_depth=1)):
            if obj.type == pdfium_c.FPDF_PAGEOBJ_TEXT:
                try:
                    ob = obj.get_bounds()
                except Exception:
                    continue
                if any(_intersects(ob, r) for r in rects):
                    page.remove_obj(obj)
                    removed = True
            del obj
        if removed:
            page.gen_content()
        del page
    buf = io.BytesIO()
    doc.save(buf)
    del doc
    with open(out + ".r.pdf", "wb") as f:
        f.write(buf.getvalue())


def _page_rect_to_pixels(rect, w, h, rot, scale):
    l, b, r, t = rect
    corners = [(l, b), (r, t)]
    px = []
    for x, y in corners:
        if rot == 0:
            vx, vy = x, h - y
        elif rot == 90:
            vx, vy = y, x
        elif rot == 180:
            vx, vy = w - x, y
        else:
            vx, vy = h - y, w - x
        px.append((vx * scale, vy * scale))
    (x1, y1), (x2, y2) = px
    return [min(x1, x2) - 1, min(y1, y2) - 1, max(x1, x2) + 1, max(y1, y2) + 1]


def redact_text(path: str, out: str, terms: list[str] | None = None, patterns: list[str] | None = None,
                **kw) -> tuple[str, int]:
    regex = [PATTERNS.get(p, p) for p in (patterns or [])]
    regions = find_text_rects(path, terms, regex, kw.get("password"))
    count = sum(len(v) for v in regions.values())
    if not regions:
        import shutil
        shutil.copyfile(path, out)
        return out, 0
    redact_regions(path, out, regions, **kw)
    return out, count


# ------------------------------------------------------------------ gizli veri temizleme
def remove_hidden_data(path: str, out: str, metadata=True, attachments=True, javascript=True,
                       comments=True, forms=False, bookmarks=False, links=False, hidden_layers=True,
                       thumbnails=True, password: str | None = None) -> tuple[str, list[str]]:
    removed = []
    with pikepdf.open(path, password=password or "", allow_overwriting_input=True) as pdf:
        root = pdf.Root
        if metadata:
            for k in list(pdf.docinfo.keys()):
                del pdf.docinfo[k]
            if "/Metadata" in root:
                del root.Metadata
            for p in pdf.pages:
                for k in ("/PieceInfo", "/Metadata"):
                    if k in p.obj:
                        del p.obj[k]
            removed.append("Meta veriler")
        names = root.get("/Names")
        if attachments and len(pdf.attachments):
            for n in list(pdf.attachments.keys()):
                del pdf.attachments[n]
            removed.append("Ekli dosyalar")
        if javascript:
            hit = False
            if names is not None and "/JavaScript" in names:
                del names.JavaScript
                hit = True
            for k in ("/OpenAction", "/AA"):
                if k in root:
                    del root[k]
                    hit = True
            for p in pdf.pages:
                if "/AA" in p.obj:
                    del p.obj.AA
                    hit = True
            if hit:
                removed.append("JavaScript / eylemler")
        if comments or links or forms:
            for p in pdf.pages:
                annots = p.obj.get("/Annots")
                if annots is None:
                    continue
                keep = pikepdf.Array()
                for a in annots:
                    st = str(a.get("/Subtype", ""))
                    if st == "/Link":
                        if not links:
                            keep.append(a)
                    elif st == "/Widget":
                        if not forms:
                            keep.append(a)
                    elif not comments:
                        keep.append(a)
                p.obj.Annots = keep
            if comments:
                removed.append("Yorumlar")
            if links:
                removed.append("Bağlantılar")
        if forms and "/AcroForm" in root:
            del root.AcroForm
            removed.append("Form alanları")
        if bookmarks and "/Outlines" in root:
            del root.Outlines
            removed.append("Yer imleri")
        if hidden_layers and "/OCProperties" in root:
            del root.OCProperties
            removed.append("Gizli katmanlar")
        if thumbnails:
            for p in pdf.pages:
                if "/Thumb" in p.obj:
                    del p.obj.Thumb
        pdf.remove_unreferenced_resources()
        pdf.save(out)
    return out, removed
