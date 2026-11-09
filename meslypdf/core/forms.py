"""Formlar: alanları listeleme, doldurma, FDF içe/dışa aktarma ve doldurulabilir form oluşturma."""
from __future__ import annotations

import io
import json

import pikepdf

FIELD_TYPES_TR = {"Tx": "Metin", "Btn": "Düğme/Onay", "Ch": "Seçim", "Sig": "İmza"}

FF_READONLY = 1
FF_REQUIRED = 1 << 1
FF_MULTILINE = 1 << 12
FF_PASSWORD = 1 << 13
FF_NO_TOGGLE_OFF = 1 << 14
FF_RADIO = 1 << 15
FF_PUSHBUTTON = 1 << 16
FF_COMBO = 1 << 17
FF_EDIT = 1 << 18


def _full_name(f) -> str:
    parts = []
    node = f
    while node is not None:
        if "/T" in node:
            parts.append(str(node.T))
        node = node.get("/Parent")
    return ".".join(reversed(parts))


def _inherited(f, key):
    node = f
    while node is not None:
        if key in node:
            return node[key]
        node = node.get("/Parent")
    return None


def list_fields(path: str, password: str | None = None) -> list[dict]:
    out = []
    with pikepdf.open(path, password=password or "") as pdf:
        pagemap = {p.obj.objgen: i for i, p in enumerate(pdf.pages)}
        acro = pdf.Root.get("/AcroForm")
        if acro is None:
            return out
        seen = set()

        def walk(field):
            kids = field.get("/Kids")
            is_terminal = kids is None or all("/T" not in k for k in kids)
            if not is_terminal:
                for k in kids:
                    walk(k)
                return
            name = _full_name(field)
            if name in seen:
                return
            seen.add(name)
            ft = str(_inherited(field, "/FT") or "").lstrip("/")
            ff = int(_inherited(field, "/Ff") or 0)
            v = _inherited(field, "/V")
            widgets = list(kids) if kids is not None else [field]
            w0 = widgets[0]
            page = pagemap.get(w0.P.objgen) if "/P" in w0 else None
            if page is None:
                for i, p in enumerate(pdf.pages):
                    if any(a.objgen == w0.objgen for a in p.obj.get("/Annots", [])):
                        page = i
                        break
            kind = ft
            states = []
            if ft == "Btn":
                kind = "radio" if ff & FF_RADIO else ("button" if ff & FF_PUSHBUTTON else "checkbox")
                for w in (widgets if kind != "button" else []):
                    ap = w.get("/AP")
                    if ap is not None and "/N" in ap:
                        states += [str(k).lstrip("/") for k in ap.N.keys() if str(k) != "/Off"]
            elif ft == "Ch":
                kind = "combo" if ff & FF_COMBO else "list"
            elif ft == "Tx":
                kind = "text"
            elif ft == "Sig":
                kind = "signature"
            opts = []
            o = _inherited(field, "/Opt")
            if o is not None:
                for it in o:
                    opts.append(str(it[1]) if isinstance(it, pikepdf.Array) else str(it))
            val = str(v).lstrip("/") if v is not None and not isinstance(v, pikepdf.Array) else (
                [str(x) for x in v] if v is not None else "")
            out.append({"name": name, "type": kind, "type_tr": FIELD_TYPES_TR.get(ft, ft), "value": val,
                        "options": opts or sorted(set(states)), "page": page,
                        "rect": [float(x) for x in w0.get("/Rect", [0, 0, 0, 0])],
                        "required": bool(ff & FF_REQUIRED), "readonly": bool(ff & FF_READONLY),
                        "tooltip": str(field.get("/TU", ""))})
        for f in acro.get("/Fields", []):
            walk(f)
    return out


