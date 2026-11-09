"""Sayfa düzenleme: birleştir, böl, döndür, sil, sırala, kırp, ekle, boş sayfa tespiti."""
from __future__ import annotations

import io
import os
from typing import Iterable, Sequence

import pikepdf
from PIL import Image


def parse_ranges(spec: str, page_count: int) -> list[int]:
    """'1-3,5,8-' gibi 1 tabanlı aralıkları 0 tabanlı indekslere çevirir."""
    result: list[int] = []
    spec = (spec or "").replace(" ", "")
    if not spec or spec.lower() in ("all", "tümü", "hepsi"):
        return list(range(page_count))
    for part in spec.split(","):
        if not part:
            continue
        if "-" in part:
            a, b = part.split("-", 1)
            start = int(a) if a else 1
            end = int(b) if b else page_count
        else:
            start = end = int(part)
        if start < 1 or end > page_count or start > end:
            raise ValueError(f"Geçersiz sayfa aralığı: {part} (belge {page_count} sayfa)")
        result.extend(range(start - 1, end))
    return result


def _save(pdf: pikepdf.Pdf, out: str):
    os.makedirs(os.path.dirname(os.path.abspath(out)) or ".", exist_ok=True)
    pdf.save(out)


def merge(inputs: Sequence[str], out: str, bookmarks: bool = True) -> str:
    """PDF ve görselleri tek PDF'te birleştirir; her dosya için yer imi ekler."""
    dst = pikepdf.new()
    marks = []
    for path in inputs:
        start = len(dst.pages)
        if path.lower().endswith(".pdf"):
            with pikepdf.open(path) as src:
                dst.pages.extend(src.pages)
        else:
            img_pdf = images_to_pdf_bytes([path])
            with pikepdf.open(io.BytesIO(img_pdf)) as src:
                dst.pages.extend(src.pages)
        marks.append((os.path.splitext(os.path.basename(path))[0], start))
    if bookmarks:
        with dst.open_outline() as outline:
            for title, idx in marks:
                outline.root.append(pikepdf.OutlineItem(title, idx))
    _save(dst, out)
    return out


def split(path: str, out_dir: str, mode: str = "every", value: str | int = 1) -> list[str]:
    """mode: 'every' (n sayfada bir), 'ranges' ('1-3,4-6'), 'bookmarks' (üst düzey yer imleri), 'size' (MB)."""
    os.makedirs(out_dir, exist_ok=True)
    base = os.path.splitext(os.path.basename(path))[0]
    outs = []
    with pikepdf.open(path) as src:
        n = len(src.pages)
        groups: list[list[int]] = []
        if mode == "every":
            k = max(1, int(value))
            groups = [list(range(i, min(i + k, n))) for i in range(0, n, k)]
        elif mode == "ranges":
            for part in str(value).split(";") if ";" in str(value) else str(value).split(","):
                groups.append(parse_ranges(part, n))
        elif mode == "bookmarks":
            starts = []
            with src.open_outline() as ol:
                for item in ol.root:
                    try:
                        dest = item.destination
                        if dest is None and item.action is not None:
                            dest = item.action.D
                        pg = dest[0] if isinstance(dest, pikepdf.Array) else None
                        for i, p in enumerate(src.pages):
                            if pg is not None and p.obj.objgen == pg.objgen:
                                starts.append(i)
                                break
                    except Exception:
                        continue
            starts = sorted(set([0] + starts))
            groups = [list(range(s, (starts[j + 1] if j + 1 < len(starts) else n))) for j, s in enumerate(starts)]
        elif mode == "size":
            limit = float(value) * 1024 * 1024
            cur: list[int] = []
            for i in range(n):
                cur.append(i)
                tmp = pikepdf.new()
                for j in cur:
                    tmp.pages.append(src.pages[j])
                buf = io.BytesIO()
                tmp.save(buf)
                if buf.tell() > limit and len(cur) > 1:
                    groups.append(cur[:-1])
                    cur = [i]
            if cur:
                groups.append(cur)
        else:
            raise ValueError(f"Bilinmeyen bölme modu: {mode}")
        for gi, g in enumerate(groups, 1):
            if not g:
                continue
            dst = pikepdf.new()
            for idx in g:
                dst.pages.append(src.pages[idx])
            out = os.path.join(out_dir, f"{base}_{gi:03d}.pdf")
            _save(dst, out)
            outs.append(out)
    return outs


def extract_pages(path: str, indices: Iterable[int], out: str) -> str:
    with pikepdf.open(path) as src:
        dst = pikepdf.new()
        for i in indices:
            dst.pages.append(src.pages[i])
        _save(dst, out)
    return out


def rotate(path: str, indices: Iterable[int], angle: int, out: str) -> str:
    with pikepdf.open(path, allow_overwriting_input=True) as pdf:
        for i in indices:
            pdf.pages[i].rotate(angle, relative=True)
        _save(pdf, out)
    return out


