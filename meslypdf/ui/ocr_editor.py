"""OCR Düzenleyici: sayfa görüntüleri, tanıma alanları (metin/tablo/resim/barkod), metin düzenleyici,
doğrulama, görüntü ön işleme, OCR projeleri ve çok formatlı kaydetme."""
from __future__ import annotations

import io
import json
import os
import zipfile

from PySide6.QtCore import Qt, QRectF, QPointF, QSize, Signal, QTimer
from PySide6.QtGui import QPen, QBrush, QColor, QIcon, QPainter, QTextCharFormat, QTextCursor, QKeySequence, QShortcut
from PySide6.QtWidgets import (QWidget, QVBoxLayout, QHBoxLayout, QSplitter, QListWidget, QListWidgetItem, QGraphicsView,
                               QGraphicsScene, QGraphicsRectItem, QGraphicsPixmapItem, QToolButton, QLabel, QScrollArea,
                               QPlainTextEdit, QTableWidget, QTableWidgetItem, QFrame, QDialog, QCheckBox, QSlider,
                               QPushButton, QMenu, QMessageBox, QLineEdit, QGraphicsItem, QGridLayout, QComboBox, QSpinBox,
                               QHeaderView, QButtonGroup)
from PIL import Image

from ..core import ocr as ocrmod, convert
from ..core.document import PdfDocument
from ..core.ocr import PageResult, Block, Line, Word
from ..core.preprocess import PreprocessOptions, preprocess
from . import theme
from .common import (run_task, toast, Settings, button, label, open_files, save_file, LangPicker, confirm, open_path,
                     ALL_SUPPORTED)
from .pageview import pil_to_qpixmap

AREA_COLORS = {"text": "#2E7D32", "table": "#1565C0", "picture": "#C62828", "barcode": "#6A1B9A"}
AREA_TR = {"text": "Metin", "table": "Tablo", "picture": "Resim", "barcode": "Barkod"}


class OcrPage:
    def __init__(self, image: Image.Image, source: str, dpi: int):
        self.original = image
        self.image = image
        self.source = source
        self.dpi = dpi
        self.result: PageResult | None = None
        self.areas: list[dict] | None = None  # kullanıcı tarafından düzenlenmiş alanlar
        self.info = {}

    @property
    def status(self):
        if not self.result:
            return "Tanınmadı"
        low = len(self.result.low_confidence())
        return "Tanındı" + (f" · {low} şüpheli" if low else "")


class AreaItem(QGraphicsRectItem):
    def __init__(self, kind, rect: QRectF, editor):
        super().__init__(rect)
        self.kind = kind
        self.editor = editor
        col = QColor(AREA_COLORS[kind])
        self.setPen(QPen(col, 2))
        fill = QColor(col)
        fill.setAlpha(28)
        self.setBrush(QBrush(fill))
        self.setFlags(QGraphicsItem.ItemIsSelectable | QGraphicsItem.ItemIsMovable | QGraphicsItem.ItemSendsGeometryChanges)
        self.setZValue(2)

    def itemChange(self, change, value):
        if change == QGraphicsItem.ItemPositionHasChanged:
            self.editor.areas_changed()
        if change == QGraphicsItem.ItemSelectedHasChanged and value:
            self.editor.area_selected(self)
        return super().itemChange(change, value)

    def scene_box(self):
        r = self.mapRectToScene(self.rect())
        return (int(r.left()), int(r.top()), int(r.right()), int(r.bottom()))


class ImageView(QGraphicsView):
    areaDrawn = Signal(QRectF)

    def __init__(self):
        super().__init__()
        self.setScene(QGraphicsScene(self))
        self.setRenderHints(QPainter.Antialiasing | QPainter.SmoothPixmapTransform)
        self.setBackgroundBrush(QColor(theme.C["canvas"]))
        self.setDragMode(QGraphicsView.RubberBandDrag)
        self.pix = None
        self.draw_kind = None
        self._start = None
        self._band = None

    def set_image(self, img):
        self.scene().clear()
        self.pix = QGraphicsPixmapItem(pil_to_qpixmap(img))
        self.pix.setTransformationMode(Qt.SmoothTransformation)
        self.scene().addItem(self.pix)
        self.scene().setSceneRect(self.pix.boundingRect())
        self.fit()

    def fit(self):
        if self.pix:
            self.fitInView(self.pix, Qt.KeepAspectRatio)

    def wheelEvent(self, e):
        if e.modifiers() & Qt.ControlModifier:
            f = 1.15 if e.angleDelta().y() > 0 else 1 / 1.15
            self.scale(f, f)
            return
        super().wheelEvent(e)

    def mousePressEvent(self, e):
        if self.draw_kind and e.button() == Qt.LeftButton:
            self._start = self.mapToScene(e.position().toPoint())
            self._band = QGraphicsRectItem(QRectF(self._start, self._start))
            self._band.setPen(QPen(QColor(AREA_COLORS[self.draw_kind]), 2, Qt.DashLine))
            self.scene().addItem(self._band)
            return
        super().mousePressEvent(e)

    def mouseMoveEvent(self, e):
        if self._band:
            self._band.setRect(QRectF(self._start, self.mapToScene(e.position().toPoint())).normalized())
            return
        super().mouseMoveEvent(e)

    def mouseReleaseEvent(self, e):
        if self._band:
            r = self._band.rect()
            self.scene().removeItem(self._band)
            self._band = None
            if r.width() > 8 and r.height() > 8:
                self.areaDrawn.emit(r.intersected(self.scene().sceneRect()))
            return
        super().mouseReleaseEvent(e)


