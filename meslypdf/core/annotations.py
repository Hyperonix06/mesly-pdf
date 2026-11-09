"""Yorum ve işaretleme: vurgulama, altını çizme, üstünü çizme, not, metin kutusu, şekiller,
serbest çizim, yanıtlar, durum atama, listeleme / sıralama / filtreleme / silme."""
from __future__ import annotations

import datetime as _dt
import getpass
import io
import uuid

import pikepdf
from reportlab.lib.colors import HexColor
from reportlab.pdfgen import canvas

from .fonts import font_name

KINDS_TR = {
    "Highlight": "Vurgulama", "Underline": "Altı çizili", "StrikeOut": "Üstü çizili",
    "Squiggly": "Dalgalı çizgi", "Text": "Not", "FreeText": "Metin kutusu", "Square": "Dikdörtgen",
    "Circle": "Elips", "Ink": "Serbest çizim", "Line": "Çizgi", "Stamp": "Damga", "Popup": "Açılır",
}
STATES = {"Accepted": "Kabul edildi", "Rejected": "Reddedildi", "Cancelled": "İptal edildi",
          "Completed": "Tamamlandı", "None": "Yok"}


def _pdf_date(dt: _dt.datetime | None = None) -> str:
    dt = dt or _dt.datetime.now()
    return dt.strftime("D:%Y%m%d%H%M%S")


def _parse_date(s: str) -> str:
    try:
        s = str(s)[2:16]
        return _dt.datetime.strptime(s, "%Y%m%d%H%M%S").strftime("%d.%m.%Y %H:%M")
    except Exception:
        return ""


def _rgb(hexcolor: str) -> list[float]:
    c = HexColor(hexcolor)
    return [round(c.red, 3), round(c.green, 3), round(c.blue, 3)]


def default_author() -> str:
    try:
        return getpass.getuser()
    except Exception:
        return "Kullanıcı"


def _form_from_reportlab(pdf: pikepdf.Pdf, w: float, h: float, draw) -> pikepdf.Object:
    buf = io.BytesIO()
    c = canvas.Canvas(buf, pagesize=(max(w, 1), max(h, 1)))
    draw(c)
    c.showPage()
    c.save()
    with pikepdf.open(io.BytesIO(buf.getvalue())) as tmp:
        form = tmp.pages[0].as_form_xobject()
        return pdf.copy_foreign(form)


def _simple_form(pdf, bbox, content: bytes, extgstate: dict | None = None):
    res = pikepdf.Dictionary()
    if extgstate:
        res.ExtGState = pikepdf.Dictionary({"/G0": pikepdf.Dictionary(extgstate)})
    return pdf.make_stream(content, Type=pikepdf.Name.XObject, Subtype=pikepdf.Name.Form,
                           BBox=pikepdf.Array(bbox), Resources=res)


def _append(pdf: pikepdf.Pdf, page_index: int, annot: pikepdf.Dictionary) -> pikepdf.Object:
    p = pdf.pages[page_index].obj
    if "/Annots" not in p:
        p.Annots = pdf.make_indirect(pikepdf.Array())
    ind = pdf.make_indirect(annot)
    ind.P = p
    p.Annots.append(ind)
    return ind


def _base(kind: str, rect, color: str, author: str, contents: str, opacity: float = 1.0):
    now = _pdf_date()
    return pikepdf.Dictionary(
        Type=pikepdf.Name.Annot, Subtype=pikepdf.Name("/" + kind), Rect=pikepdf.Array([float(v) for v in rect]),
        C=pikepdf.Array(_rgb(color)), T=pikepdf.String(author or default_author()),
        Contents=pikepdf.String(contents or ""), M=pikepdf.String(now), CreationDate=pikepdf.String(now),
        NM=pikepdf.String(str(uuid.uuid4())), F=4, CA=opacity)


def _union(rects):
    return [min(r[0] for r in rects), min(r[1] for r in rects), max(r[2] for r in rects), max(r[3] for r in rects)]


