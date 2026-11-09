"""Belge karşılaştırma: farklı formatlardaki (PDF, taranmış PDF, görüntü, Word, Excel, TXT) iki belgeyi
kelime düzeyinde karşılaştırır. Üstbilgi/altbilgi farklarını ayırır. Sonuç: fark listesi,
açıklamalı PDF'ler, değişiklik izlemeli Word belgesi, Excel tablosu."""
from __future__ import annotations

import difflib
import os
import re
import shutil
import tempfile
from dataclasses import dataclass, field

from .document import PdfDocument


@dataclass
class Token:
    text: str
    page: int
    rect: tuple       # PDF koordinatı (l,b,r,t)
    line: int         # satır kimliği (paragraf yeniden kurmak için)
    zone: str = "body"  # body / header / footer


@dataclass
class Difference:
    kind: str                      # Eklendi, Silindi, Değiştirildi
    old_text: str = ""
    new_text: str = ""
    old_tokens: list = field(default_factory=list)
    new_tokens: list = field(default_factory=list)
    zone: str = "body"

    @property
    def old_page(self):
        return self.old_tokens[0].page if self.old_tokens else None

    @property
    def new_page(self):
        return self.new_tokens[0].page if self.new_tokens else None

    @property
    def kind_icon(self):
        return {"Eklendi": "+", "Silindi": "−", "Değiştirildi": "≠"}.get(self.kind, "?")


@dataclass
class CompareResult:
    old_pdf: str
    new_pdf: str
    diffs: list
    old_tokens: list
    new_tokens: list
    old_name: str = ""
    new_name: str = ""

    def summary(self) -> dict:
        s = {"Eklendi": 0, "Silindi": 0, "Değiştirildi": 0}
        for d in self.diffs:
            s[d.kind] = s.get(d.kind, 0) + 1
        s["Toplam"] = len(self.diffs)
        return s


_punct = re.compile(r"[^\w]+", re.UNICODE)


def _norm(t: str, ignore_case: bool, ignore_punct: bool) -> str:
    if ignore_punct:
        t = _punct.sub("", t)
    if ignore_case:
        t = t.replace("I", "ı").replace("İ", "i").lower()
    return t


def prepare_pdf(path: str, lang: str = "tur+eng", password: str | None = None) -> str:
    """Karşılaştırma için belgeyi metin katmanlı PDF'e çevirir (gerekirse OCR)."""
    from .convert import any_to_pdf, IMAGE_EXT
    from .ocr import ocr_pdf
    ext = os.path.splitext(path)[1].lower()
    tmp = tempfile.mktemp(suffix=".pdf")
    if ext == ".pdf":
        with PdfDocument(path, password) as d:
            needs_ocr = any(len(d.page_text(i).strip()) < 20 for i in range(d.page_count))
        if needs_ocr:
            ocr_pdf(path, tmp, lang, password=password)
        else:
            if password:
                from .security import decrypt
                decrypt(path, tmp, password)
            else:
                shutil.copyfile(path, tmp)
    else:
        any_to_pdf([path], tmp, ocr=ext in IMAGE_EXT, lang=lang)
    return tmp


def extract_tokens(pdf: str, header_ratio: float = 0.06) -> list[Token]:
    toks: list[Token] = []
    line_id = 0
    with PdfDocument(pdf) as d:
        for i in range(d.page_count):
            pw, ph = d.page_size(i)
            words = d.words(i)
            prev_bottom = None
            for t, (l, b, r, tp) in words:
                if prev_bottom is None or abs(b - prev_bottom) > (tp - b) * 0.6:
                    line_id += 1
                prev_bottom = b
                zone = "header" if tp > ph * (1 - header_ratio) else ("footer" if b < ph * header_ratio else "body")
                toks.append(Token(t, i, (l, b, r, tp), line_id, zone))
    return toks


