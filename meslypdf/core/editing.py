"""İçerik düzenleme: filigran, üstbilgi/altbilgi, Bates, damga, metin/görsel ekleme,
metin düzeltme, bağlantılar, yer imleri, meta veriler, ekler ve sıkıştırma."""
from __future__ import annotations

import datetime as _dt
import io
import os
from typing import Callable, Iterable

import pikepdf
from PIL import Image
from reportlab.lib.colors import HexColor, Color
from reportlab.lib.utils import ImageReader
from reportlab.pdfgen import canvas

from .fonts import font_name

MARK_KEY = "/MeslyMark"


# ---------------------------------------------------------------- yardımcılar
def _box(page: pikepdf.Page) -> tuple[float, float, float, float]:
    b = page.obj.get("/CropBox") or page.mediabox
    x0, y0, x1, y1 = [float(v) for v in b]
    return min(x0, x1), min(y0, y1), max(x0, x1), max(y0, y1)


def _rotation(page: pikepdf.Page) -> int:
    return int(page.obj.get("/Rotate", 0)) % 360


def visual_size(page: pikepdf.Page) -> tuple[float, float]:
    x0, y0, x1, y1 = _box(page)
    w, h = x1 - x0, y1 - y0
    return (h, w) if _rotation(page) in (90, 270) else (w, h)


DrawFn = Callable[[canvas.Canvas, int, int, float, float], None]


def overlay(path: str, out: str, draw: DrawFn, pages: Iterable[int] | None = None,
            under: bool = False, mark: str | None = None, password: str | None = None,
            raw: bool = False) -> str:
    """Her sayfaya reportlab ile çizilen bir katman ekler. draw(c, index, total, vw, vh)
    görsel (döndürülmüş) koordinatlarda çizer."""
    with pikepdf.open(path, password=password or "", allow_overwriting_input=True) as pdf:
        total = len(pdf.pages)
        idx = list(range(total) if pages is None else pages)
        buf = io.BytesIO()
        c = canvas.Canvas(buf)
        for i in idx:
            p = pdf.pages[i]
            x0, y0, x1, y1 = _box(p)
            w, h = x1 - x0, y1 - y0
            rot = 0 if raw else _rotation(p)
            vw, vh = (h, w) if rot in (90, 270) else (w, h)
            c.setPageSize((vw, vh))
            c.saveState()
            draw(c, i, total, vw, vh)
            c.restoreState()
            c.showPage()
        c.save()
        with pikepdf.open(io.BytesIO(buf.getvalue())) as ov:
            for k, i in enumerate(idx):
                p = pdf.pages[i]
                if raw:
                    # Sayfa koordinatlarında, döndürme telafisi olmadan yerleştir
                    x0, y0, _, _ = _box(p)
                    form = pdf.copy_foreign(ov.pages[k].as_form_xobject())
                    if "/Resources" not in p.obj:
                        p.obj.Resources = pikepdf.Dictionary()
                    if "/XObject" not in p.obj.Resources:
                        p.obj.Resources.XObject = pikepdf.Dictionary()
                    name = "/MeslyR%d" % len(p.obj.Resources.XObject.keys())
                    while name in p.obj.Resources.XObject:
                        name += "x"
                    p.obj.Resources.XObject[name] = form
                    p.contents_coalesce()
                    p.contents_add(pdf.make_stream(b"q"), prepend=True)
                    p.contents_add(pdf.make_stream(f"Q q 1 0 0 1 {x0} {y0} cm {name} Do Q".encode()))
                    name = pikepdf.Name(name)
                else:
                    # pikepdf sayfa döndürmesini telafi eder -> katman kullanıcıya düz görünür
                    rect = pikepdf.Rectangle(*_box(p))
                    src = ov.pages[k]
                    name = p.add_underlay(src, rect) if under else p.add_overlay(src, rect)
                if mark:
                    try:
                        xo = p.obj.Resources.XObject[name]
                        xo[MARK_KEY] = pikepdf.Name("/" + mark)
                    except Exception:
                        pass
        pdf.save(out)
    return out