def fill(path: str, out: str, values: dict, flatten: bool = False, password: str | None = None) -> str:
    from pikepdf.form import Form
    with pikepdf.open(path, password=password or "", allow_overwriting_input=True) as pdf:
        form = Form(pdf, None)  # görünümleri kendimiz üretiyoruz
        for name, value in values.items():
            try:
                field = form[name]
            except KeyError:
                continue
            if field.is_checkbox:
                field.checked = bool(value) and str(value).lower() not in ("off", "false", "0", "hayır", "")
            elif field.is_radio_button:
                for opt in field.options:
                    if str(opt.on_value).lstrip("/") == str(value):
                        opt.select()
            elif field.is_choice:
                field.value = str(value)
            elif field.is_text:
                field.value = str(value)
        refresh_appearances(pdf)
        if flatten:
            pdf.flatten_annotations(mode="all")
            if "/AcroForm" in pdf.Root:
                del pdf.Root.AcroForm
        pdf.save(out)
    return out


def _da_size(field) -> float:
    da = str(_inherited(field, "/DA") or "")
    import re
    m = re.search(r"([\d.]+)\s+Tf", da)
    return float(m.group(1)) if m else 0.0


def refresh_appearances(pdf: pikepdf.Pdf):
    """Metin, seçim ve düğme alanları için Türkçe karakter destekli görünüm akışları üretir."""
    from reportlab.lib.colors import HexColor
    from .annotations import _form_from_reportlab
    from .fonts import font_name
    acro = pdf.Root.get("/AcroForm")
    if acro is None:
        return
    for p in pdf.pages:
        for w in p.obj.get("/Annots", []):
            if w.get("/Subtype") != "/Widget":
                continue
            field = w if "/T" in w else w.get("/Parent", w)
            ft = str(_inherited(field, "/FT") or "")
            ff = int(_inherited(field, "/Ff") or 0)
            if ft not in ("/Tx", "/Ch") and not (ft == "/Btn" and ff & FF_PUSHBUTTON):
                continue
            l, b, r, t = [float(x) for x in w.Rect]
            wd, ht = abs(r - l), abs(t - b)
            v = _inherited(field, "/V")
            val = "" if v is None else (", ".join(str(x) for x in v) if isinstance(v, pikepdf.Array) else str(v))
            fs = _da_size(field)
            fn = font_name()
            mk = w.get("/MK", {})
            bg = mk.get("/BG") if hasattr(mk, "get") else None

            def draw(c, ft=ft, ff=ff, val=val, fs=fs, wd=wd, ht=ht, field=field, bg=bg, mk=mk):
                if bg is not None and len(bg) == 3:
                    c.setFillColorRGB(*[float(x) for x in bg])
                    c.rect(0, 0, wd, ht, stroke=0, fill=1)
                c.setStrokeColor(HexColor("#9E9E9E"))
                c.setLineWidth(1)
                c.rect(0.5, 0.5, wd - 1, ht - 1, stroke=1, fill=0)
                c.setFillColor(HexColor("#000000"))
                if ft == "/Btn":
                    label = str(mk.get("/CA", "")) if hasattr(mk, "get") else ""
                    size = fs or 10
                    c.setFont(fn, size)
                    c.drawCentredString(wd / 2, ht / 2 - size / 3, label)
                    return
                if ft == "/Ch" and not (ff & FF_COMBO):
                    size = fs or 10
                    c.setFont(fn, size)
                    opts = [str(o[1]) if isinstance(o, pikepdf.Array) else str(o) for o in _inherited(field, "/Opt") or []]
                    y = ht - size - 2
                    for o in opts:
                        if o == val:
                            c.setFillColor(HexColor("#1565C0"))
                            c.rect(1, y - 2, wd - 2, size + 3, stroke=0, fill=1)
                            c.setFillColor(HexColor("#FFFFFF"))
                        else:
                            c.setFillColor(HexColor("#000000"))
                        c.drawString(3, y, o)
                        y -= size + 3
                    return
                multiline = bool(ff & FF_MULTILINE)
                text = ("•" * len(val)) if ff & FF_PASSWORD else val
                if ft == "/Ch":
                    c.setFillColor(HexColor("#E0E0E0"))
                    c.rect(wd - ht + 1, 1, ht - 2, ht - 2, stroke=0, fill=1)
                    c.setFillColor(HexColor("#424242"))
                    pth = c.beginPath()
                    pth.moveTo(wd - ht * 0.7, ht * 0.6); pth.lineTo(wd - ht * 0.3, ht * 0.6); pth.lineTo(wd - ht / 2, ht * 0.35)
                    c.drawPath(pth, fill=1, stroke=0)
                    c.setFillColor(HexColor("#000000"))
                size = fs or (min(12, ht * 0.65) if not multiline else 10)
                if not fs and not multiline:
                    while size > 4 and c.stringWidth(text, fn, size) > wd - 6:
                        size -= 0.5
                c.setFont(fn, size)
                if multiline:
                    from reportlab.lib.styles import ParagraphStyle
                    from reportlab.platypus import Paragraph
                    para = Paragraph(text.replace("&", "&amp;").replace("<", "&lt;").replace("\n", "<br/>"),
                                     ParagraphStyle("m", fontName=fn, fontSize=size, leading=size * 1.2))
                    _, ph = para.wrap(wd - 6, ht)
                    para.drawOn(c, 3, ht - 3 - ph)
                else:
                    c.drawString(3, (ht - size) / 2 + size * 0.22, text)
            w.AP = pikepdf.Dictionary(N=_form_from_reportlab(pdf, wd, ht, draw))
    acro.NeedAppearances = False