def add_markup(path: str, out: str, page: int, rects: list[tuple], kind: str = "Highlight",
               color: str = "#FFEB3B", author: str = "", contents: str = "") -> str:
    """Metin işaretleme. rects: PDF koordinatlarında (l,b,r,t) liste."""
    with pikepdf.open(path, allow_overwriting_input=True) as pdf:
        u = _union(rects)
        a = _base(kind, u, color, author, contents)
        quads = []
        for l, b, r, t in rects:
            quads += [l, t, r, t, l, b, r, b]
        a.QuadPoints = pikepdf.Array(quads)
        rgb = " ".join(str(v) for v in _rgb(color))
        ops = []
        for l, b, r, t in rects:
            l, b, r, t = l - u[0], b - u[1], r - u[0], t - u[1]
            if kind == "Highlight":
                ops.append(f"{rgb} rg {l:.2f} {b:.2f} {r - l:.2f} {t - b:.2f} re f")
            elif kind == "Underline":
                ops.append(f"{rgb} RG 1 w {l:.2f} {b + 0.8:.2f} m {r:.2f} {b + 0.8:.2f} l S")
            elif kind == "StrikeOut":
                y = (b + t) / 2
                ops.append(f"{rgb} RG 1 w {l:.2f} {y:.2f} m {r:.2f} {y:.2f} l S")
            elif kind == "Squiggly":
                pts, x, up = [], l, True
                while x <= r:
                    pts.append(f"{x:.2f} {b + (2 if up else 0):.2f} l")
                    x += 2
                    up = not up
                ops.append(f"{rgb} RG 0.8 w {l:.2f} {b:.2f} m " + " ".join(pts) + " S")
        bbox = [0, 0, u[2] - u[0], u[3] - u[1]]
        gs = {"/BM": pikepdf.Name.Multiply, "/ca": 1, "/CA": 1} if kind == "Highlight" else None
        content = ("/G0 gs " if gs else "") + "\n".join(ops)
        form = _simple_form(pdf, bbox, content.encode(), gs)
        form.Matrix = pikepdf.Array([1, 0, 0, 1, 0, 0])
        a.AP = pikepdf.Dictionary(N=form)
        _append(pdf, page, a)
        pdf.save(out)
    return out


def add_note(path: str, out: str, page: int, x: float, y: float, contents: str,
             color: str = "#FFC107", author: str = "") -> str:
    with pikepdf.open(path, allow_overwriting_input=True) as pdf:
        a = _base("Text", [x, y - 20, x + 20, y], color, author, contents)
        a.Name = pikepdf.Name.Comment
        a.Open = False

        def draw(c):
            c.setFillColor(HexColor(color))
            c.setStrokeColor(HexColor("#5D4037"))
            c.roundRect(1, 3, 18, 16, 2, fill=1)
            c.setFillColor(HexColor(color))
            p = c.beginPath()
            p.moveTo(5, 4); p.lineTo(4, 0); p.lineTo(9, 4)
            c.drawPath(p, fill=1)
            c.setStrokeColor(HexColor("#5D4037"))
            for yy in (14, 11, 8):
                c.line(4, yy, 16, yy)
        a.AP = pikepdf.Dictionary(N=_form_from_reportlab(pdf, 20, 20, draw))
        _append(pdf, page, a)
        pdf.save(out)
    return out


def add_freetext(path: str, out: str, page: int, rect: tuple, text: str, size: float = 11,
                 color: str = "#000000", border: str = "#1565C0", fill: str | None = "#FFFFFF",
                 author: str = "") -> str:
    from reportlab.lib.styles import ParagraphStyle
    from reportlab.platypus import Paragraph
    l, b, r, t = rect
    w, h = r - l, t - b
    with pikepdf.open(path, allow_overwriting_input=True) as pdf:
        a = _base("FreeText", rect, border, author, text)
        a.DA = pikepdf.String(f"/Helv {size} Tf {' '.join(map(str, _rgb(color)))} rg")

        def draw(c):
            if fill:
                c.setFillColor(HexColor(fill))
            c.setStrokeColor(HexColor(border))
            c.rect(0.5, 0.5, w - 1, h - 1, fill=1 if fill else 0, stroke=1)
            st = ParagraphStyle("f", fontName=font_name(), fontSize=size, leading=size * 1.25,
                                textColor=HexColor(color))
            para = Paragraph(text.replace("&", "&amp;").replace("<", "&lt;").replace("\n", "<br/>"), st)
            _, ph = para.wrap(w - 8, h)
            para.drawOn(c, 4, h - 4 - ph)
        a.AP = pikepdf.Dictionary(N=_form_from_reportlab(pdf, w, h, draw))
        _append(pdf, page, a)
        pdf.save(out)
    return out


