"""Dönüştürme: PDF/tarama -> Word, Excel, PowerPoint, ODT, RTF, TXT, CSV, HTML, EPUB, FB2;
Office/görüntü/metin -> PDF; PDF/A ve PDF/UA üretimi; toplu dönüştürme."""
from __future__ import annotations

import csv
import datetime as _dt
import html
import io
import os
import re
import shutil
import subprocess
import sys
import tempfile
import uuid
import warnings
from typing import Callable

warnings.filterwarnings("ignore", message=".*has no XMP equivalent.*")

from PIL import Image

from .ocr import Block, PageResult, recognize_pdf, recognize_image
from .preprocess import PreprocessOptions, preprocess

EXPORT_FORMATS = {
    "docx": "Microsoft Word (*.docx)", "xlsx": "Microsoft Excel (*.xlsx)", "pptx": "Microsoft PowerPoint (*.pptx)",
    "odt": "OpenDocument Metin (*.odt)", "rtf": "Zengin Metin (*.rtf)", "txt": "Düz Metin (*.txt)",
    "csv": "CSV (*.csv)", "html": "Web Sayfası (*.html)", "epub": "E-Kitap EPUB (*.epub)", "fb2": "FictionBook (*.fb2)",
    "pdf": "Aranabilir PDF (*.pdf)", "pdfa": "PDF/A arşiv (*.pdf)", "pdfua": "Erişilebilir PDF/UA (*.pdf)",
}
IMAGE_EXT = (".png", ".jpg", ".jpeg", ".tif", ".tiff", ".bmp", ".gif", ".webp", ".jp2", ".pcx")
OFFICE_EXT = (".doc", ".docx", ".rtf", ".odt", ".xls", ".xlsx", ".ods", ".ppt", ".pptx", ".odp", ".vsd", ".vsdx")
TEXT_EXT = (".txt", ".htm", ".html", ".csv", ".md")


# ------------------------------------------------------------------ yardımcılar
def _pt(px: float, dpi: int) -> float:
    return px * 72.0 / dpi


def _font_pt(b: Block, dpi: int) -> float:
    if not b.font_px:
        return 11
    return max(6, min(48, round(_pt(b.font_px, dpi) * 1.05 * 2) / 2))


def _paragraph_text(b: Block) -> str:
    """Satır sonu tirelerini birleştirip paragrafı akan metne çevirir."""
    out = ""
    for l in b.lines:
        t = l.text.strip()
        if out.endswith("-") and t[:1].islower():
            out = out[:-1] + t
        else:
            out = (out + " " + t) if out else t
    return out


def _crop(res: PageResult, box) -> Image.Image | None:
    if res.image is None:
        return None
    x0, y0, x1, y1 = [int(v) for v in box]
    return res.image.crop((max(0, x0), max(0, y0), min(res.width, x1), min(res.height, y1)))


_num_re = re.compile(r"^-?[\d.]+(,\d+)?$|^-?\d+(\.\d+)?$")


def parse_number(s: str):
    t = s.strip().replace("₺", "").replace("TL", "").replace("%", "").strip()
    if not t or not _num_re.match(t):
        return None
    try:
        if "," in t:
            return float(t.replace(".", "").replace(",", "."))
        if t.count(".") > 1:
            return float(t.replace(".", ""))
        return float(t) if "." in t else int(t)
    except ValueError:
        return None


# ------------------------------------------------------------------ yazıcılar
def to_docx(results: list[PageResult], out: str, mode: str = "formatted", images: bool = True) -> str:
    """mode: 'formatted' (düzenlenebilir kopya: başlıklar, yazı boyutları, tablolar, resimler),
    'plain' (yalnızca metin)."""
    from docx import Document
    from docx.enum.section import WD_ORIENT
    from docx.enum.text import WD_BREAK
    from docx.shared import Pt, Emu

    doc = Document()
    st = doc.styles["Normal"]
    st.font.name = "Calibri"
    st.font.size = Pt(11)
    for pi, res in enumerate(results):
        sec = doc.sections[-1] if pi == 0 else doc.add_section()
        pw, ph = _pt(res.width, res.dpi), _pt(res.height, res.dpi)
        sec.page_width, sec.page_height = Emu(int(pw * 12700)), Emu(int(ph * 12700))
        sec.orientation = WD_ORIENT.LANDSCAPE if pw > ph else WD_ORIENT.PORTRAIT
        m = Emu(int(min(72, pw * 0.1) * 12700))
        sec.left_margin = sec.right_margin = sec.top_margin = sec.bottom_margin = m
        usable = pw - 2 * min(72, pw * 0.1)
        for b in res.blocks:
            if b.kind == "text":
                if mode == "plain":
                    doc.add_paragraph(_paragraph_text(b))
                    continue
                size = _font_pt(b, res.dpi)
                if b.heading:
                    p = doc.add_paragraph()
                    run = p.add_run(_paragraph_text(b))
                    run.bold = True
                    run.font.size = Pt(size)
                    p.style = doc.styles["Heading 1"] if size >= 16 else doc.styles["Heading 2"]
                    for r in p.runs:
                        r.font.size = Pt(size)
                else:
                    p = doc.add_paragraph()
                    run = p.add_run(_paragraph_text(b))
                    run.font.size = Pt(size)
                    indent = _pt(b.box[0], res.dpi) - min(72, pw * 0.1)
                    if indent > 20:
                        p.paragraph_format.left_indent = Pt(min(indent, usable * 0.6))
            elif b.kind == "table" and b.cells:
                ncols = max(len(r) for r in b.cells)
                t = doc.add_table(rows=len(b.cells), cols=ncols)
                t.style = "Table Grid"
                for ri, row in enumerate(b.cells):
                    for ci, cell in enumerate(row):
                        t.cell(ri, ci).text = cell["text"]
                doc.add_paragraph()
            elif b.kind == "picture" and images and mode != "plain":
                img = _crop(res, b.box)
                if img is not None and img.width > 5 and img.height > 5:
                    bio = io.BytesIO()
                    img.save(bio, "PNG")
                    bio.seek(0)
                    width = min(_pt(b.box[2] - b.box[0], res.dpi), usable)
                    doc.add_picture(bio, width=Pt(width))
            elif b.kind == "barcode":
                doc.add_paragraph(f"[Barkod: {b.value}]")
    doc.save(out)
    return out