class BlockCard(QFrame):
    changed = Signal()

    def __init__(self, block: Block, idx: int, low_conf=60):
        super().__init__()
        self.block = block
        self.setObjectName("Card")
        v = QVBoxLayout(self)
        v.setContentsMargins(10, 8, 10, 10)
        hdr = QHBoxLayout()
        dot = QLabel("●")
        dot.setStyleSheet(f"color:{AREA_COLORS[block.kind]}; font-size: 14px;")
        hdr.addWidget(dot)
        hdr.addWidget(label(f"{idx + 1}. {AREA_TR[block.kind]}" + (" · başlık" if block.heading else ""), "H3"))
        hdr.addStretch()
        v.addLayout(hdr)
        if block.kind == "text":
            self.edit = QPlainTextEdit()
            self.edit.setPlainText("\n".join(l.text for l in block.lines))
            self._mark_low(low_conf)
            self.edit.setMinimumHeight(min(260, 26 + 20 * max(1, len(block.lines))))
            self.edit.textChanged.connect(self._text_changed)
            v.addWidget(self.edit)
        elif block.kind == "table":
            rows = len(block.cells)
            cols = max((len(r) for r in block.cells), default=0)
            self.table = QTableWidget(rows, cols)
            self.table.horizontalHeader().setVisible(False)
            self.table.verticalHeader().setVisible(False)
            self.table.horizontalHeader().setSectionResizeMode(QHeaderView.Stretch)
            for r, row in enumerate(block.cells):
                for c, cell in enumerate(row):
                    self.table.setItem(r, c, QTableWidgetItem(cell["text"]))
            self.table.setMinimumHeight(min(320, 30 + 28 * rows))
            self.table.itemChanged.connect(self._cell_changed)
            v.addWidget(self.table)
        elif block.kind == "barcode":
            self.edit = QLineEdit(block.value)
            self.edit.textChanged.connect(lambda t: (setattr(block, "value", t), self.changed.emit()))
            v.addWidget(self.edit)
        else:
            v.addWidget(label("Resim olarak korunacak", "Muted"))

    def _mark_low(self, thr):
        doc = self.edit.document()
        fmt = QTextCharFormat()
        fmt.setBackground(QColor("#FFF59D"))
        fmt.setToolTip("Düşük güvenle tanındı — kontrol edin")
        cur = QTextCursor(doc)
        pos = 0
        for li, line in enumerate(self.block.lines):
            for w in line.words:
                start = pos
                pos += len(w.text)
                if w.conf < thr:
                    cur.setPosition(start)
                    cur.setPosition(pos, QTextCursor.KeepAnchor)
                    cur.mergeCharFormat(fmt)
                pos += 1  # boşluk / satır sonu

    def _text_changed(self):
        text = self.edit.toPlainText()
        old = self.block.lines
        new_lines = []
        rows = text.split("\n")
        for k, t in enumerate(rows):
            box = old[min(k, len(old) - 1)].box if old else self.block.box
            words = t.split()
            if not words:
                continue
            # kelime kutularını satır kutusuna orantılı dağıt
            x0, y0, x1, y1 = box
            total = sum(len(w) + 1 for w in words)
            cur = x0
            wl = []
            for w in words:
                ww = (x1 - x0) * (len(w) + 1) / total
                wl.append(Word(w, (cur, y0, cur + ww * len(w) / (len(w) + 1), y1), 100.0))
                cur += ww
            new_lines.append(Line(wl, box))
        self.block.lines = new_lines
        self.changed.emit()

    def _cell_changed(self, item):
        cell = self.block.cells[item.row()][item.column()]
        cell["text"] = item.text()
        x0, y0, x1, y1 = cell["box"]
        cell["words"] = [Word(item.text(), (x0 + 3, y0 + 3, x1 - 3, y1 - 3), 100.0)] if item.text() else []
        self.changed.emit()