def remove_marks(path: str, out: str, mark: str | None = None) -> tuple[str, int]:
    """Bu uygulamanın eklediği filigran/damga/üstbilgi katmanlarını kaldırır."""
    removed = 0
    with pikepdf.open(path, allow_overwriting_input=True) as pdf:
        for p in pdf.pages:
            res = p.obj.get("/Resources")
            xobjs = res.get("/XObject") if res is not None else None
            if xobjs is None:
                continue
            names = set()
            for k in list(xobjs.keys()):
                xo = xobjs[k]
                if MARK_KEY in xo and (mark is None or str(xo[MARK_KEY]) == "/" + mark):
                    names.add(k)
            if not names:
                continue
            ops = pikepdf.parse_content_stream(p)
            new = []
            for operands, op in ops:
                if str(op) == "Do" and operands and str(operands[0]) in names:
                    removed += 1
                    continue
                new.append((operands, op))
            p.obj.Contents = pdf.make_stream(pikepdf.unparse_content_stream(new))
            for k in names:
                del xobjs[k]
        pdf.save(out)
    return out, removed


def _color(hexstr: str, alpha: float = 1.0) -> Color:
    col = HexColor(hexstr)
    return Color(col.red, col.green, col.blue, alpha=alpha)


# ---------------------------------------------------------------- filigran
def add_watermark(path: str, out: str, text: str = "", image: str | None = None,
                  size: float = 60, color: str = "#C62828", opacity: float = 0.25,
                  angle: float = 45, under: bool = False, pages=None, scale: float = 0.5,
                  password: str | None = None) -> str:
    def draw(c, i, n, w, h):
        c.saveState()
        c.translate(w / 2, h / 2)
        c.rotate(angle)
        if image:
            img = Image.open(image)
            iw = w * scale
            ih = iw * img.height / img.width
            c.setFillAlpha(opacity)
            c.drawImage(ImageReader(img), -iw / 2, -ih / 2, iw, ih, mask="auto")
        if text:
            c.setFillColor(_color(color, opacity))
            c.setFont(font_name(True), size)
            for li, line in enumerate(text.split("\n")):
                c.drawCentredString(0, -size / 3 - li * size * 1.2, line)
        c.restoreState()
    return overlay(path, out, draw, pages, under=under, mark="watermark", password=password)


# ---------------------------------------------------------------- üstbilgi / altbilgi
def _expand(tpl: str, i: int, n: int, filename: str) -> str:
    now = _dt.datetime.now()
    return (tpl.replace("{sayfa}", str(i + 1)).replace("{page}", str(i + 1))
               .replace("{toplam}", str(n)).replace("{total}", str(n))
               .replace("{tarih}", now.strftime("%d.%m.%Y")).replace("{date}", now.strftime("%d.%m.%Y"))
               .replace("{saat}", now.strftime("%H:%M")).replace("{dosya}", filename).replace("{file}", filename))


def add_header_footer(path: str, out: str, header: dict | None = None, footer: dict | None = None,
                      size: float = 9, color: str = "#333333", margin: float = 24, pages=None,
                      start_number: int = 1) -> str:
    """header/footer: {'left': '...', 'center': '...', 'right': '...'}; {sayfa} {toplam} {tarih} {dosya}."""
    header = header or {}
    footer = footer or {}
    fname = os.path.basename(path)

    def draw(c, i, n, w, h):
        c.setFont(font_name(), size)
        c.setFillColor(HexColor(color))
        num = i + start_number - 1
        for spec, y in ((header, h - margin), (footer, margin - size / 2)):
            for pos, tpl in spec.items():
                if not tpl:
                    continue
                s = _expand(tpl, num, n + start_number - 1, fname)
                if pos == "left":
                    c.drawString(margin, y, s)
                elif pos == "center":
                    c.drawCentredString(w / 2, y, s)
                else:
                    c.drawRightString(w - margin, y, s)
    return overlay(path, out, draw, pages, mark="headerfooter")