def to_xlsx(results: list[PageResult], out: str, include_text: bool = True) -> str:
    from openpyxl import Workbook
    from openpyxl.styles import Font, Border, Side, Alignment, PatternFill
    wb = Workbook()
    wb.remove(wb.active)
    thin = Side(style="thin", color="999999")
    tcount = 0
    text_rows = []
    for pi, res in enumerate(results):
        for b in res.blocks:
            if b.kind == "table" and b.cells:
                tcount += 1
                ws = wb.create_sheet(f"Tablo {tcount}")
                for ri, row in enumerate(b.cells, 1):
                    for ci, cell in enumerate(row, 1):
                        v = parse_number(cell["text"])
                        c = ws.cell(ri, ci, v if v is not None else cell["text"])
                        c.border = Border(left=thin, right=thin, top=thin, bottom=thin)
                        c.alignment = Alignment(wrap_text=True, vertical="top")
                        if isinstance(v, float):
                            c.number_format = "#,##0.00"
                        if ri == 1:
                            c.font = Font(bold=True)
                            c.fill = PatternFill("solid", fgColor="E3EEFB")
                for ci in range(1, max(len(r) for r in b.cells) + 1):
                    width = max(len(str(ws.cell(r, ci).value or "")) for r in range(1, len(b.cells) + 1))
                    ws.column_dimensions[ws.cell(1, ci).column_letter].width = min(60, max(8, width + 2))
            elif b.kind == "text":
                for l in b.lines:
                    text_rows.append((pi + 1, l.text))
    if include_text and (text_rows or not tcount):
        ws = wb.create_sheet("Metin", 0 if not tcount else None)
        ws.append(["Sayfa", "Metin"])
        ws["A1"].font = ws["B1"].font = Font(bold=True)
        for r in text_rows:
            ws.append(list(r))
        ws.column_dimensions["B"].width = 100
    wb.save(out)
    return out


def to_pptx(results: list[PageResult], out: str) -> str:
    """Her sayfa bir slayt; metin kutuları ve resimler orijinal konumlarında (birebir düzen)."""
    from pptx import Presentation
    from pptx.util import Pt, Emu
    prs = Presentation()
    blank = prs.slide_layouts[6]
    if results:
        r0 = results[0]
        prs.slide_width = Emu(int(_pt(r0.width, r0.dpi) * 12700))
        prs.slide_height = Emu(int(_pt(r0.height, r0.dpi) * 12700))
    for res in results:
        s = prs.slides.add_slide(blank)
        E = lambda px: Emu(int(_pt(px, res.dpi) * 12700))
        for b in res.blocks:
            x0, y0, x1, y1 = b.box
            if b.kind == "picture":
                img = _crop(res, b.box)
                if img is not None:
                    bio = io.BytesIO()
                    img.save(bio, "PNG")
                    bio.seek(0)
                    s.shapes.add_picture(bio, E(x0), E(y0), E(x1 - x0), E(y1 - y0))
            elif b.kind == "table" and b.cells:
                rows, cols = len(b.cells), max(len(r) for r in b.cells)
                gt = s.shapes.add_table(rows, cols, E(x0), E(y0), E(x1 - x0), E(y1 - y0)).table
                for ri, row in enumerate(b.cells):
                    for ci, cell in enumerate(row):
                        gt.cell(ri, ci).text = cell["text"]
                        for p in gt.cell(ri, ci).text_frame.paragraphs:
                            for r in p.runs:
                                r.font.size = Pt(10)
            elif b.kind in ("text", "barcode"):
                tb = s.shapes.add_textbox(E(x0), E(y0), E(max(x1 - x0, 20)), E(max(y1 - y0, 10)))
                tf = tb.text_frame
                tf.word_wrap = True
                for m in ("margin_left", "margin_right", "margin_top", "margin_bottom"):
                    setattr(tf, m, 0)
                lines = [l.text for l in b.lines] if b.kind == "text" else [b.value]
                for k, ln in enumerate(lines):
                    p = tf.paragraphs[0] if k == 0 else tf.add_paragraph()
                    run = p.add_run()
                    run.text = ln
                    run.font.size = Pt(_font_pt(b, res.dpi) * 0.92)
                    run.font.bold = b.heading
    prs.save(out)
    return out