def validate_required(path: str) -> list[str]:
    """Doldurulmamış zorunlu alanların adlarını döndürür."""
    return [f["name"] for f in list_fields(path) if f["required"] and f["value"] in ("", "Off", None, [])]


# ------------------------------------------------------------------ FDF / JSON
def _fdf_str(v: str) -> str:
    return "<FEFF" + str(v).encode("utf-16-be").hex().upper() + ">"


def _fdf_name(v: str) -> str:
    return "/" + "".join(ch if (ch.isascii() and ch.isalnum()) else "".join(f"#{b:02X}" for b in ch.encode("utf-8"))
                         for ch in str(v))


def export_fdf(path: str, fdf_out: str) -> str:
    import os
    parts = []
    for f in list_fields(path):
        if f["type"] in ("button", "signature"):
            continue
        v = f["value"]
        if f["type"] in ("checkbox", "radio"):
            val = _fdf_name(v or "Off")
        elif isinstance(v, list):
            val = "[" + " ".join(_fdf_str(x) for x in v) + "]"
        else:
            val = _fdf_str(v or "")
        parts.append(f"<< /T {_fdf_str(f['name'])} /V {val} >>")
    body = ("%FDF-1.2\n%\xe2\xe3\xcf\xd3\n1 0 obj\n<< /FDF << /F " + _fdf_str(os.path.basename(path)) +
            " /Fields [\n" + "\n".join(parts) + "\n] >> >>\nendobj\ntrailer\n<< /Root 1 0 R >>\n%%EOF\n")
    with open(fdf_out, "wb") as fh:
        fh.write(body.encode("latin-1"))
    return fdf_out


def read_fdf(fdf_path: str) -> dict:
    """FDF dosyasındaki alan değerlerini okur (dolaylı nesneler dahil)."""
    import re
    raw = open(fdf_path, "rb").read()
    objs = {}
    for m in re.finditer(rb"(\d+)\s+(\d+)\s+obj(.*?)endobj", raw, re.S):
        objs[int(m.group(1))] = m.group(3).strip()
    if not objs:
        raise ValueError("Geçerli bir FDF dosyası değil")
    tmp = pikepdf.new()

    def resolve(o):
        return o

    parsed = {}
    for num, body in objs.items():
        # dolaylı başvuruları (n 0 R) çözmek için önce yer tutucu nesneler oluştur
        parsed[num] = body
    # dolaylı başvuruları metin düzeyinde yerine koy (basit FDF'ler için yeterli)
    def inline(body, depth=0):
        if depth > 10:
            return body
        return re.sub(rb"(\d+)\s+0\s+R", lambda m: inline(objs.get(int(m.group(1)), b"null"), depth + 1), body)
    root_num = 1
    m = re.search(rb"/Root\s+(\d+)\s+0\s+R", raw)
    if m:
        root_num = int(m.group(1))
    root = pikepdf.Object.parse(inline(objs[root_num]))
    fdf = root.get("/FDF", root)
    vals = {}

    def walk(fields, prefix=""):
        for f in fields:
            name = prefix + str(f.get("/T", ""))
            if "/Kids" in f:
                walk(f.Kids, name + ".")
            elif "/V" in f:
                v = f.V
                vals[name] = str(v).lstrip("/") if not isinstance(v, pikepdf.Array) else [str(x) for x in v]
    walk(fdf.get("/Fields", []))
    return vals


