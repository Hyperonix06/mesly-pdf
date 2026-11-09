"""PDF görüntüleme, metin çıkarma ve arama (PDFium tabanlı)."""
from __future__ import annotations

import os
from dataclasses import dataclass, field

import pypdfium2 as pdfium
from PIL import Image


@dataclass
class SearchHit:
    page: int
    text: str
    rects: list = field(default_factory=list)  # PDF koordinatları (l, b, r, t)


@dataclass
class TocItem:
    title: str
    page: int
    level: int


class PdfDocument:
    """Salt okunur PDF belgesi; görüntüleme ve metin işlemleri için."""

    def __init__(self, path: str, password: str | None = None):
        self.path = os.path.abspath(path)
        self.password = password
        self._doc = pdfium.PdfDocument(self.path, password=password)
        try:
            self._doc.init_forms()  # form alanları ve imza görünümleri çizilsin
        except Exception:
            pass

    # -- temel bilgiler -------------------------------------------------
    def __len__(self):
        return len(self._doc)

    @property
    def page_count(self) -> int:
        return len(self._doc)

    def page_size(self, index: int) -> tuple[float, float]:
        return tuple(self._doc.get_page_size(index))

    def metadata(self) -> dict:
        try:
            return {k: v for k, v in self._doc.get_metadata_dict().items() if v}
        except Exception:
            return {}

    def toc(self) -> list[TocItem]:
        items = []
        try:
            for b in self._doc.get_toc():
                idx = b.get_dest().get_index() if b.get_dest() else None
                if idx is not None:
                    items.append(TocItem(b.get_title(), idx, b.level))
        except Exception:
            pass
        return items

    def rotation(self, index: int) -> int:
        page = self._doc[index]
        try:
            return page.get_rotation()
        finally:
            page.close()

    def device_to_page(self, index: int, w: float, h: float, dx: float, dy: float) -> tuple[float, float]:
        """Görsel (döndürülmüş) sayfa üzerindeki noktayı (w x h boyutunda) PDF koordinatına çevirir."""
        import ctypes
        import pypdfium2.raw as c
        page = self._doc[index]
        try:
            px, py = ctypes.c_double(), ctypes.c_double()
            k = 16  # alt piksel hassasiyeti
            c.FPDF_DeviceToPage(page.raw, 0, 0, int(w * k), int(h * k), 0, int(dx * k), int(dy * k), px, py)
            return px.value, py.value
        finally:
            page.close()

    def page_to_device(self, index: int, w: float, h: float, x: float, y: float) -> tuple[float, float]:
        import ctypes
        import pypdfium2.raw as c
        page = self._doc[index]
        try:
            dx, dy = ctypes.c_int(), ctypes.c_int()
            k = 16
            c.FPDF_PageToDevice(page.raw, 0, 0, int(w * k), int(h * k), 0, float(x), float(y), dx, dy)
            return dx.value / k, dy.value / k
        finally:
            page.close()

    def rect_to_device(self, index: int, w: float, h: float, rect) -> tuple[float, float, float, float]:
        l, b, r, t = rect
        x1, y1 = self.page_to_device(index, w, h, l, b)
        x2, y2 = self.page_to_device(index, w, h, r, t)
        return min(x1, x2), min(y1, y2), max(x1, x2), max(y1, y2)

    def device_rect_to_page(self, index: int, w: float, h: float, x0, y0, x1, y1) -> tuple[float, float, float, float]:
        a = self.device_to_page(index, w, h, x0, y0)
        b = self.device_to_page(index, w, h, x1, y1)
        return min(a[0], b[0]), min(a[1], b[1]), max(a[0], b[0]), max(a[1], b[1])

    # -- görüntüleme -----------------------------------------------------
    def render(self, index: int, scale: float = 1.0, rotation: int = 0) -> Image.Image:
        page = self._doc[index]
        try:
            bmp = page.render(scale=scale, rotation=rotation, may_draw_forms=True)
            return bmp.to_pil().convert("RGB")
        finally:
            page.close()

    def render_dpi(self, index: int, dpi: int = 300) -> Image.Image:
        return self.render(index, scale=dpi / 72.0)

    # -- metin -----------------------------------------------------------
    def page_text(self, index: int) -> str:
        page = self._doc[index]
        tp = page.get_textpage()
        try:
            return tp.get_text_range() or ""
        finally:
            tp.close()
            page.close()

    def all_text(self) -> list[str]:
        return [self.page_text(i) for i in range(self.page_count)]

    def has_text_layer(self, min_chars: int = 20) -> bool:
        total = 0
        for i in range(min(self.page_count, 5)):
            total += len(self.page_text(i).strip())
            if total >= min_chars:
                return True
        return False

    def words(self, index: int) -> list[tuple[str, tuple]]:
        """Kelime + sınırlayıcı kutu (PDF koordinatları)."""
        page = self._doc[index]
        tp = page.get_textpage()
        out = []
        try:
            n = tp.count_chars()
            cur, box = "", None
            for i in range(n):
                ch = tp.get_text_range(i, 1)
                if ch.strip() == "":
                    if cur:
                        out.append((cur, box))
                    cur, box = "", None
                    continue
                l, b, r, t = tp.get_charbox(i)
                cur += ch
                box = (l, b, r, t) if box is None else (min(box[0], l), min(box[1], b), max(box[2], r), max(box[3], t))
            if cur:
                out.append((cur, box))
        finally:
            tp.close()
            page.close()
        return out

    def search(self, query: str, match_case: bool = False, whole_word: bool = False,
               pages: list[int] | None = None) -> list[SearchHit]:
        hits: list[SearchHit] = []
        if not query:
            return hits
        for i in pages if pages is not None else range(self.page_count):
            page = self._doc[i]
            tp = page.get_textpage()
            try:
                searcher = tp.search(query, match_case=match_case, match_whole_word=whole_word)
                while True:
                    occ = searcher.get_next()
                    if not occ:
                        break
                    start, count = occ
                    nrect = tp.count_rects(start, count)
                    rects = [tp.get_rect(k) for k in range(nrect)]
                    hits.append(SearchHit(i, tp.get_text_range(start, count), rects))
                searcher.close()
            finally:
                tp.close()
                page.close()
        return hits

    def close(self):
        try:
            self._doc.close()
        except Exception:
            pass

    def __enter__(self):
        return self

    def __exit__(self, *a):
        self.close()


def is_encrypted(path: str) -> bool:
    import pikepdf
    try:
        with pikepdf.open(path):
            return False
    except pikepdf.PasswordError:
        return True