def add_shape(path: str, out: str, page: int, rect: tuple, kind: str = "Square", color: str = "#E53935",
              width: float = 1.5, contents: str = "", author: str = "", fill: str | None = None) -> str:
    """kind: Square, Circle, Line (rect = x1,y1,x2,y2)."""
    with pikepdf.open(path, allow_overwriting_input=True) as pdf:
        if kind == "Line":
            x1, y1, x2, y2 = rect
            r = [min(x1, x2) - width * 4, min(y1, y2) - width * 4, max(x1, x2) + width * 4, max(y1, y2) + width * 4]
            a = _base("Line", r, color, author, contents)
            a.L = pikepdf.Array([x1, y1, x2, y2])
            a.LE = pikepdf.Array([pikepdf.Name.None_, pikepdf.Name.OpenArrow])
            rgb = " ".join(map(str, _rgb(color)))
            import math
            ang = math.atan2(y2 - y1, x2 - x1)
            ax1 = x2 - 9 * math.cos(ang - 0.45); ay1 = y2 - 9 * math.sin(ang - 0.45)
            ax2 = x2 - 9 * math.cos(ang + 0.45); ay2 = y2 - 9 * math.sin(ang + 0.45)
            ox, oy = r[0], r[1]
            cs = (f"{rgb} RG {width} w 1 J {x1-ox:.2f} {y1-oy:.2f} m {x2-ox:.2f} {y2-oy:.2f} l S "
                  f"{ax1-ox:.2f} {ay1-oy:.2f} m {x2-ox:.2f} {y2-oy:.2f} l {ax2-ox:.2f} {ay2-oy:.2f} l S")
            a.AP = pikepdf.Dictionary(N=_simple_form(pdf, [0, 0, r[2] - r[0], r[3] - r[1]], cs.encode()))
        else:
            a = _base(kind, rect, color, author, contents)
            a.BS = pikepdf.Dictionary(W=width, S=pikepdf.Name.S)
            if fill:
                a.IC = pikepdf.Array(_rgb(fill))
            l, b, r_, t = rect
            w, h = r_ - l, t - b

            def draw(c):
                c.setStrokeColor(HexColor(color))
                c.setLineWidth(width)
                if fill:
                    c.setFillColor(HexColor(fill))
                hw = width / 2
                if kind == "Circle":
                    c.ellipse(hw, hw, w - hw, h - hw, stroke=1, fill=1 if fill else 0)
                else:
                    c.rect(hw, hw, w - width, h - width, stroke=1, fill=1 if fill else 0)
            a.AP = pikepdf.Dictionary(N=_form_from_reportlab(pdf, w, h, draw))
        _append(pdf, page, a)
        pdf.save(out)
    return out


def add_ink(path: str, out: str, page: int, strokes: list[list[tuple]], color: str = "#1E88E5",
            width: float = 2, author: str = "", contents: str = "") -> str:
    pts = [p for s in strokes for p in s]
    r = [min(p[0] for p in pts) - width, min(p[1] for p in pts) - width,
         max(p[0] for p in pts) + width, max(p[1] for p in pts) + width]
    with pikepdf.open(path, allow_overwriting_input=True) as pdf:
        a = _base("Ink", r, color, author, contents)
        a.InkList = pikepdf.Array([pikepdf.Array([v for p in s for v in p]) for s in strokes])
        a.BS = pikepdf.Dictionary(W=width)
        rgb = " ".join(map(str, _rgb(color)))
        ops = [f"{rgb} RG {width} w 1 J 1 j"]
        for s in strokes:
            ops.append(f"{s[0][0]-r[0]:.2f} {s[0][1]-r[1]:.2f} m " +
                       " ".join(f"{x-r[0]:.2f} {y-r[1]:.2f} l" for x, y in s[1:]) + " S")
        a.AP = pikepdf.Dictionary(N=_simple_form(pdf, [0, 0, r[2] - r[0], r[3] - r[1]], "\n".join(ops).encode()))
        _append(pdf, page, a)
        pdf.save(out)
    return out


# ------------------------------------------------------------------ okuma / yönetim
def list_annotations(path: str, password: str | None = None) -> list[dict]:
    items = []
    with pikepdf.open(path, password=password or "") as pdf:
        by_id = {}
        for pi, p in enumerate(pdf.pages):
            for a in p.obj.get("/Annots", []):
                st = str(a.get("/Subtype", "")).lstrip("/")
                if st in ("Link", "Widget", "Popup"):
                    continue
                d = {
                    "id": str(a.get("/NM", "")) or f"{pi}-{a.objgen[0]}",
                    "objgen": a.objgen, "page": pi, "kind": st, "kind_tr": KINDS_TR.get(st, st),
                    "author": str(a.get("/T", "")), "contents": str(a.get("/Contents", "")),
                    "date": _parse_date(a.get("/M", a.get("/CreationDate", ""))),
                    "raw_date": str(a.get("/M", "")), "rect": [float(v) for v in a.get("/Rect", [0, 0, 0, 0])],
                    "color": a.get("/C"), "reply_to": None, "state": None, "replies": [],
                }
                if "/IRT" in a:
                    d["reply_to"] = a.IRT.objgen
                if "/State" in a:
                    d["state"] = str(a.State)
                items.append(d)
                by_id[a.objgen] = d
        # yanıtları ve durumları ana yoruma bağla
        top = []
        for d in items:
            if d["reply_to"] and d["reply_to"] in by_id:
                parent = by_id[d["reply_to"]]
                if d["state"]:
                    parent["status"] = d["state"]
                    parent["status_tr"] = STATES.get(d["state"], d["state"])
                else:
                    parent["replies"].append(d)
            else:
                top.append(d)
    for d in top:
        d.setdefault("status", None)
        d.setdefault("status_tr", "")
        d["color"] = None
    return top