class OcrEditor(QWidget):
    titleChanged = Signal()

    def __init__(self, main, sources: list[str] | None = None, name: str = "", project: str | None = None):
        super().__init__()
        self.main = main
        self.pages: list[OcrPage] = []
        self.cur = -1
        self.name = name or (os.path.basename(sources[0]) if sources else "Yeni OCR projesi")
        self.project_path = project
        self.dirty = False
        self._build()
        if project:
            self.load_project(project)
        elif sources:
            self.add_sources(sources)

    @property
    def title(self):
        return "OCR · " + self.name + (" •" if self.dirty else "")

    def _build(self):
        v = QVBoxLayout(self)
        v.setContentsMargins(0, 0, 0, 0)
        v.setSpacing(0)
        bar = QWidget()
        bar.setObjectName("Ribbon")
        bar.setAttribute(Qt.WA_StyledBackground, True)
        h = QHBoxLayout(bar)
        h.setContentsMargins(10, 6, 10, 6)
        h.setSpacing(4)

        def tb(text, ic, fn, tip="", checkable=False, color=None):
            b = QToolButton()
            b.setObjectName("RibbonButton")
            b.setText(text)
            b.setIcon(theme.icon(ic, color))
            b.setIconSize(QSize(22, 22))
            b.setToolButtonStyle(Qt.ToolButtonTextUnderIcon)
            b.setToolTip(tip or text)
            b.setCheckable(checkable)
            b.setMinimumWidth(max(60, b.fontMetrics().horizontalAdvance(text) + 22))
            if fn:
                b.clicked.connect(fn)
            h.addWidget(b)
            return b

        def sep():
            f = QFrame()
            f.setObjectName("RibbonSep")
            h.addWidget(f)
        tb("Ekle", "mdi6.file-plus-outline", self.add_files, "Dosya veya görüntü ekle")
        tb("Tara", "mdi6.scanner", self.scan)
        sep()
        self.lang = LangPicker()
        h.addWidget(self.lang)
        tb("Tümünü tanı", "mdi6.text-recognition", self.recognize_all, color=theme.C["primary"])
        tb("Sayfayı tanı", "mdi6.file-document-outline", self.recognize_current)
        tb("Görüntü", "mdi6.image-edit-outline", self.image_editor, "Görüntü düzenleyici: eğiklik, yön, arka plan, kırpma")
        sep()
        self.area_group = QButtonGroup(self)
        self.area_group.setExclusive(False)
        for kind, ic in (("text", "mdi6.format-text"), ("table", "mdi6.table"), ("picture", "mdi6.image-outline"), ("barcode", "mdi6.barcode")):
            b = tb(f"{AREA_TR[kind]} alanı", ic, None, f"{AREA_TR[kind]} alanı çiz", True, AREA_COLORS[kind])
            b.clicked.connect(lambda checked, k=kind, b=b: self._area_mode(k if checked else None, b))
            self.area_group.addButton(b)
        tb("Alanı sil", "mdi6.selection-remove", self.delete_area)
        tb("Alanları temizle", "mdi6.selection-off", self.clear_areas)
        sep()
        tb("Doğrula", "mdi6.spellcheck", self.verify, "Şüpheli karakterleri tek tek kontrol et")
        tb("Sözlük", "mdi6.book-alphabet", self.edit_dictionary, "Kullanıcı sözlüğü ve düzeltme hafızası")
        sep()
        save_menu = [(convert.EXPORT_FORMATS[k], k) for k in ("docx", "xlsx", "pptx", "pdf", "pdfa", "odt", "rtf", "txt", "csv", "html", "epub", "fb2")]
        b = tb("Kaydet", "mdi6.content-save-outline", None, color=theme.C["primary"])
        m = QMenu(b)
        for t, k in save_menu:
            m.addAction(t, lambda k=k: self.save_as(k))
        m.addSeparator()
        m.addAction(theme.icon("mdi6.content-save-cog-outline"), "OCR projesini kaydet…", self.save_project)
        b.setMenu(m)
        b.setPopupMode(QToolButton.InstantPopup)
        tb("Word'de aç", "mdi6.file-word-outline", lambda: self.save_as("docx", open_after=True), color="#2B579A")
        h.addStretch()
        v.addWidget(bar)

        split = QSplitter(Qt.Horizontal)
        left = QWidget()
        left.setObjectName("Sidebar")
        left.setAttribute(Qt.WA_StyledBackground, True)
        lv = QVBoxLayout(left)
        lv.setContentsMargins(6, 8, 6, 6)
        lv.addWidget(label("Sayfalar", "H3"))
        self.page_list = QListWidget()
        self.page_list.setIconSize(QSize(110, 140))
        self.page_list.setViewMode(QListWidget.IconMode)
        self.page_list.setFlow(QListWidget.TopToBottom)
        self.page_list.setWrapping(False)
        self.page_list.setMovement(QListWidget.Static)
        self.page_list.setSpacing(4)
        self.page_list.setStyleSheet("QListWidget { border: none; }")
        self.page_list.currentRowChanged.connect(self.show_page)
        self.page_list.setContextMenuPolicy(Qt.CustomContextMenu)
        self.page_list.customContextMenuRequested.connect(self._page_menu)
        lv.addWidget(self.page_list)
        split.addWidget(left)

        mid = QWidget()
        mv = QVBoxLayout(mid)
        mv.setContentsMargins(0, 0, 0, 0)
        self.view = ImageView()
        self.view.areaDrawn.connect(self._area_drawn)
        mv.addWidget(self.view)
        self.img_info = label("", "Small")
        self.img_info.setContentsMargins(10, 3, 10, 3)
        mv.addWidget(self.img_info)
        split.addWidget(mid)

        right = QWidget()
        right.setObjectName("RightPanel")
        right.setAttribute(Qt.WA_StyledBackground, True)
        rv = QVBoxLayout(right)
        rv.setContentsMargins(8, 8, 8, 8)
        rh = QHBoxLayout()
        rh.addWidget(label("Tanınan metin", "H3"))
        rh.addStretch()
        self.stats = label("", "Badge")
        rh.addWidget(self.stats)
        rv.addLayout(rh)
        self.scroll = QScrollArea()
        self.scroll.setWidgetResizable(True)
        self.cards_host = QWidget()
        self.cards = QVBoxLayout(self.cards_host)
        self.cards.setSpacing(8)
        self.cards.addStretch()
        self.scroll.setWidget(self.cards_host)
        rv.addWidget(self.scroll)
        split.addWidget(right)
        split.setSizes([170, 700, 460])
        v.addWidget(split, 1)
        QShortcut(QKeySequence("Delete"), self, self.delete_area)

    # ------------------------------------------------------------ kaynaklar
    def add_files(self):
        fs = open_files(self, "Sayfa ekle", ALL_SUPPORTED)
        if fs:
            self.add_sources(fs)

    def scan(self):
        from .scanner import acquire_images
        imgs = acquire_images(self)
        if imgs:
            self.add_sources(imgs)

    def add_sources(self, sources):
        dpi = int(Settings.get("dpi") or 300)

        def load(progress=None):
            pages = []
            for n, s in enumerate(sources):
                if progress:
                    progress(n, len(sources))
                ext = os.path.splitext(s)[1].lower()
                if ext == ".pdf":
                    with PdfDocument(s) as d:
                        for i in range(d.page_count):
                            pages.append(OcrPage(d.render_dpi(i, dpi), f"{os.path.basename(s)} s.{i + 1}", dpi))
                elif ext in convert.IMAGE_EXT:
                    im = Image.open(s)
                    fdpi = int((im.info.get("dpi") or (dpi, dpi))[0] or dpi)
                    k = 0
                    try:
                        while True:
                            im.seek(k)
                            pages.append(OcrPage(im.convert("RGB").copy(), os.path.basename(s), fdpi))
                            k += 1
                    except EOFError:
                        pass
                else:
                    import tempfile
                    tmp = tempfile.mktemp(suffix=".pdf")
                    convert.any_to_pdf([s], tmp)
                    with PdfDocument(tmp) as d:
                        for i in range(d.page_count):
                            pages.append(OcrPage(d.render_dpi(i, dpi), f"{os.path.basename(s)} s.{i + 1}", dpi))
            return pages

        def done(pages):
            start = len(self.pages)
            self.pages += pages
            self._refresh_list()
            self.page_list.setCurrentRow(start)
            self.dirty = True
            self.titleChanged.emit()
            if Settings.get("auto_recognize", True) and pages:
                self.recognize_all(only_new=True)
        run_task(self, "Sayfalar yükleniyor…", load, on_done=done)

    def _refresh_list(self):
        cur = self.page_list.currentRow()
        self.page_list.blockSignals(True)
        self.page_list.clear()
        for i, p in enumerate(self.pages):
            th = p.image.copy()
            th.thumbnail((180, 240))
            it = QListWidgetItem(QIcon(pil_to_qpixmap(th)), f"{i + 1} · {p.status}")
            it.setSizeHint(QSize(150, 180))
            it.setTextAlignment(Qt.AlignHCenter)
            ic = "mdi6.check-circle" if p.result and not p.result.low_confidence() else ("mdi6.alert-circle-outline" if p.result else None)
            it.setToolTip(p.source)
            self.page_list.addItem(it)
        self.page_list.blockSignals(False)
        if 0 <= cur < len(self.pages):
            self.page_list.setCurrentRow(cur)

    def _page_menu(self, pos):
        it = self.page_list.itemAt(pos)
        if not it:
            return
        i = self.page_list.row(it)
        m = QMenu(self)
        m.addAction(theme.icon("mdi6.rotate-right"), "Sağa döndür", lambda: self._rotate(i, -90))
        m.addAction(theme.icon("mdi6.rotate-left"), "Sola döndür", lambda: self._rotate(i, 90))
        m.addAction(theme.icon("mdi6.delete-outline"), "Sayfayı sil", lambda: self._delete_page(i))
        m.addAction(theme.icon("mdi6.restore"), "Orijinal görüntüye dön", lambda: self._restore(i))
        m.exec(self.page_list.mapToGlobal(pos))

    def _rotate(self, i, ang):
        p = self.pages[i]
        p.image = p.image.rotate(ang, expand=True)
        p.result = None
        p.areas = None
        self._refresh_list()
        self.show_page(i)

    def _delete_page(self, i):
        del self.pages[i]
        self._refresh_list()
        self.show_page(min(i, len(self.pages) - 1))

    def _restore(self, i):
        p = self.pages[i]
        p.image, p.result, p.areas = p.original, None, None
        self._refresh_list()
        self.show_page(i)

    # ------------------------------------------------------------ gösterim
    def show_page(self, i):
        self.cur = i
        while self.cards.count() > 1:
            w = self.cards.takeAt(0).widget()
            if w:
                w.deleteLater()
        if i < 0 or i >= len(self.pages):
            self.view.scene().clear()
            self.stats.setText("")
            return
        p = self.pages[i]
        self.view.set_image(p.image)
        self.img_info.setText(f"{p.source} · {p.image.width}×{p.image.height} px · {p.dpi} dpi"
                              + (f" · eğiklik {p.info.get('skew', 0)}°" if p.info.get("skew") else ""))
        self._draw_areas()
        self._fill_cards()

    def _draw_areas(self):
        p = self.pages[self.cur]
        areas = p.areas if p.areas is not None else ([{"kind": b.kind, "box": b.box} for b in p.result.blocks] if p.result else [])
        for a in areas:
            x0, y0, x1, y1 = a["box"]
            it = AreaItem(a["kind"], QRectF(x0, y0, x1 - x0, y1 - y0), self)
            self.view.scene().addItem(it)

    def _fill_cards(self):
        p = self.pages[self.cur]
        if not p.result:
            self.cards.insertWidget(0, label("Bu sayfa henüz tanınmadı. 'Sayfayı tanı'ya basın veya önce alanları çizin.", "Muted", True))
            self.stats.setText("")
            return
        low = len(p.result.low_confidence())
        words = sum(1 for _ in p.result.words())
        self.stats.setText(f"{words} kelime · {low} şüpheli · {ocrmod.LANG_NAMES.get(p.result.lang.split('+')[0], p.result.lang)}")
        for k, b in enumerate(p.result.blocks):
            card = BlockCard(b, k)
            card.changed.connect(self._mark_dirty)
            self.cards.insertWidget(self.cards.count() - 1, card)

    def _mark_dirty(self):
        if not self.dirty:
            self.dirty = True
            self.titleChanged.emit()

    # ------------------------------------------------------------ alanlar
    def _area_mode(self, kind, btn):
        for b in self.area_group.buttons():
            if b is not btn:
                b.setChecked(False)
        self.view.draw_kind = kind
        self.view.setDragMode(QGraphicsView.NoDrag if kind else QGraphicsView.RubberBandDrag)
        self.view.viewport().setCursor(Qt.CrossCursor if kind else Qt.ArrowCursor)

    def _area_drawn(self, r: QRectF):
        if self.cur < 0:
            return
        it = AreaItem(self.view.draw_kind, r, self)
        self.view.scene().addItem(it)
        self.areas_changed()

    def areas_changed(self):
        if self.cur < 0:
            return
        items = [i for i in self.view.scene().items() if isinstance(i, AreaItem)]
        self.pages[self.cur].areas = [{"kind": i.kind, "box": i.scene_box()} for i in reversed(items)]
        self._mark_dirty()

    def area_selected(self, item):
        pass

    def delete_area(self):
        for it in self.view.scene().selectedItems():
            if isinstance(it, AreaItem):
                self.view.scene().removeItem(it)
        self.areas_changed()

    def clear_areas(self):
        for it in [i for i in self.view.scene().items() if isinstance(i, AreaItem)]:
            self.view.scene().removeItem(it)
        if self.cur >= 0:
            self.pages[self.cur].areas = []

    # ------------------------------------------------------------ tanıma
    def _recognize(self, idxs, progress=None, cancel=None):
        lang = self.lang.value()
        out = {}
        for n, i in enumerate(idxs):
            if cancel and cancel():
                raise InterruptedError
            if progress:
                progress(n, len(idxs))
            p = self.pages[i]
            areas = p.areas if p.areas else None
            out[i] = ocrmod.recognize_image(p.image, lang, p.dpi, areas=areas)
        if progress:
            progress(len(idxs), len(idxs))
        return out

    def _apply_results(self, res):
        for i, r in res.items():
            self.pages[i].result = r
            self.pages[i].areas = None
        self._refresh_list()
        self.show_page(self.cur)
        self._mark_dirty()
        lows = sum(len(r.low_confidence()) for r in res.values())
        toast(self, f"{len(res)} sayfa tanındı" + (f" · {lows} şüpheli kelime (Doğrula ile kontrol edin)" if lows else ""), "ok")

    def recognize_all(self, only_new=False):
        if not self.pages:
            return
        if not ocrmod.tesseract_ok():
            QMessageBox.critical(self, "OCR motoru bulunamadı", "Tesseract OCR motoru bulunamadı. Kurulumu onarın veya Ayarlar'dan yolunu belirtin.")
            return
        idxs = [i for i, p in enumerate(self.pages) if not (only_new and p.result)]
        if not only_new and any(p.result for p in self.pages) and self.dirty:
            if not confirm(self, "Yeniden tanı", "Tüm sayfalar yeniden tanınacak; yaptığınız metin düzeltmeleri kaybolacak.\n"
                                                 "Yalnızca tanınmamış sayfaları tanımak için 'Hayır'ı seçin. Tümü yeniden tanınsın mı?"):
                idxs = [i for i, p in enumerate(self.pages) if not p.result]
                if not idxs:
                    return
        if Settings.get("auto_preprocess", True):
            opts = PreprocessOptions(**Settings.get("preprocess"))
            for i in idxs:
                p = self.pages[i]
                if not p.info.get("preprocessed"):
                    try:
                        imgs, info = preprocess(p.image, opts, p.dpi)
                        p.image = imgs[0]
                        p.info = dict(info, preprocessed=True)
                    except Exception:
                        pass
        run_task(self, "Metin tanınıyor…", self._recognize, idxs, on_done=self._apply_results)

    def recognize_current(self):
        if self.cur >= 0:
            run_task(self, "Sayfa tanınıyor…", self._recognize, [self.cur], on_done=self._apply_results)

    # ------------------------------------------------------------ görüntü düzenleyici
    def image_editor(self):
        if self.cur < 0:
            return
        d = ImageEditorDialog(self, self.pages[self.cur])
        if d.exec():
            targets = range(len(self.pages)) if d.all_pages.isChecked() else [self.cur]
            opts = d.options()

            def work(progress=None):
                out = {}
                for n, i in enumerate(targets):
                    if progress:
                        progress(n, len(targets))
                    p = self.pages[i]
                    imgs, info = preprocess(p.original if d.from_original.isChecked() else p.image, opts, p.dpi)
                    out[i] = (imgs, info)
                return out

            def done(out):
                for i in sorted(out, reverse=True):
                    imgs, info = out[i]
                    p = self.pages[i]
                    p.image, p.info, p.result, p.areas = imgs[0], dict(info, preprocessed=True), None, None
                    for extra in imgs[1:]:
                        np_ = OcrPage(extra, p.source + " (b)", p.dpi)
                        np_.info = p.info
                        self.pages.insert(i + 1, np_)
                self._refresh_list()
                self.show_page(self.cur)
                toast(self, "Görüntü iyileştirildi — yeniden tanıyın", "ok")
            run_task(self, "Görüntüler işleniyor…", work, on_done=done)

    # ------------------------------------------------------------ doğrulama
    def verify(self):
        items = []
        for pi, p in enumerate(self.pages):
            if p.result:
                for w in p.result.low_confidence():
                    items.append((pi, w))
        if not items:
            toast(self, "Şüpheli kelime yok", "ok")
            return
        VerifyDialog(self, items).exec()
        self.show_page(self.cur)
        self._refresh_list()

    def edit_dictionary(self):
        from .dialogs import TextDialog
        p = ocrmod.user_dict_path()
        cur = open(p, encoding="utf-8").read() if os.path.exists(p) else ""

        def save(t):
            open(p, "w", encoding="utf-8").write(t)
            toast(self, "Sözlük kaydedildi", "ok")
        TextDialog(self, "Kullanıcı sözlüğü (her satıra bir kelime: özel isimler, terimler)", cur,
                   actions=[("Kaydet", "mdi6.content-save-outline", save)]).exec()

    # ------------------------------------------------------------ kaydetme
    def _results(self):
        res = []
        for p in self.pages:
            if p.result:
                p.result.image = p.image
                res.append(p.result)
        return res

    def save_as(self, fmt, open_after=False):
        if any(not p.result for p in self.pages):
            if not confirm(self, "Tanınmamış sayfalar", "Bazı sayfalar henüz tanınmadı. Önce tümünü tanımak ister misiniz?"):
                pass
            else:
                return self.recognize_all()
        res = self._results()
        if not res:
            return
        ext = {"pdfa": "pdf", "pdfua": "pdf"}.get(fmt, fmt)
        out = save_file(self, "Kaydet", os.path.splitext(self.name)[0] + "." + ext, convert.EXPORT_FORMATS[fmt])
        if not out:
            return

        def work():
            if fmt in ("pdf", "pdfa"):
                import pikepdf
                dst = pikepdf.new()
                for r in res:
                    w, h = r.width * 72 / r.dpi, r.height * 72 / r.dpi
                    data = ocrmod.image_to_searchable_pdf_page(r.image, w, h, r.lang, r)
                    with pikepdf.open(io.BytesIO(data)) as s:
                        dst.pages.extend(s.pages)
                tmp = out + ".tmp.pdf"
                dst.save(tmp)
                if fmt == "pdfa":
                    convert.make_pdfa(tmp, out)
                    os.remove(tmp)
                else:
                    os.replace(tmp, out)
            else:
                w = convert.WRITERS[fmt]
                if fmt == "docx":
                    w(res, out, mode=Settings.get("export_mode"))
                elif fmt in ("epub", "fb2", "html"):
                    w(res, out, title=os.path.splitext(self.name)[0])
                else:
                    w(res, out)
            return out

        def done(o):
            toast(self, f"Kaydedildi: {os.path.basename(o)}", "ok")
            if open_after or fmt in ("pdf", "pdfa") and confirm(self, "Kaydedildi", "Belge açılsın mı?"):
                if fmt in ("pdf", "pdfa"):
                    self.main.open_document(o)
                else:
                    open_path(o)
        run_task(self, "Kaydediliyor…", work, on_done=done)

    def save_project(self):
        out = save_file(self, "OCR projesini kaydet", os.path.splitext(self.name)[0] + ".meslyocr", "Mesly OCR projesi (*.meslyocr)")
        if not out:
            return
        with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
            meta = {"name": self.name, "lang": self.lang.value(), "pages": []}
            for i, p in enumerate(self.pages):
                for key, im in (("image", p.image), ("original", p.original)):
                    b = io.BytesIO()
                    im.save(b, "PNG")
                    z.writestr(f"{i:04d}_{key}.png", b.getvalue())
                meta["pages"].append({"source": p.source, "dpi": p.dpi, "info": p.info, "areas": p.areas,
                                      "result": p.result.to_dict() if p.result else None})
            z.writestr("proje.json", json.dumps(meta, ensure_ascii=False))
        self.project_path = out
        self.dirty = False
        self.titleChanged.emit()
        toast(self, "Proje kaydedildi", "ok")

    def load_project(self, path):
        with zipfile.ZipFile(path) as z:
            meta = json.loads(z.read("proje.json"))
            self.name = meta.get("name", os.path.basename(path))
            self.lang.set_value(meta.get("lang", Settings.get("ocr_lang")))
            for i, pm in enumerate(meta["pages"]):
                img = Image.open(io.BytesIO(z.read(f"{i:04d}_image.png"))).convert("RGB")
                orig = Image.open(io.BytesIO(z.read(f"{i:04d}_original.png"))).convert("RGB")
                p = OcrPage(orig, pm["source"], pm["dpi"])
                p.image = img
                p.info = pm.get("info") or {}
                p.areas = pm.get("areas")
                if pm.get("result"):
                    p.result = PageResult.from_dict(pm["result"])
                self.pages.append(p)
        self._refresh_list()
        self.page_list.setCurrentRow(0)
        self.titleChanged.emit()

    def close_tab(self):
        if self.dirty and self.pages:
            r = QMessageBox.question(self, "OCR projesi", "Proje kaydedilmedi. Kapatmadan önce proje olarak kaydedilsin mi?",
                                     QMessageBox.Save | QMessageBox.Discard | QMessageBox.Cancel)
            if r == QMessageBox.Cancel:
                return False
            if r == QMessageBox.Save:
                self.save_project()
        return True