def compare(old_path: str, new_path: str, lang: str = "tur+eng", ignore_case: bool = False,
            ignore_punct: bool = False, ignore_headers: bool = False, progress=None,
            old_password: str | None = None, new_password: str | None = None) -> CompareResult:
    if progress:
        progress(0, 4)
    a_pdf = prepare_pdf(old_path, lang, old_password)
    if progress:
        progress(1, 4)
    b_pdf = prepare_pdf(new_path, lang, new_password)
    if progress:
        progress(2, 4)
    A, B = extract_tokens(a_pdf), extract_tokens(b_pdf)
    diffs: list[Difference] = []
    a = [t for t in A if not ignore_headers or t.zone == "body"]
    b = [t for t in B if not ignore_headers or t.zone == "body"]
    na = [_norm(t.text, ignore_case, ignore_punct) for t in a]
    nb = [_norm(t.text, ignore_case, ignore_punct) for t in b]
    sm = difflib.SequenceMatcher(None, na, nb, autojunk=False)
    for op, i1, i2, j1, j2 in sm.get_opcodes():
        if op == "equal":
            continue
        ot, nt = a[i1:i2], b[j1:j2]
        if ignore_punct and not any(_norm(t.text, ignore_case, True) for t in ot + nt):
            continue
        kind = {"replace": "Değiştirildi", "delete": "Silindi", "insert": "Eklendi"}[op]
        # Uzun değiştirme bloklarını satır düzeyinde alt farklara böl (daha okunur sonuç)
        if op == "replace" and (len(ot) > 3 or len(nt) > 3):
            sub = difflib.SequenceMatcher(None, [t.text for t in ot], [t.text for t in nt], autojunk=False)
            for op2, x1, x2, y1, y2 in sub.get_opcodes():
                if op2 == "equal":
                    continue
                k2 = {"replace": "Değiştirildi", "delete": "Silindi", "insert": "Eklendi"}[op2]
                o2, n2 = ot[x1:x2], nt[y1:y2]
                diffs.append(Difference(k2, " ".join(t.text for t in o2), " ".join(t.text for t in n2), o2, n2,
                                        (o2 or n2)[0].zone))
            continue
        diffs.append(Difference(kind, " ".join(t.text for t in ot), " ".join(t.text for t in nt), ot, nt,
                                (ot or nt)[0].zone))
    diffs.sort(key=lambda d: (d.new_page if d.new_page is not None else (d.old_page or 0),
                              -(d.new_tokens[0].rect[3] if d.new_tokens else d.old_tokens[0].rect[3])))
    if progress:
        progress(4, 4)
    return CompareResult(a_pdf, b_pdf, diffs, A, B, os.path.basename(old_path), os.path.basename(new_path))


def _rects_by_page(tokens) -> dict[int, list[tuple]]:
    out: dict[int, list[tuple]] = {}
    for t in tokens:
        out.setdefault(t.page, []).append(t.rect)
    return out


def _merge_rects(rects):
    """Aynı satırdaki bitişik kelime kutularını birleştirir."""
    rects = sorted(rects, key=lambda r: (-round(r[1]), r[0]))
    out = []
    for r in rects:
        if out and abs(out[-1][1] - r[1]) < 2 and r[0] - out[-1][2] < 12:
            l, b, rr, t = out[-1]
            out[-1] = (l, min(b, r[1]), max(rr, r[2]), max(t, r[3]))
        else:
            out.append(r)
    return out


def export_annotated_pdfs(res: CompareResult, old_out: str, new_out: str) -> tuple[str, str]:
    """Eski belgede silinen/değişenler kırmızı üstü çizili, yeni belgede eklenen/değişenler vurgulu."""
    from .annotations import add_markup
    shutil.copyfile(res.old_pdf, old_out)
    shutil.copyfile(res.new_pdf, new_out)
    for d in res.diffs:
        if d.old_tokens:
            for pg, rects in _rects_by_page(d.old_tokens).items():
                add_markup(old_out, old_out, pg, _merge_rects(rects), "StrikeOut", "#D32F2F", "Karşılaştırma",
                           f"{d.kind}: '{d.old_text}'" + (f" → '{d.new_text}'" if d.new_text else ""))
        if d.new_tokens:
            color = "#81C784" if d.kind == "Eklendi" else "#FFD54F"
            for pg, rects in _rects_by_page(d.new_tokens).items():
                add_markup(new_out, new_out, pg, _merge_rects(rects), "Highlight", color, "Karşılaştırma",
                           f"{d.kind}: " + (f"'{d.old_text}' → " if d.old_text else "") + f"'{d.new_text}'")
    return old_out, new_out