def to_txt(results: list[PageResult], out: str, page_breaks: bool = True) -> str:
    parts = []
    for res in results:
        parts.append("\n\n".join(_paragraph_text(b) if b.kind == "text" else b.text for b in res.blocks))
    with open(out, "w", encoding="utf-8-sig") as f:
        f.write(("\n\f\n" if page_breaks else "\n\n").join(parts))
    return out


def to_csv(results: list[PageResult], out: str, delimiter: str = ";") -> str:
    with open(out, "w", newline="", encoding="utf-8-sig") as f:
        w = csv.writer(f, delimiter=delimiter)
        any_table = False
        for res in results:
            for b in res.blocks:
                if b.kind == "table":
                    any_table = True
                    for row in b.cells:
                        w.writerow([c["text"] for c in row])
                    w.writerow([])
        if not any_table:
            for res in results:
                for b in res.blocks:
                    for l in b.lines:
                        w.writerow([l.text])
    return out


def _rtf_escape(s: str) -> str:
    out = []
    for ch in s:
        o = ord(ch)
        if ch in "\\{}":
            out.append("\\" + ch)
        elif o > 127:
            out.append(f"\\u{o if o < 32768 else o - 65536}?")
        elif ch == "\n":
            out.append("\\line ")
        else:
            out.append(ch)
    return "".join(out)


def to_rtf(results: list[PageResult], out: str) -> str:
    body = ["{\\rtf1\\ansi\\ansicpg1254\\deff0{\\fonttbl{\\f0 Calibri;}}\\fs22 "]
    for pi, res in enumerate(results):
        if pi:
            body.append("\\page ")
        for b in res.blocks:
            if b.kind == "text":
                size = int(_font_pt(b, res.dpi) * 2)
                body.append(f"{{\\pard{'\\b' if b.heading else ''}\\fs{size} {_rtf_escape(_paragraph_text(b))}\\par}}")
            elif b.kind == "table":
                for row in b.cells:
                    n = len(row)
                    body.append("\\trowd\\trgaph108" + "".join(f"\\clbrdrt\\brdrs\\clbrdrl\\brdrs\\clbrdrb\\brdrs\\clbrdrr\\brdrs\\cellx{(k + 1) * 9000 // n}" for k in range(n)))
                    body.append("".join(f"\\pard\\intbl {_rtf_escape(c['text'])}\\cell " for c in row) + "\\row ")
                body.append("\\pard\\par ")
    body.append("}")
    with open(out, "w", encoding="ascii", errors="replace") as f:
        f.write("".join(body))
    return out


def to_html(results: list[PageResult], out: str, title: str = "", embed_images: bool = True) -> str:
    import base64
    parts = [f"<!DOCTYPE html><html lang='tr'><head><meta charset='utf-8'><title>{html.escape(title or 'Belge')}</title>"
             "<style>body{font-family:Segoe UI,Arial,sans-serif;max-width:900px;margin:2em auto;line-height:1.5;color:#222}"
             "table{border-collapse:collapse;margin:1em 0}td{border:1px solid #aaa;padding:4px 8px}"
             "hr.page{border:0;border-top:1px dashed #bbb;margin:2em 0}img{max-width:100%}</style></head><body>"]
    for pi, res in enumerate(results):
        if pi:
            parts.append("<hr class='page'>")
        for b in res.blocks:
            if b.kind == "text":
                tag = "h2" if b.heading else "p"
                parts.append(f"<{tag}>{html.escape(_paragraph_text(b))}</{tag}>")
            elif b.kind == "table":
                parts.append("<table>" + "".join("<tr>" + "".join(f"<td>{html.escape(c['text'])}</td>" for c in row) + "</tr>"
                                                 for row in b.cells) + "</table>")
            elif b.kind == "picture" and embed_images:
                img = _crop(res, b.box)
                if img is not None:
                    bio = io.BytesIO()
                    img.save(bio, "PNG")
                    parts.append(f"<img alt='' src='data:image/png;base64,{base64.b64encode(bio.getvalue()).decode()}'>")
            elif b.kind == "barcode":
                parts.append(f"<p><code>{html.escape(b.value)}</code></p>")
    parts.append("</body></html>")
    with open(out, "w", encoding="utf-8") as f:
        f.write("".join(parts))
    return out