# ---------------------------------------------------------------- Bates numaralandırma
def bates_number(paths: list[str], out_dir: str, prefix: str = "", suffix: str = "", start: int = 1,
                 digits: int = 6, position: str = "right", size: float = 9) -> tuple[list[str], int]:
    """Birden çok belgeye ardışık Bates numarası ekler. Sonraki numarayı da döndürür."""
    os.makedirs(out_dir, exist_ok=True)
    counter = [start]
    outs = []
    for path in paths:
        def draw(c, i, n, w, h):
            label = f"{prefix}{counter[0]:0{digits}d}{suffix}"
            counter[0] += 1
            c.setFont(font_name(True), size)
            y = 18
            if position == "left":
                c.drawString(24, y, label)
            elif position == "center":
                c.drawCentredString(w / 2, y, label)
            else:
                c.drawRightString(w - 24, y, label)
        out = os.path.join(out_dir, os.path.basename(path))
        overlay(path, out, draw, mark="bates")
        outs.append(out)
    return outs, counter[0]


# ---------------------------------------------------------------- damgalar
STAMPS = {
    "ONAYLANDI": "#2E7D32",
    "REDDEDİLDİ": "#C62828",
    "TASLAK": "#1565C0",
    "GİZLİ": "#6A1B9A",
    "ÖDENDİ": "#00838F",
    "KOPYA": "#EF6C00",
    "ACİL": "#D84315",
    "İNCELENDİ": "#455A64",
}


def add_stamp(path: str, out: str, text: str = "ONAYLANDI", pages=None, x: float | None = None,
              y: float | None = None, color: str | None = None, with_date: bool = True,
              image: str | None = None, width: float = 160, author: str = "") -> str:
    """Damga ekler. x,y verilmezse sağ üst köşeye koyar (görsel koordinatlar, sol-alt orijin)."""
    color = color or STAMPS.get(text.upper(), "#C62828")

    def draw(c, i, n, w, h):
        sw = width
        sh = width * 0.36 if not image else None
        px = x if x is not None else w - sw - 36
        if image:
            img = Image.open(image)
            sh2 = sw * img.height / img.width
            py = y if y is not None else h - sh2 - 36
            c.drawImage(ImageReader(img), px, py, sw, sh2, mask="auto")
            return
        py = y if y is not None else h - sh - 36
        col = HexColor(color)
        c.saveState()
        c.setStrokeColor(col)
        c.setFillColor(_color(color, 0.08))
        c.setLineWidth(2.2)
        c.roundRect(px, py, sw, sh, 8, stroke=1, fill=1)
        c.setLineWidth(0.8)
        c.roundRect(px + 4, py + 4, sw - 8, sh - 8, 6, stroke=1, fill=0)
        c.setFillColor(col)
        fs = min(22, (sw - 20) / max(1, len(text)) * 1.7)
        c.setFont(font_name(True), fs)
        ty = py + sh / 2 + (2 if with_date else -fs / 3)
        c.drawCentredString(px + sw / 2, ty, text)
        if with_date:
            c.setFont(font_name(), 8)
            sub = _dt.datetime.now().strftime("%d.%m.%Y %H:%M") + (f" · {author}" if author else "")
            c.drawCentredString(px + sw / 2, py + 10, sub)
        c.restoreState()
    return overlay(path, out, draw, pages, mark="stamp")


# ---------------------------------------------------------------- metin / görsel ekleme
def add_text(path: str, out: str, page: int, x: float, y: float, text: str, size: float = 12,
             color: str = "#000000", bold: bool = False, family: str = "Sans", width: float | None = None) -> str:
    """Görsel koordinatlarda (sol-alt orijin) metin ekler; width verilirse satır kaydırır."""
    from reportlab.lib.styles import ParagraphStyle
    from reportlab.platypus import Paragraph

    def draw(c, i, n, w, h):
        fn = font_name(bold, family)
        if width:
            style = ParagraphStyle("p", fontName=fn, fontSize=size, leading=size * 1.25,
                                   textColor=HexColor(color))
            safe = text.replace("&", "&amp;").replace("<", "&lt;").replace("\n", "<br/>")
            para = Paragraph(safe, style)
            _, ph = para.wrap(width, h)
            para.drawOn(c, x, y - ph + size)
        else:
            c.setFont(fn, size)
            c.setFillColor(HexColor(color))
            for k, line in enumerate(text.split("\n")):
                c.drawString(x, y - k * size * 1.25, line)
    return overlay(path, out, draw, [page], mark="text")