def import_fdf(path: str, fdf_path: str, out: str) -> str:
    return fill(path, out, read_fdf(fdf_path))


def export_json(path: str, json_out: str) -> str:
    data = {f["name"]: f["value"] for f in list_fields(path) if f["type"] not in ("button", "signature")}
    with open(json_out, "w", encoding="utf-8") as fh:
        json.dump(data, fh, ensure_ascii=False, indent=2)
    return json_out


# ------------------------------------------------------------------ form oluşturma
def _ensure_acroform(pdf: pikepdf.Pdf):
    if "/AcroForm" not in pdf.Root:
        pdf.Root.AcroForm = pdf.make_indirect(pikepdf.Dictionary(Fields=pikepdf.Array()))
    af = pdf.Root.AcroForm
    if "/Fields" not in af:
        af.Fields = pikepdf.Array()
    if "/DR" not in af:
        helv = pdf.make_indirect(pikepdf.Dictionary(Type=pikepdf.Name.Font, Subtype=pikepdf.Name.Type1,
                                                    BaseFont=pikepdf.Name.Helvetica,
                                                    Encoding=pikepdf.Name.WinAnsiEncoding))
        zadb = pdf.make_indirect(pikepdf.Dictionary(Type=pikepdf.Name.Font, Subtype=pikepdf.Name.Type1,
                                                    BaseFont=pikepdf.Name.ZapfDingbats))
        af.DR = pikepdf.Dictionary(Font=pikepdf.Dictionary(Helv=helv, ZaDb=zadb))
    af.DA = pikepdf.String("/Helv 0 Tf 0 g")
    return af


def _mk(pdf, w, h, content: str, font_zadb=False):
    res = pikepdf.Dictionary()
    if font_zadb:
        res.Font = pikepdf.Dictionary(ZaDb=pdf.Root.AcroForm.DR.Font.ZaDb)
    return pdf.make_stream(content.encode(), Type=pikepdf.Name.XObject, Subtype=pikepdf.Name.Form,
                           BBox=pikepdf.Array([0, 0, w, h]), Resources=res)


def _border_ops(w, h):
    return f"0.6 0.6 0.6 RG 1 w 0.5 0.5 {w - 1} {h - 1} re S "