def to_odt(results: list[PageResult], out: str) -> str:
    from odf.opendocument import OpenDocumentText
    from odf.style import Style, TextProperties, ParagraphProperties, TableCellProperties
    from odf.text import P, H
    from odf.table import Table, TableColumn, TableRow, TableCell
    doc = OpenDocumentText()
    hs = Style(name="Baslik", family="paragraph")
    hs.addElement(TextProperties(fontsize="16pt", fontweight="bold"))
    doc.styles.addElement(hs)
    pb = Style(name="SayfaSonu", family="paragraph")
    pb.addElement(ParagraphProperties(breakbefore="page"))
    doc.automaticstyles.addElement(pb)
    cs = Style(name="Hucre", family="table-cell")
    cs.addElement(TableCellProperties(border="0.5pt solid #999999", padding="0.05cm"))
    doc.automaticstyles.addElement(cs)
    for pi, res in enumerate(results):
        if pi:
            doc.text.addElement(P(stylename=pb, text=""))
        for b in res.blocks:
            if b.kind == "text":
                doc.text.addElement(H(outlinelevel=1, stylename=hs, text=_paragraph_text(b)) if b.heading
                                    else P(text=_paragraph_text(b)))
            elif b.kind == "table" and b.cells:
                t = Table()
                t.addElement(TableColumn(numbercolumnsrepeated=max(len(r) for r in b.cells)))
                for row in b.cells:
                    tr = TableRow()
                    for c in row:
                        tc = TableCell(stylename=cs)
                        tc.addElement(P(text=c["text"]))
                        tr.addElement(tc)
                    t.addElement(tr)
                doc.text.addElement(t)
    doc.save(out)
    return out