def add_image(path: str, out: str, page: int, image: str, x: float, y: float, w: float,
              h: float | None = None) -> str:
    img = Image.open(image)
    h = h or w * img.height / img.width

    def draw(c, i, n, pw, ph):
        c.drawImage(ImageReader(img), x, y, w, h, mask="auto")
    return overlay(path, out, draw, [page], mark="image")


def text_lines(path: str, page: int, password: str | None = None) -> list[dict]:
    """Sayfadaki metin satırlarını (PDF metin nesnelerinden) döndürür:
    {'bounds': (l,b,r,t), 'text', 'size', 'bold', 'baseline'}"""
    import pypdfium2 as pdfium
    import pypdfium2.raw as pdfium_c
    doc = pdfium.PdfDocument(path, password=password)
    pg = doc[page]
    tp = pg.get_textpage()
    objs = []
    for o in pg.get_objects(max_depth=1):  # yalnızca üst düzey nesneler (katmanlar hariç)
        if o.type != pdfium_c.FPDF_PAGEOBJ_TEXT:
            continue
        l, b, r, t = o.get_bounds()
        m = o.get_matrix()
        scale = (m.a ** 2 + m.b ** 2) ** 0.5 or 1
        try:
            fname = o.get_font().get_base_name() or ""
            weight = o.get_font().get_weight()
        except Exception:
            fname, weight = "", 400
        objs.append({"bounds": (l, b, r, t), "size": round(o.get_font_size() * scale, 2), "baseline": m.f,
                     "bold": "bold" in fname.lower() or (weight or 0) >= 600})
    lines: list[dict] = []
    for o in sorted(objs, key=lambda x: (-round(x["baseline"], 0), x["bounds"][0])):
        for ln in lines:
            if abs(ln["baseline"] - o["baseline"]) < max(1.5, o["size"] * 0.3):
                l, b, r, t = ln["bounds"]
                ol, ob, orr, ot = o["bounds"]
                ln["bounds"] = (min(l, ol), min(b, ob), max(r, orr), max(t, ot))
                ln["size"] = max(ln["size"], o["size"])
                ln["bold"] = ln["bold"] and o["bold"]
                break
        else:
            lines.append(dict(o))
    for ln in lines:
        l, b, r, t = ln["bounds"]
        ln["text"] = tp.get_text_bounded(l - 0.5, b - 0.5, r + 0.5, t + 0.5).replace("\r", "").replace("\n", " ").strip()
    del objs, o, tp, pg, doc  # pdfium nesneleri ağaç sırasıyla serbest kalsın
    return [ln for ln in lines if ln["text"]]


def text_blocks(path: str, page: int, password: str | None = None) -> list[dict]:
    """Ardışık, aynı boyutlu ve düzenli aralıklı satırları paragraf olarak birleştirir."""
    lines = sorted(text_lines(path, page, password), key=lambda x: -x["baseline"])
    blocks: list[dict] = []
    for ln in lines:
        if blocks:
            bl = blocks[-1]
            gap = bl["last_baseline"] - ln["baseline"]
            same = abs(bl["size"] - ln["size"]) < 0.6 and bl["bold"] == ln["bold"]
            leading_ok = 0 < gap <= ln["size"] * 1.9 and (bl["leading"] is None or abs(gap - bl["leading"]) < 2)
            aligned = abs(bl["bounds"][0] - ln["bounds"][0]) < ln["size"] * 2.5
            if same and leading_ok and aligned:
                l, b, r, t = bl["bounds"]
                ol, ob, orr, ot = ln["bounds"]
                bl["bounds"] = (min(l, ol), min(b, ob), max(r, orr), max(t, ot))
                bl["text"] += "\n" + ln["text"]
                bl["leading"] = gap
                bl["last_baseline"] = ln["baseline"]
                bl["lines"] += 1
                continue
        blocks.append({**ln, "leading": None, "last_baseline": ln["baseline"], "first_baseline": ln["baseline"],
                       "lines": 1})
    return blocks