def add_field(path: str, out: str, page: int, kind: str, name: str, rect: tuple,
              options: list[str] | None = None, value: str = "", required: bool = False,
              readonly: bool = False, multiline: bool = False, password: bool = False,
              tooltip: str = "", max_len: int | None = None, font_size: float = 0,
              action: dict | None = None, export_value: str = "Evet") -> str:
    """kind: text, checkbox, radio, combo, list, button, signature
    action (yalnızca düğme): {'type': 'url', 'value': 'https://...'} | {'type': 'reset'} |
    {'type': 'print'} | {'type': 'js', 'value': 'app.alert(...)'} | {'type': 'submit', 'value': url}"""
    l, b, r, t = rect
    w, h = r - l, t - b
    with pikepdf.open(path, allow_overwriting_input=True) as pdf:
        af = _ensure_acroform(pdf)
        pobj = pdf.pages[page].obj
        if "/Annots" not in pobj:
            pobj.Annots = pdf.make_indirect(pikepdf.Array())
        ff = (FF_REQUIRED if required else 0) | (FF_READONLY if readonly else 0)
        base = dict(Type=pikepdf.Name.Annot, Subtype=pikepdf.Name.Widget, Rect=pikepdf.Array([l, b, r, t]),
                    F=4, P=pobj, T=pikepdf.String(name),
                    MK=pikepdf.Dictionary(BC=pikepdf.Array([0.6, 0.6, 0.6]), BG=pikepdf.Array([1, 1, 1])))
        if tooltip:
            base["TU"] = pikepdf.String(tooltip)
        base = {k: v for k, v in base.items()}
        widgets = []
        if kind == "text":
            ff |= (FF_MULTILINE if multiline else 0) | (FF_PASSWORD if password else 0)
            d = pikepdf.Dictionary(**base, FT=pikepdf.Name.Tx, Ff=ff, DA=pikepdf.String(f"/Helv {font_size} Tf 0 g"),
                                   V=pikepdf.String(value))
            if max_len:
                d.MaxLen = max_len
            d.AP = pikepdf.Dictionary(N=_mk(pdf, w, h, _border_ops(w, h)))
            widgets.append(d)
            field = d
        elif kind == "checkbox":
            ev = pikepdf.Name("/" + export_value)
            on = _mk(pdf, w, h, _border_ops(w, h) + f"0 g BT /ZaDb {min(w, h) * 0.8:.1f} Tf {w * 0.15:.1f} {h * 0.2:.1f} Td (4) Tj ET", True)
            off = _mk(pdf, w, h, _border_ops(w, h))
            checked = bool(value) and str(value).lower() not in ("off", "false", "0")
            d = pikepdf.Dictionary(**base, FT=pikepdf.Name.Btn, Ff=ff, V=ev if checked else pikepdf.Name.Off,
                                   AS=ev if checked else pikepdf.Name.Off, DA=pikepdf.String("/ZaDb 0 Tf 0 g"))
            d.AP = pikepdf.Dictionary(N=pikepdf.Dictionary({str(ev): on, "/Off": off}))
            d.MK.CA = pikepdf.String("4")
            widgets.append(d)
            field = d
        elif kind == "radio":
            opts = options or ["Seçenek 1", "Seçenek 2"]
            parent = pdf.make_indirect(pikepdf.Dictionary(FT=pikepdf.Name.Btn, T=pikepdf.String(name),
                                                          Ff=ff | FF_RADIO | FF_NO_TOGGLE_OFF,
                                                          V=pikepdf.Name("/" + value) if value else pikepdf.Name.Off,
                                                          Kids=pikepdf.Array()))
            gap = 4
            size = min(h, (w - gap * (len(opts) - 1)) / len(opts)) if w > h else min(w, (h - gap * (len(opts) - 1)) / len(opts))
            for k, opt in enumerate(opts):
                if w > h:
                    rr = [l + k * (size + gap), b, l + k * (size + gap) + size, b + size]
                else:
                    rr = [l, t - (k + 1) * size - k * gap, l + size, t - k * size - k * gap]
                s = size
                ring = f"0.6 0.6 0.6 RG 1 w {_circle(s/2, s/2, s/2 - 1)} S "
                dot = f"0 g {_circle(s/2, s/2, s/4)} f"
                ev = pikepdf.Name("/" + opt)
                kid = pikepdf.Dictionary(Type=pikepdf.Name.Annot, Subtype=pikepdf.Name.Widget,
                                         Rect=pikepdf.Array(rr), F=4, P=pobj, Parent=parent,
                                         AS=ev if value == opt else pikepdf.Name.Off,
                                         MK=pikepdf.Dictionary(BC=pikepdf.Array([0.6, 0.6, 0.6])))
                kid.AP = pikepdf.Dictionary(N=pikepdf.Dictionary({str(ev): _mk(pdf, s, s, ring + dot),
                                                                   "/Off": _mk(pdf, s, s, ring)}))
                kid = pdf.make_indirect(kid)
                parent.Kids.append(kid)
                pobj.Annots.append(kid)
            af.Fields.append(parent)
            pdf.save(out)
            return out
        elif kind in ("combo", "list"):
            if kind == "combo":
                ff |= FF_COMBO | FF_EDIT
            d = pikepdf.Dictionary(**base, FT=pikepdf.Name.Ch, Ff=ff, DA=pikepdf.String(f"/Helv {font_size} Tf 0 g"),
                                   Opt=pikepdf.Array([pikepdf.String(o) for o in (options or [])]),
                                   V=pikepdf.String(value or (options[0] if options else "")))
            d.AP = pikepdf.Dictionary(N=_mk(pdf, w, h, _border_ops(w, h)))
            widgets.append(d)
            field = d
        elif kind == "button":
            d = pikepdf.Dictionary(**base, FT=pikepdf.Name.Btn, Ff=ff | FF_PUSHBUTTON,
                                   DA=pikepdf.String("/Helv 10 Tf 0 g"))
            d.MK.BG = pikepdf.Array([0.82, 0.88, 0.97])
            d.MK.CA = pikepdf.String(value or name)
            label = (value or name).encode("latin-1", "replace").decode("latin-1")
            d.AP = pikepdf.Dictionary(N=_mk(pdf, w, h, f"0.82 0.88 0.97 rg 0 0 {w} {h} re f " + _border_ops(w, h) +
                                            f"0 g BT /Helv 10 Tf {max(4, w/2 - len(label)*2.6):.1f} {h/2 - 3.5:.1f} Td ({label}) Tj ET"))
            d.AP.N.Resources.Font = pikepdf.Dictionary(Helv=af.DR.Font.Helv)
            if action:
                d.A = _action(action)
            widgets.append(d)
            field = d
        elif kind == "signature":
            d = pikepdf.Dictionary(**base, FT=pikepdf.Name.Sig, Ff=ff)
            d.AP = pikepdf.Dictionary(N=_mk(pdf, w, h, _border_ops(w, h)))
            widgets.append(d)
            field = d
        else:
            raise ValueError(f"Bilinmeyen alan türü: {kind}")
        ind = pdf.make_indirect(field)
        pobj.Annots.append(ind)
        af.Fields.append(ind)
        refresh_appearances(pdf)
        pdf.save(out)
    return out