def filter_sort(items: list[dict], author: str | None = None, kind: str | None = None,
                status: str | None = None, text: str | None = None, sort: str = "page") -> list[dict]:
    res = [d for d in items
           if (not author or d["author"] == author) and (not kind or d["kind"] == kind)
           and (not status or (d.get("status") or "None") == status)
           and (not text or text.lower() in d["contents"].lower())]
    keyf = {"page": lambda d: (d["page"], -d["rect"][3]), "author": lambda d: d["author"].lower(),
            "date": lambda d: d["raw_date"], "kind": lambda d: d["kind"]}[sort]
    return sorted(res, key=keyf)


def _find(pdf, objgen):
    for pi, p in enumerate(pdf.pages):
        for a in p.obj.get("/Annots", []):
            if a.objgen == tuple(objgen):
                return pi, a
    raise KeyError("Yorum bulunamadı")


def reply(path: str, out: str, objgen, text: str, author: str = "") -> str:
    with pikepdf.open(path, allow_overwriting_input=True) as pdf:
        pi, parent = _find(pdf, objgen)
        r = parent.Rect
        a = _base("Text", [float(r[0]), float(r[1]), float(r[0]) + 20, float(r[1]) + 20], "#FFC107", author, text)
        a.IRT = parent
        a.RT = pikepdf.Name.R
        a.F = 32 | 2  # gizli, yazdırılmaz -> sadece panelde
        _append(pdf, pi, a)
        pdf.save(out)
    return out


def set_status(path: str, out: str, objgen, state: str, author: str = "") -> str:
    """state: Accepted, Rejected, Cancelled, Completed, None"""
    with pikepdf.open(path, allow_overwriting_input=True) as pdf:
        pi, parent = _find(pdf, objgen)
        r = parent.Rect
        a = _base("Text", [float(r[0]), float(r[1]), float(r[0]) + 20, float(r[1]) + 20], "#FFC107", author,
                  f"{author or default_author()} durumu '{STATES.get(state, state)}' olarak ayarladı")
        a.IRT = parent
        a.State = pikepdf.String(state)
        a.StateModel = pikepdf.String("Review")
        a.F = 32 | 2
        _append(pdf, pi, a)
        pdf.save(out)
    return out


def edit_contents(path: str, out: str, objgen, text: str) -> str:
    with pikepdf.open(path, allow_overwriting_input=True) as pdf:
        _, a = _find(pdf, objgen)
        a.Contents = pikepdf.String(text)
        a.M = pikepdf.String(_pdf_date())
        pdf.save(out)
    return out


def delete_annotation(path: str, out: str, objgen) -> str:
    with pikepdf.open(path, allow_overwriting_input=True) as pdf:
        target = tuple(objgen)
        for p in pdf.pages:
            ann = p.obj.get("/Annots")
            if ann is None:
                continue
            keep = pikepdf.Array([a for a in ann if a.objgen != target and
                                  not ("/IRT" in a and a.IRT.objgen == target)])
            p.obj.Annots = keep
        pdf.save(out)
    return out


def flatten_annotations(path: str, out: str) -> str:
    """Yorumları sayfa içeriğine kalıcı olarak gömer."""
    with pikepdf.open(path, allow_overwriting_input=True) as pdf:
        pdf.flatten_annotations(mode="all")
        pdf.save(out)
    return out


def export_comments_text(path: str) -> str:
    lines = []
    for d in list_annotations(path):
        lines.append(f"Sayfa {d['page'] + 1} · {d['kind_tr']} · {d['author']} · {d['date']}"
                     + (f" · [{d['status_tr']}]" if d.get('status_tr') else ""))
        if d["contents"]:
            lines.append("    " + d["contents"])
        for r in d["replies"]:
            lines.append(f"    ↳ {r['author']}: {r['contents']}")
    return "\n".join(lines)