def edit_text_block(path: str, out: str, page: int, bounds: tuple, new_text: str, size: float,
                    baseline: float | None = None, leading: float | None = None, bold: bool = False,
                    color: str = "#000000", family: str = "Sans", reflow: bool = True, align: str = "left",
                    password: str | None = None) -> str:
    """Seçilen satır/paragrafın metnini değiştirir. Eski metin nesneleri gerçekten silinir
    (metin katmanından da), yeni metin aynı konuma, gerekirse paragraf genişliğine göre
    yeniden akıtılarak yazılır."""
    from reportlab.lib.styles import ParagraphStyle
    from reportlab.lib.enums import TA_LEFT, TA_CENTER, TA_RIGHT, TA_JUSTIFY
    from reportlab.platypus import Paragraph
    from .security import redact_regions
    l, b, r, t = bounds
    tmp = out + ".tmp.pdf"
    redact_regions(path, tmp, {page: [(l + 0.3, b + 0.3, r - 0.3, t - 0.3)]}, rasterize=False, fill=None,
                   password=password)
    with pikepdf.open(tmp) as pdf:
        x0, y0, _, _ = _box(pdf.pages[page])
    lead = leading or size * 1.2
    base = baseline if baseline is not None else t - size * 0.8
    fn = font_name(bold, family)

    def draw(c, i, n, w, h):
        lx, top = l - x0, base - y0 + size  # paragrafın üst kenarı (ilk satırın yaklaşık üstü)
        if reflow:
            al = {"left": TA_LEFT, "center": TA_CENTER, "right": TA_RIGHT, "justify": TA_JUSTIFY}[align]
            st = ParagraphStyle("e", fontName=fn, fontSize=size, leading=lead, textColor=HexColor(color),
                                alignment=al)
            safe = new_text.replace("&", "&amp;").replace("<", "&lt;").replace("\n", "<br/>")
            para = Paragraph(safe, st)
            width = max(r - l, 20)
            # tek satırsa genişliği serbest bırak (taşmasın diye)
            if "\n" not in new_text and c.stringWidth(new_text, fn, size) > width and (t - b) < size * 1.8:
                width = c.stringWidth(new_text, fn, size) + 2
            _, ph = para.wrap(width, 10000)
            para.drawOn(c, lx, top - ph + (lead - size) * 0.5 - size * 0.05)
        else:
            c.setFont(fn, size)
            c.setFillColor(HexColor(color))
            for k, line in enumerate(new_text.split("\n")):
                c.drawString(lx, base - y0 - k * lead, line)
    overlay(tmp, out, draw, [page], mark="edit", raw=True)
    os.remove(tmp)
    return out


def replace_text_region(path: str, out: str, page: int, rect: tuple, new_text: str, **kw) -> str:
    """Belirtilen bölgedeki satır(lar)ı bulur ve metni değiştirir."""
    lines = [ln for ln in text_lines(path, page) if not (ln["bounds"][2] < rect[0] or ln["bounds"][0] > rect[2]
                                                         or ln["bounds"][3] < rect[1] or ln["bounds"][1] > rect[3])]
    if not lines:
        raise ValueError("Seçilen bölgede düzenlenebilir metin yok")
    ln = lines[0]
    return edit_text_block(path, out, page, ln["bounds"], new_text, ln["size"], ln["baseline"], bold=ln["bold"], **kw)


# ---------------------------------------------------------------- bağlantı, yer imi, meta veri
def add_link(path: str, out: str, page: int, rect: tuple, uri: str | None = None,
             target_page: int | None = None) -> str:
    with pikepdf.open(path, allow_overwriting_input=True) as pdf:
        annot = pikepdf.Dictionary(Type=pikepdf.Name.Annot, Subtype=pikepdf.Name.Link,
                                   Rect=pikepdf.Array(list(rect)), Border=pikepdf.Array([0, 0, 0]))
        if uri:
            annot.A = pikepdf.Dictionary(S=pikepdf.Name.URI, URI=pikepdf.String(uri))
        else:
            annot.Dest = pikepdf.Array([pdf.pages[target_page].obj, pikepdf.Name.Fit])
        p = pdf.pages[page].obj
        if "/Annots" not in p:
            p.Annots = pdf.make_indirect(pikepdf.Array())
        p.Annots.append(pdf.make_indirect(annot))
        pdf.save(out)
    return out