def _circle(cx, cy, r):
    k = 0.5523 * r
    return (f"{cx + r:.2f} {cy:.2f} m {cx + r:.2f} {cy + k:.2f} {cx + k:.2f} {cy + r:.2f} {cx:.2f} {cy + r:.2f} c "
            f"{cx - k:.2f} {cy + r:.2f} {cx - r:.2f} {cy + k:.2f} {cx - r:.2f} {cy:.2f} c "
            f"{cx - r:.2f} {cy - k:.2f} {cx - k:.2f} {cy - r:.2f} {cx:.2f} {cy - r:.2f} c "
            f"{cx + k:.2f} {cy - r:.2f} {cx + r:.2f} {cy - k:.2f} {cx + r:.2f} {cy:.2f} c")


def _action(a: dict) -> pikepdf.Dictionary:
    t = a.get("type")
    if t == "url":
        return pikepdf.Dictionary(S=pikepdf.Name.URI, URI=pikepdf.String(a["value"]))
    if t == "reset":
        return pikepdf.Dictionary(S=pikepdf.Name.ResetForm)
    if t == "submit":
        return pikepdf.Dictionary(S=pikepdf.Name.SubmitForm, F=pikepdf.Dictionary(
            FS=pikepdf.Name.URL, F=pikepdf.String(a["value"])), Flags=4)
    if t == "print":
        return pikepdf.Dictionary(S=pikepdf.Name.JavaScript, JS=pikepdf.String("this.print();"))
    if t == "js":
        return pikepdf.Dictionary(S=pikepdf.Name.JavaScript, JS=pikepdf.String(a["value"]))
    raise ValueError(f"Bilinmeyen eylem: {t}")


def remove_field(path: str, out: str, name: str) -> str:
    with pikepdf.open(path, allow_overwriting_input=True) as pdf:
        af = pdf.Root.get("/AcroForm")
        if af is None:
            pdf.save(out)
            return out
        targets = set()
        keep = pikepdf.Array()
        for f in af.Fields:
            if _full_name(f) == name:
                targets.add(f.objgen)
                for k in f.get("/Kids", []):
                    targets.add(k.objgen)
            else:
                keep.append(f)
        af.Fields = keep
        for p in pdf.pages:
            if "/Annots" in p.obj:
                p.obj.Annots = pikepdf.Array([a for a in p.obj.Annots if a.objgen not in targets])
        pdf.save(out)
    return out