def export_xlsx(res: CompareResult, out: str) -> str:
    from openpyxl import Workbook
    from openpyxl.styles import Font, PatternFill, Alignment
    wb = Workbook()
    ws = wb.active
    ws.title = "Farklar"
    ws.append([f"Karşılaştırma: {res.old_name}  ↔  {res.new_name}"])
    ws["A1"].font = Font(bold=True, size=13)
    s = res.summary()
    ws.append([f"Toplam {s['Toplam']} fark · {s['Değiştirildi']} değişiklik · {s['Eklendi']} ekleme · {s['Silindi']} silme"])
    ws.append([])
    ws.append(["No", "Tür", "Bölüm", "Eski sayfa", "Eski metin", "Yeni sayfa", "Yeni metin"])
    for c in ws[4]:
        c.font = Font(bold=True, color="FFFFFF")
        c.fill = PatternFill("solid", fgColor="1565C0")
    fills = {"Eklendi": "E8F5E9", "Silindi": "FFEBEE", "Değiştirildi": "FFF8E1"}
    zone_tr = {"body": "Gövde", "header": "Üstbilgi", "footer": "Altbilgi"}
    for n, d in enumerate(res.diffs, 1):
        ws.append([n, d.kind, zone_tr[d.zone], (d.old_page + 1) if d.old_page is not None else "", d.old_text,
                   (d.new_page + 1) if d.new_page is not None else "", d.new_text])
        for c in ws[ws.max_row]:
            c.fill = PatternFill("solid", fgColor=fills[d.kind])
            c.alignment = Alignment(wrap_text=True, vertical="top")
    for col, w in zip("ABCDEFG", (6, 14, 11, 10, 50, 10, 50)):
        ws.column_dimensions[col].width = w
    ws.freeze_panes = "A5"
    wb.save(out)
    return out


def export_docx_track_changes(res: CompareResult, out: str, author: str = "Mesly PDF") -> str:
    """Yeni belgenin metnini, farkları Word 'Değişiklikleri İzle' (w:ins / w:del) olarak içeren .docx üretir."""
    import datetime as _dt
    from docx import Document
    from docx.oxml import OxmlElement
    from docx.oxml.ns import qn

    doc = Document()
    doc.add_heading(f"Karşılaştırma: {res.old_name} → {res.new_name}", level=1)
    now = _dt.datetime.now().strftime("%Y-%m-%dT%H:%M:%SZ")
    rev = [1]

    def run(text, deleted=False):
        r = OxmlElement("w:r")
        t = OxmlElement("w:delText" if deleted else "w:t")
        t.set(qn("xml:space"), "preserve")
        t.text = text
        r.append(t)
        return r

    def wrap(tag, r):
        e = OxmlElement(tag)
        e.set(qn("w:id"), str(rev[0]))
        e.set(qn("w:author"), author)
        e.set(qn("w:date"), now)
        rev[0] += 1
        e.append(r)
        return e

    # Yeni belge belirteçleri sırasıyla; silinenleri doğru konuma yerleştirmek için eşleme
    del_before: dict[int, list] = {}
    ins_ids, chg_ids = set(), set()
    new_index = {id(t): k for k, t in enumerate(res.new_tokens)}
    for d in res.diffs:
        for t in d.new_tokens:
            ins_ids.add(id(t))
        if d.old_tokens:
            anchor = new_index[id(d.new_tokens[0])] if d.new_tokens else None
            if anchor is None:
                # silinen metni, eski metinden sonraki ilk eşit kelimenin önüne koy
                last_old = d.old_tokens[-1]
                anchor = len(res.new_tokens)
                # en yakın sonraki yeni belirteç: aynı sayfa/konumdan sonra
                for k, t in enumerate(res.new_tokens):
                    if (t.page, -t.rect[3], t.rect[0]) > (last_old.page, -last_old.rect[3], last_old.rect[0]):
                        anchor = k
                        break
            del_before.setdefault(anchor, []).append(d.old_text)
    p = doc.add_paragraph()
    cur_line = None
    for k, t in enumerate(res.new_tokens + [None]):
        for txt in del_before.get(k, []):
            p._p.append(wrap("w:del", run(txt + " ", True)))
        if t is None:
            break
        if cur_line is not None and t.line != cur_line and (t.page != res.new_tokens[k - 1].page or
                                                             res.new_tokens[k - 1].rect[1] - t.rect[3] > (t.rect[3] - t.rect[1]) * 0.8):
            p = doc.add_paragraph()
        cur_line = t.line
        r = run(t.text + " ")
        p._p.append(wrap("w:ins", r) if id(t) in ins_ids else r)
    doc.save(out)
    return out


def export_report_text(res: CompareResult) -> str:
    lines = [f"{res.old_name} ↔ {res.new_name}", ""]
    for n, d in enumerate(res.diffs, 1):
        pg = f"s.{(d.new_page if d.new_page is not None else d.old_page) + 1}"
        if d.kind == "Değiştirildi":
            lines.append(f"{n}. [{pg}] Değiştirildi: '{d.old_text}' → '{d.new_text}'")
        elif d.kind == "Eklendi":
            lines.append(f"{n}. [{pg}] Eklendi: '{d.new_text}'")
        else:
            lines.append(f"{n}. [{pg}] Silindi: '{d.old_text}'")
    return "\n".join(lines)