def list_links(path: str) -> list[dict]:
    res = []
    with pikepdf.open(path) as pdf:
        for i, p in enumerate(pdf.pages):
            for a in p.obj.get("/Annots", []):
                if a.get("/Subtype") == "/Link":
                    act = a.get("/A")
                    res.append({"page": i, "rect": [float(v) for v in a.Rect],
                                "uri": str(act.URI) if act is not None and "/URI" in act else None})
    return res


def get_bookmarks(path: str) -> list[dict]:
    out = []
    with pikepdf.open(path) as pdf:
        pages = {p.obj.objgen: i for i, p in enumerate(pdf.pages)}

        def walk(items, level):
            for it in items:
                pg = None
                try:
                    dest = it.destination
                    if dest is None and it.action is not None:
                        dest = it.action.get("/D")
                    if isinstance(dest, pikepdf.Array) and len(dest):
                        pg = pages.get(dest[0].objgen)
                except Exception:
                    pass
                out.append({"title": it.title, "page": pg, "level": level})
                walk(it.children, level + 1)
        with pdf.open_outline() as ol:
            walk(ol.root, 0)
    return out


def set_bookmarks(path: str, out: str, items: list[dict]) -> str:
    """items: [{'title','page','level'}] düz liste; level ile hiyerarşi kurulur."""
    with pikepdf.open(path, allow_overwriting_input=True) as pdf:
        with pdf.open_outline() as ol:
            ol.root.clear()
            stack: list[tuple[int, pikepdf.OutlineItem]] = []
            for it in items:
                node = pikepdf.OutlineItem(it["title"], int(it.get("page") or 0))
                lvl = int(it.get("level", 0))
                while stack and stack[-1][0] >= lvl:
                    stack.pop()
                (stack[-1][1].children if stack else ol.root).append(node)
                stack.append((lvl, node))
        pdf.save(out)
    return out


def auto_bookmarks_from_headings(path: str, out: str, min_size_ratio: float = 1.25) -> str:
    """Gövde metninden belirgin biçimde büyük (veya kalın ve büyük) satırları başlık kabul edip
    yazı tipi boyutuna göre hiyerarşik yer imleri oluşturur."""
    import pypdfium2 as pdfium
    doc = pdfium.PdfDocument(path)
    n = len(doc)
    del doc
    all_lines = []
    for i in range(n):
        for ln in text_lines(path, i):
            all_lines.append((i, ln))
    if not all_lines:
        return set_bookmarks(path, out, [])
    # gövde boyutu: karakter sayısına göre ağırlıklı en yaygın boyut
    weight: dict[float, int] = {}
    for _, ln in all_lines:
        weight[round(ln["size"], 1)] = weight.get(round(ln["size"], 1), 0) + len(ln["text"])
    body = max(weight, key=weight.get)
    heads = [(i, ln) for i, ln in all_lines
             if ln["size"] >= body * min_size_ratio and 2 < len(ln["text"]) < 150]
    sizes = sorted({round(ln["size"]) for _, ln in heads}, reverse=True)[:3]
    items = []
    for i, ln in sorted(heads, key=lambda x: (x[0], -x[1]["baseline"])):
        lvl = sizes.index(round(ln["size"])) if round(ln["size"]) in sizes else len(sizes) - 1
        items.append({"title": ln["text"], "page": i, "level": max(0, lvl)})
    # düzeyleri sıkıştır (ilk öge her zaman 0)
    if items:
        m = min(it["level"] for it in items)
        for it in items:
            it["level"] -= m
    return set_bookmarks(path, out, items)


def get_metadata(path: str) -> dict:
    with pikepdf.open(path) as pdf:
        info = pdf.docinfo
        return {k.lstrip("/"): str(v) for k, v in info.items()}


def set_metadata(path: str, out: str, **fields) -> str:
    """title, author, subject, keywords, creator, producer"""
    keymap = {"title": "/Title", "author": "/Author", "subject": "/Subject", "keywords": "/Keywords",
              "creator": "/Creator", "producer": "/Producer"}
    with pikepdf.open(path, allow_overwriting_input=True) as pdf:
        for k, v in fields.items():
            key = keymap.get(k, "/" + k)
            if v is None or v == "":
                if key in pdf.docinfo:
                    del pdf.docinfo[key]
            else:
                pdf.docinfo[key] = pikepdf.String(v)
        with pdf.open_metadata(set_pikepdf_as_editor=False) as meta:
            meta.load_from_docinfo(pdf.docinfo)
        pdf.save(out)
    return out