def to_epub(results: list[PageResult], out: str, title: str = "Belge", author: str = "") -> str:
    """EPUB 3 e-kitap (harici kütüphane kullanmadan; başlıklar bölüm olur, resimler gömülür)."""
    import zipfile
    uid = "urn:uuid:" + str(uuid.uuid4())
    chapters: list[tuple[str, list[str]]] = []
    images: list[tuple[str, bytes]] = []
    cur_title, cur = title, []

    def flush():
        nonlocal cur
        if cur:
            chapters.append((cur_title, cur))
            cur = []
    for res in results:
        for b in res.blocks:
            if b.kind == "text":
                t = html.escape(_paragraph_text(b))
                if b.heading:
                    flush()
                    cur_title = _paragraph_text(b)[:80]
                    cur.append(f"<h2>{t}</h2>")
                else:
                    cur.append(f"<p>{t}</p>")
            elif b.kind == "table":
                cur.append("<table>" + "".join("<tr>" + "".join(f"<td>{html.escape(c['text'])}</td>" for c in r) + "</tr>"
                                               for r in b.cells) + "</table>")
            elif b.kind == "picture":
                img = _crop(res, b.box)
                if img is not None:
                    bio = io.BytesIO()
                    img.convert("RGB").save(bio, "JPEG", quality=85)
                    name = f"images/resim{len(images) + 1:03d}.jpg"
                    images.append((name, bio.getvalue()))
                    cur.append(f'<p><img src="{name}" alt=""/></p>')
    flush()
    if not chapters:
        chapters = [(title, ["<p></p>"])]

    def xhtml(t, body):
        return ('<?xml version="1.0" encoding="utf-8"?>\n<!DOCTYPE html>\n<html xmlns="http://www.w3.org/1999/xhtml" '
                f'xmlns:epub="http://www.idpf.org/2007/ops" xml:lang="tr" lang="tr"><head><title>{html.escape(t)}</title>'
                '<link rel="stylesheet" type="text/css" href="stil.css"/></head><body>' + body + "</body></html>")
    now = _dt.datetime.now(_dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    manifest = ['<item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>',
                '<item id="css" href="stil.css" media-type="text/css"/>']
    spine = []
    for k in range(len(chapters)):
        manifest.append(f'<item id="b{k + 1}" href="bolum{k + 1}.xhtml" media-type="application/xhtml+xml"/>')
        spine.append(f'<itemref idref="b{k + 1}"/>')
    for k, (name, _) in enumerate(images):
        manifest.append(f'<item id="r{k + 1}" href="{name}" media-type="image/jpeg"/>')
    opf = ('<?xml version="1.0" encoding="utf-8"?>\n<package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="uid">'
           '<metadata xmlns:dc="http://purl.org/dc/elements/1.1/">'
           f'<dc:identifier id="uid">{uid}</dc:identifier><dc:title>{html.escape(title)}</dc:title><dc:language>tr</dc:language>'
           + (f"<dc:creator>{html.escape(author)}</dc:creator>" if author else "") +
           f'<meta property="dcterms:modified">{now}</meta></metadata><manifest>' + "".join(manifest) +
           '</manifest><spine>' + "".join(spine) + "</spine></package>")
    nav = xhtml("İçindekiler", '<nav epub:type="toc" id="toc"><h1>İçindekiler</h1><ol>' +
                "".join(f'<li><a href="bolum{k + 1}.xhtml">{html.escape(t)}</a></li>' for k, (t, _) in enumerate(chapters)) + "</ol></nav>")
    css = "body{font-family:serif;line-height:1.5}h2{margin-top:1.5em}table{border-collapse:collapse}td{border:1px solid #999;padding:3px 6px}img{max-width:100%}"
    container = ('<?xml version="1.0" encoding="utf-8"?>\n<container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">'
                 '<rootfiles><rootfile full-path="OEBPS/icerik.opf" media-type="application/oebps-package+xml"/></rootfiles></container>')
    with zipfile.ZipFile(out, "w") as z:
        z.writestr(zipfile.ZipInfo("mimetype"), "application/epub+zip", compress_type=zipfile.ZIP_STORED)
        z.writestr("META-INF/container.xml", container, compress_type=zipfile.ZIP_DEFLATED)
        z.writestr("OEBPS/icerik.opf", opf, compress_type=zipfile.ZIP_DEFLATED)
        z.writestr("OEBPS/nav.xhtml", nav, compress_type=zipfile.ZIP_DEFLATED)
        z.writestr("OEBPS/stil.css", css, compress_type=zipfile.ZIP_DEFLATED)
        for k, (t, body) in enumerate(chapters):
            z.writestr(f"OEBPS/bolum{k + 1}.xhtml", xhtml(t, "".join(body)), compress_type=zipfile.ZIP_DEFLATED)
        for name, data in images:
            z.writestr(f"OEBPS/{name}", data, compress_type=zipfile.ZIP_STORED)
    return out


def to_fb2(results: list[PageResult], out: str, title: str = "Belge", author: str = "") -> str:
    secs = []
    cur = []
    for res in results:
        for b in res.blocks:
            if b.kind == "text":
                t = html.escape(_paragraph_text(b))
                if b.heading:
                    if cur:
                        secs.append("<section>" + "".join(cur) + "</section>")
                    cur = [f"<title><p>{t}</p></title>"]
                else:
                    cur.append(f"<p>{t}</p>")
            elif b.kind == "table":
                cur.append("<table>" + "".join("<tr>" + "".join(f"<td>{html.escape(c['text'])}</td>" for c in r) + "</tr>" for r in b.cells) + "</table>")
    if cur:
        secs.append("<section>" + "".join(cur) + "</section>")
    xml = ('<?xml version="1.0" encoding="utf-8"?>\n<FictionBook xmlns="http://www.gribuser.ru/xml/fictionbook/2.0">'
           f"<description><title-info><genre>nonfiction</genre><author><nickname>{html.escape(author or 'Bilinmiyor')}</nickname></author>"
           f"<book-title>{html.escape(title)}</book-title><lang>tr</lang></title-info>"
           f"<document-info><program-used>Mesly PDF</program-used><date>{_dt.date.today()}</date></document-info></description>"
           "<body>" + "".join(secs) + "</body></FictionBook>")
    with open(out, "w", encoding="utf-8") as f:
        f.write(xml)
    return out


WRITERS = {"docx": to_docx, "xlsx": to_xlsx, "pptx": to_pptx, "txt": to_txt, "csv": to_csv, "rtf": to_rtf,
           "html": to_html, "odt": to_odt, "epub": to_epub, "fb2": to_fb2}


# ------------------------------------------------------------------ PDF/A ve PDF/UA
def _srgb_icc() -> bytes:
    from PIL import ImageCms
    return ImageCms.ImageCmsProfile(ImageCms.createProfile("sRGB")).tobytes()


def make_pdfa(path: str, out: str, part: int = 2, conformance: str = "B", title: str | None = None,
              lang: str = "tr-TR", password: str | None = None) -> tuple[str, list[str]]:
    """PDF/A-1b/2b/2u/3b dönüştürmesi (meta veri, çıktı amacı, şifre/JS kaldırma).
    Uyarı listesi döndürür (gömülmemiş yazı tipleri vb.)."""
    import pikepdf
    warnings = []
    with pikepdf.open(path, password=password or "", allow_overwriting_input=True) as pdf:
        root = pdf.Root
        for k in ("/OpenAction", "/AA", "/JavaScript"):
            if k in root:
                del root[k]
        if "/Names" in root and "/JavaScript" in root.Names:
            del root.Names.JavaScript
        if part == 1 and "/Names" in root and "/EmbeddedFiles" in root.Names:
            del root.Names.EmbeddedFiles
            warnings.append("PDF/A-1 ekli dosyaya izin vermez; ekler kaldırıldı")
        icc = pdf.make_stream(_srgb_icc(), N=3)
        root.OutputIntents = pikepdf.Array([pikepdf.Dictionary(
            Type=pikepdf.Name.OutputIntent, S=pikepdf.Name.GTS_PDFA1, OutputConditionIdentifier=pikepdf.String("sRGB IEC61966-2.1"),
            Info=pikepdf.String("sRGB IEC61966-2.1"), DestOutputProfile=icc)])
        if lang:
            root.Lang = pikepdf.String(lang)
        if title:
            pdf.docinfo["/Title"] = pikepdf.String(title)
        elif "/Title" not in pdf.docinfo:
            pdf.docinfo["/Title"] = pikepdf.String(os.path.splitext(os.path.basename(path))[0])
        for p in pdf.pages:
            for a in p.obj.get("/Annots", []):
                a.F = int(a.get("/F", 0)) | 4  # yazdırılabilir bayrağı zorunlu
                for k in ("/A",):
                    if k in a and str(a[k].get("/S", "")) in ("/JavaScript", "/Launch"):
                        del a[k]
        # gömülmemiş yazı tipi denetimi
        seen = set()
        for p in pdf.pages:
            fonts = p.obj.get("/Resources", {}).get("/Font", {}) if "/Resources" in p.obj else {}
            for _, f in (fonts.items() if hasattr(fonts, "items") else []):
                name = str(f.get("/BaseFont", ""))
                fd = f.get("/FontDescriptor")
                if f.get("/Subtype") == "/Type0":
                    df = f.DescendantFonts[0]
                    fd = df.get("/FontDescriptor")
                embedded = fd is not None and any(k in fd for k in ("/FontFile", "/FontFile2", "/FontFile3"))
                if not embedded and name not in seen and f.get("/Subtype") != "/Type3":
                    seen.add(name)
                    warnings.append(f"Yazı tipi gömülü değil: {name.lstrip('/')}")
        with pdf.open_metadata(set_pikepdf_as_editor=False) as meta:
            meta.load_from_docinfo(pdf.docinfo)
            meta["pdfaid:part"] = str(part)
            meta["pdfaid:conformance"] = conformance
            meta["xmp:CreatorTool"] = "Mesly PDF"
        pdf.save(out, fix_metadata_version=True, object_stream_mode=pikepdf.ObjectStreamMode.disable if part == 1
                 else pikepdf.ObjectStreamMode.preserve)
    return out, warnings


def check_pdfa(path: str) -> dict:
    import pikepdf
    with pikepdf.open(path) as pdf:
        meta = pdf.open_metadata()
        return {"part": meta.get("pdfaid:part"), "conformance": meta.get("pdfaid:conformance"),
                "output_intent": "/OutputIntents" in pdf.Root, "encrypted": pdf.is_encrypted}


def make_pdfua(path: str, out: str, title: str | None = None, lang: str = "tr-TR", ocr_lang: str = "tur+eng") -> str:
    """Erişilebilir PDF: OCR'lı, etiketli (yapı ağacı), dil ve başlık bilgili.
    Sayfalar yeniden tanınır ve her satır bir <P> öğesi olarak etiketlenir."""
    import pikepdf
    from reportlab.pdfgen import canvas
    from reportlab.lib.utils import ImageReader
    from .ocr import _text_layer_canvas
    results = recognize_pdf(path, ocr_lang)
    buf = io.BytesIO()
    c = canvas.Canvas(buf)
    counts = []
    with pikepdf.open(path) as src:
        sizes = []
        for p in src.pages:
            mb = [float(v) for v in p.mediabox]
            sizes.append((mb[2] - mb[0], mb[3] - mb[1]))
    for res, (w, h) in zip(results, sizes):
        c.setPageSize((w, h))
        c._code.append("/Artifact BMC")
        jb = io.BytesIO()
        res.image.convert("RGB").save(jb, "JPEG", quality=80)
        jb.seek(0)
        c.drawImage(ImageReader(jb), 0, 0, w, h)
        c._code.append("EMC")
        counts.append(_text_layer_canvas(c, res, w, h, tagged=True))
        c.showPage()
    c.save()
    with pikepdf.open(io.BytesIO(buf.getvalue())) as pdf:
        doc_elem = pdf.make_indirect(pikepdf.Dictionary(Type=pikepdf.Name.StructElem, S=pikepdf.Name.Document, K=pikepdf.Array()))
        st_root = pdf.make_indirect(pikepdf.Dictionary(Type=pikepdf.Name.StructTreeRoot, K=doc_elem))
        doc_elem.P = st_root
        nums = pikepdf.Array()
        for pi, page in enumerate(pdf.pages):
            page.obj.StructParents = pi
            page.obj.Tabs = pikepdf.Name.S
            arr = pikepdf.Array()
            for mcid in range(counts[pi]):
                el = pdf.make_indirect(pikepdf.Dictionary(Type=pikepdf.Name.StructElem, S=pikepdf.Name.P, P=doc_elem,
                                                          Pg=page.obj, K=mcid))
                doc_elem.K.append(el)
                arr.append(el)
            nums.append(pi)
            nums.append(pdf.make_indirect(arr))
        st_root.ParentTree = pdf.make_indirect(pikepdf.Dictionary(Nums=nums))
        st_root.ParentTreeNextKey = len(pdf.pages)
        pdf.Root.StructTreeRoot = st_root
        pdf.Root.MarkInfo = pikepdf.Dictionary(Marked=True)
        pdf.Root.Lang = pikepdf.String(lang)
        pdf.Root.ViewerPreferences = pikepdf.Dictionary(DisplayDocTitle=True)
        pdf.docinfo["/Title"] = pikepdf.String(title or os.path.splitext(os.path.basename(path))[0])
        with pdf.open_metadata(set_pikepdf_as_editor=False) as meta:
            meta.load_from_docinfo(pdf.docinfo)
            meta["pdfuaid:part"] = "1"
        pdf.save(out)
    return out


# ------------------------------------------------------------------ X -> PDF
def office_to_pdf(path: str, out: str) -> str:
    """Word/Excel/PowerPoint/OpenDocument -> PDF. Windows'ta yüklü Microsoft Office (COM),
    yoksa LibreOffice kullanılır."""
    ext = os.path.splitext(path)[1].lower()
    path, out = os.path.abspath(path), os.path.abspath(out)
    if sys.platform == "win32":
        try:
            import win32com.client  # type: ignore
            if ext in (".doc", ".docx", ".rtf", ".odt", ".txt"):
                app = win32com.client.DispatchEx("Word.Application")
                app.Visible = False
                try:
                    d = app.Documents.Open(path, ReadOnly=True)
                    d.ExportAsFixedFormat(out, 17, CreateBookmarks=1)
                    d.Close(False)
                finally:
                    app.Quit()
                return out
            if ext in (".xls", ".xlsx", ".ods", ".csv"):
                app = win32com.client.DispatchEx("Excel.Application")
                app.Visible = False
                app.DisplayAlerts = False
                try:
                    wb = app.Workbooks.Open(path, ReadOnly=True)
                    wb.ExportAsFixedFormat(0, out)
                    wb.Close(False)
                finally:
                    app.Quit()
                return out
            if ext in (".ppt", ".pptx", ".odp"):
                app = win32com.client.DispatchEx("PowerPoint.Application")
                try:
                    pr = app.Presentations.Open(path, ReadOnly=True, WithWindow=False)
                    pr.SaveAs(out, 32)
                    pr.Close()
                finally:
                    app.Quit()
                return out
        except Exception:
            pass  # Office yoksa LibreOffice'e geç
    soffice = find_libreoffice()
    if not soffice:
        raise RuntimeError("Office belgesini PDF'e çevirmek için Microsoft Office veya LibreOffice gerekli")
    tmpd = tempfile.mkdtemp()
    subprocess.run([soffice, "--headless", "--convert-to", "pdf", "--outdir", tmpd, path],
                   check=True, capture_output=True, timeout=300,
                   creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    produced = os.path.join(tmpd, os.path.splitext(os.path.basename(path))[0] + ".pdf")
    shutil.move(produced, out)
    shutil.rmtree(tmpd, ignore_errors=True)
    return out


def find_libreoffice() -> str | None:
    for c in ("soffice", "libreoffice"):
        p = shutil.which(c)
        if p:
            return p
    for c in (r"C:\Program Files\LibreOffice\program\soffice.exe", r"C:\Program Files (x86)\LibreOffice\program\soffice.exe"):
        if os.path.exists(c):
            return c
    return None


def text_to_pdf(path: str, out: str) -> str:
    from reportlab.lib.pagesizes import A4
    from reportlab.lib.styles import ParagraphStyle
    from reportlab.platypus import SimpleDocTemplate, Paragraph, Spacer
    from .fonts import font_name
    raw = open(path, "rb").read()
    for enc in ("utf-8-sig", "cp1254", "latin-1"):
        try:
            text = raw.decode(enc)
            break
        except UnicodeDecodeError:
            continue
    if path.lower().endswith((".htm", ".html")):
        text = re.sub(r"<(script|style).*?</\1>", "", text, flags=re.S | re.I)
        text = re.sub(r"<br\s*/?>|</p>|</div>|</h\d>|</li>", "\n", text, flags=re.I)
        text = html.unescape(re.sub(r"<[^>]+>", "", text))
    st = ParagraphStyle("t", fontName=font_name(), fontSize=11, leading=15)
    story = []
    for para in text.split("\n"):
        story.append(Paragraph(html.escape(para) or "&nbsp;", st))
    SimpleDocTemplate(out, pagesize=A4, title=os.path.basename(path)).build(story or [Spacer(1, 1)])
    return out


def any_to_pdf(paths: list[str], out: str, ocr: bool = False, lang: str = "tur+eng",
               opts: PreprocessOptions | None = None, progress=None, cancel=None) -> str:
    """Desteklenen herhangi dosya(lar)dan tek PDF oluşturur (birleştirerek)."""
    from . import pages as pg
    tmpdir = tempfile.mkdtemp()
    parts = []
    try:
        for n, p in enumerate(paths):
            ext = os.path.splitext(p)[1].lower()
            dst = os.path.join(tmpdir, f"{n:04d}.pdf")
            if ext == ".pdf":
                if ocr:
                    from .ocr import ocr_pdf
                    ocr_pdf(p, dst, lang)
                else:
                    shutil.copyfile(p, dst)
            elif ext in IMAGE_EXT:
                if ocr:
                    from .ocr import ocr_images_to_pdf
                    ocr_images_to_pdf([p], dst, lang, opts)
                else:
                    pg.images_to_pdf([p], dst)
            elif ext in OFFICE_EXT:
                office_to_pdf(p, dst)
            elif ext in TEXT_EXT:
                text_to_pdf(p, dst)
            else:
                raise ValueError(f"Desteklenmeyen dosya türü: {ext}")
            parts.append(dst)
            if progress:
                progress(n + 1, len(paths))
        if len(parts) == 1:
            shutil.copyfile(parts[0], out)
        else:
            pg.merge(parts, out, bookmarks=False)
            # yer imlerini gerçek dosya adlarıyla kur
            from .editing import set_bookmarks
            from .document import PdfDocument
            items, start = [], 0
            for src, part in zip(paths, parts):
                items.append({"title": os.path.basename(src), "page": start, "level": 0})
                with PdfDocument(part) as d:
                    start += d.page_count
            set_bookmarks(out, out, items)
    finally:
        shutil.rmtree(tmpdir, ignore_errors=True)
    return out


# ------------------------------------------------------------------ ana dönüştürme
def recognize_any(path: str, lang: str = "tur+eng", opts: PreprocessOptions | None = None, progress=None,
                  cancel=None, pages: list[int] | None = None, password: str | None = None) -> list[PageResult]:
    ext = os.path.splitext(path)[1].lower()
    if ext == ".pdf":
        return recognize_pdf(path, lang, opts=opts, progress=progress, cancel=cancel, pages=pages, password=password)
    if ext in IMAGE_EXT:
        img = Image.open(path)
        dpi = int((img.info.get("dpi") or (300, 300))[0] or 300)
        frames = []
        try:
            k = 0
            while True:
                img.seek(k)
                frames.append(img.convert("RGB").copy())
                k += 1
        except EOFError:
            pass
        res = []
        for n, fr in enumerate(frames):
            if progress:
                progress(n, len(frames))
            for pim in preprocess(fr, opts or PreprocessOptions(), dpi)[0]:
                lg = lang
                eff = int(dpi * pim.width / fr.width) if fr.width else dpi
                res.append(recognize_image(pim, lg if lg != "auto" else __import__("meslypdf.core.ocr", fromlist=["x"]).detect_language(pim), max(eff, 72)))
        return res
    if ext in OFFICE_EXT or ext in TEXT_EXT:
        tmp = tempfile.mktemp(suffix=".pdf")
        (office_to_pdf if ext in OFFICE_EXT else text_to_pdf)(path, tmp)
        try:
            return recognize_pdf(tmp, lang, opts=opts, progress=progress, cancel=cancel)
        finally:
            os.remove(tmp)
    raise ValueError(f"Desteklenmeyen dosya türü: {ext}")


def convert(paths: list[str] | str, out: str, fmt: str, lang: str = "tur+eng", opts: PreprocessOptions | None = None,
            mode: str = "formatted", progress: Callable[[int, int], None] | None = None,
            cancel: Callable[[], bool] | None = None, pages: list[int] | None = None,
            password: str | None = None) -> str:
    """Bir veya birden çok dosyayı tek çıktı dosyasına dönüştürür (birden çok girdi = birleştirerek)."""
    if isinstance(paths, str):
        paths = [paths]
    fmt = fmt.lower()
    if fmt in ("pdf", "pdfa", "pdfua"):
        tmp = tempfile.mktemp(suffix=".pdf")
        any_to_pdf(paths, tmp, ocr=True, lang=lang, opts=opts, progress=progress, cancel=cancel)
        try:
            if fmt == "pdfa":
                make_pdfa(tmp, out)
            elif fmt == "pdfua":
                make_pdfua(tmp, out, ocr_lang=lang)
            else:
                shutil.copyfile(tmp, out)
        finally:
            os.remove(tmp)
        return out
    results: list[PageResult] = []
    for n, p in enumerate(paths):
        sub = None
        if progress:
            sub = lambda i, t, n=n: progress(n * 100 + int(100 * i / max(t, 1)), len(paths) * 100)
        results += recognize_any(p, lang, opts, sub, cancel, pages, password)
    writer = WRITERS[fmt]
    if fmt == "docx":
        writer(results, out, mode=mode)
    elif fmt in ("epub", "fb2", "html"):
        writer(results, out, title=os.path.splitext(os.path.basename(paths[0]))[0])
    else:
        writer(results, out)
    return out


def batch_convert(paths: list[str], out_dir: str, fmt: str, progress=None, cancel=None, **kw) -> list[tuple[str, str]]:
    """Her dosyayı ayrı ayrı dönüştürür. [(girdi, çıktı veya 'HATA: ...')]"""
    os.makedirs(out_dir, exist_ok=True)
    ext = {"pdfa": "pdf", "pdfua": "pdf"}.get(fmt, fmt)
    results = []
    for n, p in enumerate(paths):
        if cancel and cancel():
            break
        if progress:
            progress(n, len(paths))
        base = os.path.splitext(os.path.basename(p))[0]
        out = os.path.join(out_dir, f"{base}.{ext}")
        k = 1
        while os.path.exists(out):
            out = os.path.join(out_dir, f"{base} ({k}).{ext}")
            k += 1
        try:
            convert([p], out, fmt, **kw)
            results.append((p, out))
        except Exception as e:
            results.append((p, f"HATA: {e}"))
    if progress:
        progress(len(paths), len(paths))
    return results