class ImageEditorDialog(QDialog):
    def __init__(self, parent, page: OcrPage):
        super().__init__(parent)
        self.setWindowTitle("Görüntü düzenleyici")
        self.resize(960, 640)
        self.page = page
        h = QHBoxLayout(self)
        left = QVBoxLayout()
        left.addWidget(label("Görüntü düzenleyici", "H2"))
        pre = Settings.get("preprocess")
        self.checks = {}
        for k, t in (("auto_orient", "Sayfa yönünü otomatik düzelt"), ("deskew", "Eğikliği düzelt"),
                     ("whiten_background", "Arka planı beyazlat (gölge/sararma)"), ("denoise", "Gürültüyü azalt"),
                     ("perspective", "Yamuk / perspektif düzelt (fotoğraflar)"), ("crop_borders", "Siyah kenarları kırp"),
                     ("binarize", "Siyah-beyaza çevir"), ("split_pages", "Çift sayfayı ikiye böl (kitap)"), ("invert", "Renkleri ters çevir")):
            c = QCheckBox(t)
            c.setChecked(pre.get(k, False))
            c.toggled.connect(self._schedule)
            left.addWidget(c)
            self.checks[k] = c
        g = QGridLayout()
        self.bright = QSlider(Qt.Horizontal)
        self.bright.setRange(50, 150)
        self.bright.setValue(100)
        self.contrast = QSlider(Qt.Horizontal)
        self.contrast.setRange(50, 200)
        self.contrast.setValue(100)
        for r, (t, s) in enumerate((("Parlaklık", self.bright), ("Karşıtlık", self.contrast))):
            g.addWidget(QLabel(t), r, 0)
            g.addWidget(s, r, 1)
            s.valueChanged.connect(self._schedule)
        left.addLayout(g)
        left.addSpacing(8)
        self.from_original = QCheckBox("Orijinal görüntüden başla")
        self.from_original.setChecked(True)
        self.all_pages = QCheckBox("Tüm sayfalara uygula")
        left.addWidget(self.from_original)
        left.addWidget(self.all_pages)
        left.addStretch()
        hb = QHBoxLayout()
        hb.addWidget(button("İptal", slot=self.reject))
        hb.addWidget(button("Uygula", "mdi6.check", obj="Primary", slot=self._accept_save))
        left.addLayout(hb)
        h.addLayout(left)
        self.preview = QLabel()
        self.preview.setAlignment(Qt.AlignCenter)
        self.preview.setMinimumWidth(560)
        self.preview.setStyleSheet(f"background:{theme.C['canvas']}; border-radius:10px;")
        h.addWidget(self.preview, 1)
        self.timer = QTimer(self, singleShot=True, interval=300, timeout=self._update)
        self.timer.start()

    def options(self):
        o = PreprocessOptions(**{k: c.isChecked() for k, c in self.checks.items()})
        o.brightness = self.bright.value() / 100
        o.contrast = self.contrast.value() / 100
        return o

    def _schedule(self, *_):
        self.timer.start()

    def _update(self):
        src = self.page.original.copy()
        src.thumbnail((1400, 1400))
        try:
            imgs, info = preprocess(src, self.options(), int(self.page.dpi * 1400 / max(self.page.original.size)))
            im = imgs[0]
            if len(imgs) > 1:
                w = sum(i.width for i in imgs) + 20
                canvas = Image.new("RGB", (w, max(i.height for i in imgs)), "#888888")
                x = 0
                for i in imgs:
                    canvas.paste(i, (x, 0))
                    x += i.width + 20
                im = canvas
            pm = pil_to_qpixmap(im).scaled(self.preview.size() * 0.96, Qt.KeepAspectRatio, Qt.SmoothTransformation)
            self.preview.setPixmap(pm)
            self.setWindowTitle(f"Görüntü düzenleyici — eğiklik {info.get('skew', 0)}°, döndürme {info.get('rotated', 0)}°")
        except Exception as e:
            self.preview.setText(str(e))

    def _accept_save(self):
        Settings.set("preprocess", {k: c.isChecked() for k, c in self.checks.items() if k != "invert"})
        self.accept()