def delete(path: str, indices: Iterable[int], out: str) -> str:
    with pikepdf.open(path, allow_overwriting_input=True) as pdf:
        for i in sorted(set(indices), reverse=True):
            del pdf.pages[i]
        if len(pdf.pages) == 0:
            raise ValueError("Tüm sayfalar silinemez")
        _save(pdf, out)
    return out


def reorder(path: str, order: Sequence[int], out: str) -> str:
    with pikepdf.open(path) as src:
        dst = pikepdf.new()
        for i in order:
            dst.pages.append(src.pages[i])
        _copy_info(src, dst)
        _save(dst, out)
    return out


def insert_from(path: str, other: str, at: int, out: str, indices: Iterable[int] | None = None) -> str:
    """`other` dosyasındaki sayfaları (veya görseli) `at` konumuna ekler."""
    with pikepdf.open(path, allow_overwriting_input=True) as pdf:
        if other.lower().endswith(".pdf"):
            src = pikepdf.open(other)
        else:
            src = pikepdf.open(io.BytesIO(images_to_pdf_bytes([other])))
        with src:
            pages = [src.pages[i] for i in (indices if indices is not None else range(len(src.pages)))]
            for k, p in enumerate(pages):
                pdf.pages.insert(at + k, p)
            _save(pdf, out)
    return out


def insert_blank(path: str, at: int, out: str, size: tuple | None = None) -> str:
    with pikepdf.open(path, allow_overwriting_input=True) as pdf:
        if size is None:
            mb = pdf.pages[min(at, len(pdf.pages) - 1)].mediabox
            size = (float(mb[2]) - float(mb[0]), float(mb[3]) - float(mb[1]))
        pdf.add_blank_page(page_size=size)
        blank = pdf.pages[-1]
        del pdf.pages[-1]
        pdf.pages.insert(at, blank)
        _save(pdf, out)
    return out


def crop(path: str, indices: Iterable[int], margins: tuple[float, float, float, float], out: str) -> str:
    """margins: sol, alt, sağ, üst (punto)."""
    l, b, r, t = margins
    with pikepdf.open(path, allow_overwriting_input=True) as pdf:
        for i in indices:
            mb = [float(x) for x in pdf.pages[i].mediabox]
            pdf.pages[i].cropbox = pikepdf.Array([mb[0] + l, mb[1] + b, mb[2] - r, mb[3] - t])
        _save(pdf, out)
    return out


def new_blank(out: str, pages: int = 1, size=(595.28, 841.89)) -> str:
    pdf = pikepdf.new()
    for _ in range(pages):
        pdf.add_blank_page(page_size=size)
    _save(pdf, out)
    return out


def images_to_pdf_bytes(images: Sequence[str], dpi: int = 0) -> bytes:
    from reportlab.pdfgen import canvas
    from reportlab.lib.utils import ImageReader
    buf = io.BytesIO()
    c = canvas.Canvas(buf)
    for path in images:
        img = Image.open(path)
        frames = []
        try:
            i = 0
            while True:
                img.seek(i)
                frames.append(img.convert("RGB").copy())
                i += 1
        except EOFError:
            pass
        for fr in frames:
            res = dpi or (img.info.get("dpi", (150, 150))[0] or 150)
            w, h = fr.width * 72.0 / res, fr.height * 72.0 / res
            c.setPageSize((w, h))
            c.drawImage(ImageReader(fr), 0, 0, w, h)
            c.showPage()
    c.save()
    return buf.getvalue()


def images_to_pdf(images: Sequence[str], out: str, dpi: int = 0) -> str:
    with open(out, "wb") as f:
        f.write(images_to_pdf_bytes(images, dpi))
    return out


def detect_blank_pages(path: str, threshold: float = 0.995, password: str | None = None) -> list[int]:
    """Neredeyse tamamen beyaz olan sayfaların indekslerini döndürür."""
    from .document import PdfDocument
    blanks = []
    with PdfDocument(path, password) as doc:
        for i in range(doc.page_count):
            if doc.page_text(i).strip():
                continue
            img = doc.render(i, scale=0.5).convert("L")
            hist = img.histogram()
            white = sum(hist[230:])
            if white / (img.width * img.height) >= threshold:
                blanks.append(i)
    return blanks


def page_labels_renumber(path: str, out: str, start: int = 1, style: str = "D", prefix: str = "") -> str:
    """Sayfa etiketlerini ayarlar (D=1,2,3  r=i,ii  R=I,II  a/A harf)."""
    with pikepdf.open(path, allow_overwriting_input=True) as pdf:
        d = pikepdf.Dictionary(S=pikepdf.Name("/" + style), St=start)
        if prefix:
            d.P = pikepdf.String(prefix)
        pdf.Root.PageLabels = pikepdf.Dictionary(Nums=pikepdf.Array([0, d]))
        _save(pdf, out)
    return out


def _copy_info(src: pikepdf.Pdf, dst: pikepdf.Pdf):
    try:
        with src.open_metadata() as m1, dst.open_metadata() as m2:
            for k, v in m1.items():
                m2[k] = v
    except Exception:
        pass