# ---------------------------------------------------------------- ekler
def list_attachments(path: str) -> list[dict]:
    with pikepdf.open(path) as pdf:
        return [{"name": n, "size": len(spec.get_file().read_bytes()), "description": spec.description or ""}
                for n, spec in pdf.attachments.items()]


def add_attachment(path: str, out: str, file: str, description: str = "") -> str:
    with pikepdf.open(path, allow_overwriting_input=True) as pdf:
        spec = pikepdf.AttachedFileSpec.from_filepath(pdf, file, description=description)
        pdf.attachments[os.path.basename(file)] = spec
        pdf.save(out)
    return out


def remove_attachment(path: str, out: str, name: str) -> str:
    with pikepdf.open(path, allow_overwriting_input=True) as pdf:
        del pdf.attachments[name]
        pdf.save(out)
    return out


def rename_attachment(path: str, out: str, old: str, new: str) -> str:
    with pikepdf.open(path, allow_overwriting_input=True) as pdf:
        spec = pdf.attachments[old]
        data = spec.get_file().read_bytes()
        desc = spec.description or ""
        del pdf.attachments[old]
        pdf.attachments[new] = pikepdf.AttachedFileSpec(pdf, data, description=desc, filename=new)
        pdf.save(out)
    return out


def extract_attachments(path: str, out_dir: str) -> list[str]:
    os.makedirs(out_dir, exist_ok=True)
    outs = []
    with pikepdf.open(path) as pdf:
        for n, spec in pdf.attachments.items():
            dst = os.path.join(out_dir, os.path.basename(n))
            with open(dst, "wb") as f:
                f.write(spec.get_file().read_bytes())
            outs.append(dst)
    return outs


# ---------------------------------------------------------------- sıkıştırma
def compress(path: str, out: str, level: str = "medium", password: str | None = None) -> tuple[str, int, int]:
    """level: 'low' (kayıpsız), 'medium' (150 dpi, JPEG 75), 'high' (MRC benzeri: 110 dpi, JPEG 55)."""
    from pikepdf import PdfImage
    settings = {"low": None, "medium": (150, 75), "high": (110, 55)}[level]
    before = os.path.getsize(path)
    with pikepdf.open(path, password=password or "", allow_overwriting_input=True) as pdf:
        if settings:
            max_dpi, quality = settings
            seen = set()
            for page in pdf.pages:
                pw = float(page.mediabox[2]) - float(page.mediabox[0])
                for name, raw in list(page.images.items()):
                    if raw.objgen in seen:
                        continue
                    seen.add(raw.objgen)
                    try:
                        pimg = PdfImage(raw)
                        if pimg.bits_per_component == 1 or "/SMask" in raw or "/Mask" in raw:
                            continue
                        img = pimg.as_pil_image()
                        dpi = img.width / (pw / 72.0) if pw else 300
                        if img.mode not in ("RGB", "L"):
                            img = img.convert("RGB")
                        if dpi > max_dpi:
                            ratio = max_dpi / dpi
                            img = img.resize((max(1, int(img.width * ratio)), max(1, int(img.height * ratio))),
                                             Image.LANCZOS)
                        buf = io.BytesIO()
                        img.save(buf, "JPEG", quality=quality, optimize=True)
                        if buf.tell() < len(raw.read_raw_bytes()):
                            raw.write(buf.getvalue(), filter=pikepdf.Name.DCTDecode)
                            raw.Width, raw.Height = img.width, img.height
                            raw.ColorSpace = pikepdf.Name.DeviceRGB if img.mode == "RGB" else pikepdf.Name.DeviceGray
                            raw.BitsPerComponent = 8
                            for k in ("/DecodeParms", "/Decode"):
                                if k in raw:
                                    del raw[k]
                    except Exception:
                        continue
        pdf.remove_unreferenced_resources()
        pdf.save(out, compress_streams=True, object_stream_mode=pikepdf.ObjectStreamMode.generate,
                 recompress_flate=True)
    return out, before, os.path.getsize(out)