class VerifyDialog(QDialog):
    def __init__(self, editor: OcrEditor, items):
        super().__init__(editor)
        self.setWindowTitle("Doğrulama")
        self.resize(560, 330)
        self.editor, self.items, self.k = editor, items, 0
        v = QVBoxLayout(self)
        self.title = label("", "H2")
        v.addWidget(self.title)
        self.img = QLabel()
        self.img.setAlignment(Qt.AlignCenter)
        self.img.setMinimumHeight(110)
        self.img.setStyleSheet(f"background:{theme.C['surface']}; border:1px solid {theme.C['border']}; border-radius:8px;")
        v.addWidget(self.img)
        self.edit = QLineEdit()
        f = self.edit.font()
        f.setPointSize(16)
        self.edit.setFont(f)
        self.edit.returnPressed.connect(self.replace)
        v.addWidget(self.edit)
        h = QHBoxLayout()
        h.addWidget(button("Atla", "mdi6.skip-next", slot=self.skip))
        h.addWidget(button("Sözlüğe ekle", "mdi6.book-plus-outline", slot=self.add_dict))
        h.addWidget(button("Her zaman düzelt", "mdi6.brain", slot=self.learn, tip="Bu hatayı hatırla ve sonraki tanımalarda otomatik düzelt"))
        h.addStretch()
        h.addWidget(button("Değiştir", "mdi6.check", obj="Primary", slot=self.replace))
        v.addLayout(h)
        h2 = QHBoxLayout()
        h2.addStretch()
        h2.addWidget(button("Kapat", slot=self.accept))
        v.addLayout(h2)
        self._show()

    def _show(self):
        if self.k >= len(self.items):
            toast(self.editor, "Doğrulama tamamlandı", "ok")
            self.accept()
            return
        pi, w = self.items[self.k]
        self.title.setText(f"Şüpheli kelime {self.k + 1} / {len(self.items)} · sayfa {pi + 1} · güven %{int(w.conf)}")
        img = self.editor.pages[pi].image
        x0, y0, x1, y1 = [int(v) for v in w.box]
        pad = max(10, (y1 - y0))
        crop = img.crop((max(0, x0 - pad * 4), max(0, y0 - pad), min(img.width, x1 + pad * 4), min(img.height, y1 + pad)))
        from PIL import ImageDraw
        d = ImageDraw.Draw(crop)
        ox, oy = max(0, x0 - pad * 4), max(0, y0 - pad)
        d.rectangle((x0 - ox - 2, y0 - oy - 2, x1 - ox + 2, y1 - oy + 2), outline="#E53935", width=2)
        pm = pil_to_qpixmap(crop)
        self.img.setPixmap(pm.scaled(520, 110, Qt.KeepAspectRatio, Qt.SmoothTransformation))
        self.edit.setText(w.text)
        self.edit.setFocus()
        self.edit.selectAll()

    def _cur(self):
        return self.items[self.k][1]

    def skip(self):
        self._cur().conf = 100.0
        self.k += 1
        self._show()

    def replace(self):
        w = self._cur()
        w.text = self.edit.text()
        w.conf = 100.0
        self.editor._mark_dirty()
        self.k += 1
        self._show()

    def add_dict(self):
        ocrmod.add_user_words([self.edit.text()])
        self.replace()

    def learn(self):
        old = self._cur().text
        new = self.edit.text()
        if old != new:
            ocrmod.learn_correction(old, new)
            for pi, w in self.items[self.k + 1:]:
                if w.text == old:
                    w.text, w.conf = new, 100.0
        self.replace()
