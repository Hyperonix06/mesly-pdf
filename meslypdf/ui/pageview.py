"""PDF sayfa görüntüleyici: sürekli/tek/iki sayfa düzeni, yakınlaştırma, tembel çizim,
araç modları (seçim, el, dikdörtgen çizme, tıklama, serbest çizim), arama vurguları."""
from __future__ import annotations

import threading

from PySide6.QtCore import Qt, Signal, QRectF, QPointF, QTimer, QThread
from PySide6.QtGui import QPixmap, QImage, QColor, QPen, QBrush, QPainterPath, QPainter, QCursor
from PySide6.QtWidgets import (QGraphicsView, QGraphicsScene, QGraphicsRectItem, QGraphicsPixmapItem,
                               QGraphicsPathItem, QGraphicsDropShadowEffect, QMenu, QApplication, QGraphicsSimpleTextItem)

from ..core.document import PdfDocument
from . import theme

GAP = 18
DPI_FACTOR = 96 / 72  # ekran pikseli / punto


def pil_to_qpixmap(img) -> QPixmap:
    img = img.convert("RGB")
    data = img.tobytes("raw", "RGB")
    qi = QImage(data, img.width, img.height, img.width * 3, QImage.Format_RGB888)
    return QPixmap.fromImage(qi.copy())


class Renderer(QThread):
    """Tek arka plan iş parçacığında sayfa çizimi. Görünüm istekleri küçük resim isteklerinden önceliklidir;
    güncel sayfaya en yakın istek önce işlenir. Sonuç QImage olarak ana iş parçacığına gönderilir."""
    rendered = Signal(str, int, int, float, QImage)  # tür, kuşak, sayfa, ölçek, görüntü

    def __init__(self, parent=None):
        super().__init__(parent)
        self._cv = threading.Condition()
        self._view: dict[int, float] = {}
        self._thumbs: list[tuple[int, float]] = []
        self._doc = None
        self._gen = 0
        self._focus = 0
        self._stop = False
        self.night = False

    def set_document(self, doc, gen):
        with self._cv:
            self._doc, self._gen = doc, gen
            self._view.clear()
            self._thumbs.clear()

    def request(self, page, scale, focus):
        with self._cv:
            self._view[page] = scale
            self._focus = focus
            self._cv.notify()

    def cancel_except(self, pages: set):
        with self._cv:
            for p in list(self._view):
                if p not in pages:
                    del self._view[p]

    def request_thumbs(self, items):
        with self._cv:
            self._thumbs = list(items)
            self._cv.notify()

    def stop(self):
        with self._cv:
            self._stop = True
            self._cv.notify()
        self.wait(3000)

    def run(self):
        while True:
            with self._cv:
                while not self._stop and not self._view and not self._thumbs:
                    self._cv.wait()
                if self._stop:
                    return
                doc, gen = self._doc, self._gen
                if self._view:
                    page = min(self._view, key=lambda p: abs(p - self._focus))
                    scale = self._view.pop(page)
                    kind = "view"
                else:
                    page, scale = self._thumbs.pop(0)
                    kind = "thumb"
            if doc is None:
                continue
            try:
                img = doc.render(page, scale=scale)
            except Exception:
                continue
            data = img.tobytes("raw", "RGB")
            qi = QImage(data, img.width, img.height, img.width * 3, QImage.Format_RGB888).copy()
            if self.night and kind == "view":
                qi.invertPixels()
            self.rendered.emit(kind, gen, page, scale, qi)


class PageItem(QGraphicsRectItem):
    def __init__(self, index: int, w_pt: float, h_pt: float):
        super().__init__()
        self.index = index
        self.w_pt, self.h_pt = w_pt, h_pt
        self.setBrush(QBrush(QColor("#FFFFFF")))
        self.setPen(QPen(QColor(0, 0, 0, 30), 0.6))
        self.pix = QGraphicsPixmapItem(self)
        self.pix.setTransformationMode(Qt.SmoothTransformation)
        self.rendered_scale = 0.0
        self.overlays: list = []

    def set_size(self, scale: float):
        self.setRect(0, 0, self.w_pt * scale, self.h_pt * scale)


class PageView(QGraphicsView):
    pageChanged = Signal(int)
    zoomChanged = Signal(float)
    rectDrawn = Signal(int, tuple)          # sayfa, PDF dikdörtgeni
    pointClicked = Signal(int, tuple)       # sayfa, PDF noktası
    inkDrawn = Signal(int, list)            # sayfa, [[(x,y)...]] PDF
    textSelected = Signal(int, tuple, str)  # sayfa, PDF dikdörtgeni, metin
    contextRequested = Signal(int, tuple, object)  # sayfa, PDF noktası, global konum
    layoutChanged = Signal()
    rangeSelected = Signal(int, list, str)          # sayfa, PDF dikdörtgenleri, metin (akış seçimi)
    annotationSelected = Signal(object)            # yorum sözlüğü veya None
    annotationMoved = Signal(int, object, tuple)   # sayfa, objgen, yeni PDF dikdörtgeni
    annotationActivated = Signal(object)           # çift tıklama
    annotationDeleteRequested = Signal(object)     # objgen
    linkActivated = Signal(object)                 # bağlantı sözlüğü
    selectionCleared = Signal()

    def __init__(self, parent=None):
        super().__init__(parent)
        self.setObjectName("PageView")
        self.setScene(QGraphicsScene(self))
        self.setRenderHints(QPainter.Antialiasing | QPainter.SmoothPixmapTransform)
        self.setViewportUpdateMode(QGraphicsView.SmartViewportUpdate)
        self.setTransformationAnchor(QGraphicsView.NoAnchor)
        self.setAlignment(Qt.AlignHCenter | Qt.AlignTop)
        self.doc: PdfDocument | None = None
        self.items_: list[PageItem] = []
        self.scale_ = 1.0 * DPI_FACTOR
        self.layout_mode = "continuous"  # continuous, single, facing
        self.fit_mode = "width"          # width, page, none
        self.tool = "select"
        self.tool_color = QColor(theme.C["primary"])
        self._current = 0
        self._drag_start = None
        self._drag_page = None
        self._band = None
        self._ink_path = None
        self._ink_points: list = []
        self._search_items = []
        self._sel_items = []
        self._render_timer = QTimer(self, singleShot=True, interval=15, timeout=self._render_visible)
        self.renderer = Renderer(self)
        self.renderer.rendered.connect(self._on_rendered, Qt.QueuedConnection)
        self.renderer.start()
        self._gen = 0
        self.verticalScrollBar().valueChanged.connect(self._on_scroll)
        self.horizontalScrollBar().valueChanged.connect(lambda *_: self._render_timer.start())
        self.night = False
        self.annot_provider = None   # sayfa -> [yorum sözlükleri]
        self.link_provider = None    # sayfa -> [bağlantı sözlükleri]
        self._sel = None             # akış seçimi: (sayfa, a, b)
        self._smode = None
        self._ann_sel = None         # seçili yorum
        self._ann_items = []
        self._click_count = 0
        self._last_click = (0.0, None)
        self.setMouseTracking(True)
        self.viewport().setMouseTracking(True)

    # ------------------------------------------------------------ belge
    def set_document(self, doc: PdfDocument | None, keep_position: bool = False):
        pos = self.verticalScrollBar().value() if keep_position else 0
        cur = self._current if keep_position else 0
        self.scene().clear()
        self.items_ = []
        self._search_items = []
        self._sel_items = []
        self._sel = None
        self.selectionCleared.emit()
        self._ann_items = []
        self._ann_sel = None
        self._smode = None
        self.doc = doc
        self._gen += 1
        self.renderer.set_document(doc, self._gen)
        if not doc:
            return
        for i in range(doc.page_count):
            w, h = doc.page_size(i)
            it = PageItem(i, w, h)
            self.scene().addItem(it)
            self.items_.append(it)
            if i < 40:
                eff = QGraphicsDropShadowEffect()
                eff.setBlurRadius(14)
                eff.setOffset(0, 2)
                eff.setColor(QColor(0, 0, 0, 45))
                it.setGraphicsEffect(eff)
        self._relayout()
        if self.fit_mode != "none":
            QTimer.singleShot(0, self.apply_fit)
        if keep_position:
            QTimer.singleShot(10, lambda: (self.verticalScrollBar().setValue(pos), self._render_visible()))
            self._current = cur
        self._render_timer.start()

    def _relayout(self):
        s = self.scale_
        y = GAP
        maxw = 0
        if self.layout_mode == "facing":
            i = 0
            while i < len(self.items_):
                row = self.items_[i:i + 2]
                for it in row:
                    it.set_size(s)
                rw = sum(it.rect().width() for it in row) + GAP * (len(row) - 1)
                maxw = max(maxw, rw)
                i += 2
            i = 0
            while i < len(self.items_):
                row = self.items_[i:i + 2]
                rw = sum(it.rect().width() for it in row) + GAP * (len(row) - 1)
                x = (maxw - rw) / 2 + GAP
                for it in row:
                    it.setPos(x, y)
                    it.setVisible(True)
                    x += it.rect().width() + GAP
                y += max(it.rect().height() for it in row) + GAP
                i += 2
        else:
            for it in self.items_:
                it.set_size(s)
                maxw = max(maxw, it.rect().width())
            for it in self.items_:
                if self.layout_mode == "single":
                    vis = it.index == self._current
                    it.setVisible(vis)
                    if vis:
                        it.setPos(GAP + (maxw - it.rect().width()) / 2, GAP)
                        y = GAP + it.rect().height() + GAP
                    continue
                it.setVisible(True)
                it.setPos(GAP + (maxw - it.rect().width()) / 2, y)
                y += it.rect().height() + GAP
        self.scene().setSceneRect(0, 0, maxw + GAP * 2, max(y, 10))
        dpr = self.devicePixelRatioF()
        for it in self.items_:
            if it.rendered_scale and abs(it.rendered_scale - s * dpr) > 1e-3:
                # Eski görüntüyü ölçekleyerek göster (yeni çizim gelene kadar)
                pm = it.pix.pixmap()
                if not pm.isNull():
                    it.pix.setScale(s * dpr / it.rendered_scale)
        self._refresh_overlays()
        if self._ann_sel and self._ann_items:
            r = self.pdf_rect_to_scene(self._ann_sel["page"], self._ann_sel["rect"]).adjusted(-2, -2, 2, 2)
            self._move_ann_overlay(r)
        self.layoutChanged.emit()

    # ------------------------------------------------------------ yakınlaştırma
    def zoom(self) -> float:
        return self.scale_ / DPI_FACTOR

    def set_zoom(self, z: float, anchor: QPointF | None = None):
        z = max(0.1, min(8.0, z))
        page = self._current
        old = self.scale_
        rel = None
        if anchor is None and self.items_:
            it = self.items_[page]
            top = self.mapToScene(0, 0).y()
            rel = (top - it.pos().y()) / max(1, it.rect().height())
        self.scale_ = z * DPI_FACTOR
        self._relayout()
        if rel is not None and self.items_:
            it = self.items_[page]
            self.verticalScrollBar().setValue(int(it.pos().y() + rel * it.rect().height()))
        self.zoomChanged.emit(z)
        self._render_timer.start()

    def zoom_in(self):
        self.fit_mode = "none"
        self.set_zoom(self.zoom() * 1.2)

    def zoom_out(self):
        self.fit_mode = "none"
        self.set_zoom(self.zoom() / 1.2)

    def apply_fit(self):
        if not self.items_:
            return
        it = self.items_[min(self._current, len(self.items_) - 1)]
        vw = self.viewport().width() - GAP * 2 - 4
        vh = self.viewport().height() - GAP * 2
        cols = 2 if self.layout_mode == "facing" else 1
        if self.fit_mode == "width":
            z = (vw - GAP * (cols - 1)) / (it.w_pt * cols) / DPI_FACTOR
        elif self.fit_mode == "page":
            z = min((vw - GAP * (cols - 1)) / (it.w_pt * cols), vh / it.h_pt) / DPI_FACTOR
        else:
            return
        self.set_zoom(z)

    def set_fit(self, mode):
        self.fit_mode = mode
        self.apply_fit()

    def set_layout(self, mode):
        self.layout_mode = mode
        self._relayout()
        self.apply_fit()
        self.go_to(self._current)

    def resizeEvent(self, e):
        super().resizeEvent(e)
        if self.fit_mode != "none":
            self.apply_fit()
        self._render_timer.start()

    def wheelEvent(self, e):
        if e.modifiers() & Qt.ControlModifier:
            self.fit_mode = "none"
            f = 1.15 if e.angleDelta().y() > 0 else 1 / 1.15
            self.set_zoom(self.zoom() * f)
            e.accept()
            return
        if self.layout_mode == "single":
            if e.angleDelta().y() < 0 and self.verticalScrollBar().value() >= self.verticalScrollBar().maximum():
                self.go_to(self._current + 1)
                return
            if e.angleDelta().y() > 0 and self.verticalScrollBar().value() <= 0 and self._current > 0:
                self.go_to(self._current - 1)
                QTimer.singleShot(0, lambda: self.verticalScrollBar().setValue(self.verticalScrollBar().maximum()))
                return
        super().wheelEvent(e)

    # ------------------------------------------------------------ gezinme
    def current_page(self) -> int:
        return self._current

    def go_to(self, i: int, y_pdf: float | None = None):
        if not self.items_:
            return
        i = max(0, min(i, len(self.items_) - 1))
        if self.layout_mode == "single":
            self._current = i
            self._relayout()
            self.verticalScrollBar().setValue(0)
            self.pageChanged.emit(i)
            self._render_timer.start()
            return
        it = self.items_[i]
        y = it.pos().y() - GAP / 2
        if y_pdf is not None:
            _, dy = self.doc.page_to_device(i, it.rect().width(), it.rect().height(), 0, y_pdf)
            y = it.pos().y() + dy - 60
        self.verticalScrollBar().setValue(int(y))
        self._current = i
        self.pageChanged.emit(i)

    def _on_scroll(self, *_):
        if self.layout_mode != "single" and self.items_:
            mid = self.mapToScene(0, self.viewport().height() // 3).y()
            for it in self.items_:
                if it.pos().y() <= mid <= it.pos().y() + it.rect().height() + GAP:
                    if it.index != self._current:
                        self._current = it.index
                        self.pageChanged.emit(it.index)
                    break
        self._render_timer.start()

    # ------------------------------------------------------------ çizim
    def _render_visible(self):
        """Görünen sayfalar için arka plan çizimi ister (ana iş parçacığını asla bekletmez)."""
        if not self.doc:
            return
        vis = self.mapToScene(self.viewport().rect()).boundingRect()
        near = vis.adjusted(0, -vis.height() * 0.6, 0, vis.height() * 0.6)
        s = self.scale_ * self.devicePixelRatioF()
        wanted = set()
        for it in self.items_:
            if not it.isVisible():
                continue
            r = QRectF(it.pos(), it.rect().size())
            if r.intersects(near):
                wanted.add(it.index)
                if abs(it.rendered_scale - s) > 1e-3 and abs(getattr(it, "pending_scale", 0) - s) > 1e-3:
                    if not it.rendered_scale:
                        # anında önizleme: düşük çözünürlük
                        self.renderer.request(it.index, min(s, 0.35), self._current)
                    it.pending_scale = s
                    self.renderer.request(it.index, s, self._current)
            elif it.rendered_scale and abs(r.center().y() - vis.center().y()) > vis.height() * 5:
                it.pix.setPixmap(QPixmap())  # bellek
                it.rendered_scale = 0.0
                it.pending_scale = 0
        self.renderer.cancel_except(wanted)
        for it in self.items_:
            if it.index not in wanted and getattr(it, "pending_scale", 0) and not it.rendered_scale:
                it.pending_scale = 0

    def _on_rendered(self, kind, gen, page, scale, qimg):
        if kind != "view" or gen != self._gen or page >= len(self.items_):
            return
        it = self.items_[page]
        target = self.scale_ * self.devicePixelRatioF()
        if it.rendered_scale and abs(it.rendered_scale - target) < 1e-3 and scale < target:
            return  # zaten tam kalite var; geç gelen önizlemeyi yok say
        pm = QPixmap.fromImage(qimg)
        dpr = self.devicePixelRatioF()
        pm.setDevicePixelRatio(dpr)
        it.pix.setPixmap(pm)
        it.pix.setScale(target / scale)
        it.rendered_scale = scale if abs(scale - target) > 1e-3 else target
        if abs(scale - target) < 1e-3:
            it.pending_scale = 0

    def closeEvent(self, e):
        self.renderer.stop()
        super().closeEvent(e)

    def refresh(self):
        self.renderer.night = self.night
        for it in self.items_:
            it.rendered_scale = 0.0
            it.pending_scale = 0
        self._render_visible()

    # ------------------------------------------------------------ koordinatlar
    def page_at(self, scene_pt: QPointF) -> PageItem | None:
        for it in self.items_:
            if it.isVisible() and QRectF(it.pos(), it.rect().size()).contains(scene_pt):
                return it
        return None

    def to_pdf(self, it: PageItem, scene_pt: QPointF) -> tuple[float, float]:
        lp = scene_pt - it.pos()
        return self.doc.device_to_page(it.index, it.rect().width(), it.rect().height(), lp.x(), lp.y())

    def pdf_rect_to_scene(self, page: int, rect) -> QRectF:
        it = self.items_[page]
        x0, y0, x1, y1 = self.doc.rect_to_device(page, it.rect().width(), it.rect().height(), rect)
        return QRectF(it.pos().x() + x0, it.pos().y() + y0, x1 - x0, y1 - y0)

    # ------------------------------------------------------------ vurgular
    def _refresh_overlays(self):
        for item, page, rect in self._search_items + self._sel_items:
            if page < len(self.items_):
                item.setRect(self.pdf_rect_to_scene(page, rect))

    def set_search_hits(self, hits, current: int = -1):
        for it, _, _ in self._search_items:
            self.scene().removeItem(it)
        self._search_items = []
        for k, h in enumerate(hits):
            for r in h.rects:
                ri = QGraphicsRectItem()
                col = QColor("#FF9800") if k == current else QColor("#FFEB3B")
                col.setAlpha(110)
                ri.setBrush(QBrush(col))
                ri.setPen(QPen(Qt.NoPen))
                ri.setZValue(5)
                self.scene().addItem(ri)
                self._search_items.append((ri, h.page, r))
        self._refresh_overlays()

    def set_selection_rects(self, page, rects):
        self.clear_selection()
        for r in rects:
            ri = QGraphicsRectItem()
            c = QColor(theme.C["primary"])
            c.setAlpha(70)
            ri.setBrush(QBrush(c))
            ri.setPen(QPen(Qt.NoPen))
            ri.setZValue(6)
            self.scene().addItem(ri)
            self._sel_items.append((ri, page, r))
        self._refresh_overlays()

    def clear_selection(self):
        for it, _, _ in self._sel_items:
            try:
                self.scene().removeItem(it)
            except RuntimeError:
                pass
        self._sel_items = []
        self._sel = None
        self.selectionCleared.emit()

    # ------------------------------------------------------------ araçlar
    RECT_TOOLS = {"rect_select", "highlight_area", "redact", "square", "circle", "freetext", "link", "field_text",
                  "field_checkbox", "field_radio", "field_combo", "field_list", "field_button", "field_signature",
                  "sign", "crop", "add_text_box", "image", "ocr_area", "line", "zoom_area"}
    FLOW_TOOLS = {"select", "highlight", "underline", "strike"}
    POINT_TOOLS = {"note", "add_text", "edit_text", "stamp"}

    def set_tool(self, tool: str, color: str | None = None):
        self.tool = tool
        if color:
            self.tool_color = QColor(color)
        self.setDragMode(QGraphicsView.ScrollHandDrag if tool == "hand" else QGraphicsView.NoDrag)
        if tool != "select" and self._ann_sel:
            self.select_annotation(None)
        cur = {"hand": Qt.OpenHandCursor, "select": Qt.ArrowCursor, "ink": Qt.CrossCursor, "note": Qt.PointingHandCursor,
               "add_text": Qt.IBeamCursor, "edit_text": Qt.IBeamCursor, "stamp": Qt.PointingHandCursor}.get(tool, Qt.CrossCursor)
        self.viewport().setCursor(cur)

    # ------------------------------------------------------------ seçim aracı (akış seçimi, yorum, bağlantı)
    def _hit_annot(self, page, x, y, margin=3.0):
        if not self.annot_provider:
            return None
        for d in reversed(self.annot_provider(page) or []):
            l, b, r, t = d["rect"]
            if l - margin <= x <= r + margin and b - margin <= y <= t + margin:
                return d
        return None

    def _hit_link(self, page, x, y):
        if not self.link_provider:
            return None
        for d in self.link_provider(page) or []:
            l, b, r, t = d["rect"]
            if l <= x <= r and b <= y <= t:
                return d
        return None

    def _handle_at(self, sp: QPointF):
        """Seçili yorumun köşe tutamağı üzerinde mi? (0..3 veya None)"""
        if not self._ann_sel or not self._ann_items:
            return None
        rect = self._ann_items[0].rect()
        corners = [rect.topLeft(), rect.topRight(), rect.bottomLeft(), rect.bottomRight()]
        for k, c in enumerate(corners):
            if abs(c.x() - sp.x()) <= 6 and abs(c.y() - sp.y()) <= 6:
                return k
        return None

    def select_annotation(self, d):
        for it in self._ann_items:
            if it.scene():
                self.scene().removeItem(it)
        self._ann_items = []
        self._ann_sel = d
        if d and d["page"] < len(self.items_):
            r = self.pdf_rect_to_scene(d["page"], d["rect"]).adjusted(-2, -2, 2, 2)
            box = QGraphicsRectItem(r)
            box.setPen(QPen(QColor(theme.C["primary"]), 1.2, Qt.DashLine))
            box.setZValue(15)
            self.scene().addItem(box)
            self._ann_items.append(box)
            resizable = d["kind"] in ("Square", "Circle", "FreeText", "Stamp", "Ink", "Line")
            if resizable:
                for c in (r.topLeft(), r.topRight(), r.bottomLeft(), r.bottomRight()):
                    h = QGraphicsRectItem(QRectF(c.x() - 4, c.y() - 4, 8, 8))
                    h.setBrush(QBrush(QColor("#FFFFFF")))
                    h.setPen(QPen(QColor(theme.C["primary"]), 1.2))
                    h.setZValue(16)
                    self.scene().addItem(h)
                    self._ann_items.append(h)
        self.annotationSelected.emit(d)

    def _move_ann_overlay(self, rect: QRectF):
        if not self._ann_items:
            return
        self._ann_items[0].setRect(rect)
        cs = [rect.topLeft(), rect.topRight(), rect.bottomLeft(), rect.bottomRight()]
        for h, c in zip(self._ann_items[1:], cs):
            h.setRect(QRectF(c.x() - 4, c.y() - 4, 8, 8))

    def _select_press(self, e, it, sp):
        x, y = self.to_pdf(it, sp)
        import time as _t
        now = _t.monotonic()
        if now - self._last_click[0] < 0.4 and self._last_click[1] is not None and \
                (self._last_click[1] - sp).manhattanLength() < 6:
            self._click_count += 1
        else:
            self._click_count = 1
        self._last_click = (now, QPointF(sp))
        # 1) seçili yorumun tutamağı -> boyutlandır
        hk = self._handle_at(sp)
        if hk is not None:
            self._smode = ("resize", hk, self._ann_items[0].rect(), sp)
            return
        # 2) yorum -> seç / taşı
        d = self._hit_annot(it.index, x, y) if self.tool == "select" else None
        if d and not (e.modifiers() & Qt.AltModifier):
            if self._click_count >= 2:
                self.annotationActivated.emit(d)
                return
            self.clear_selection()
            self.select_annotation(d)
            self._smode = ("move", self._ann_items[0].rect(), sp)
            return
        if self._ann_sel:
            self.select_annotation(None)
        # 3) bağlantı (metin seçimi de başlayabilir; hareket olmazsa bırakınca açılır)
        self._link_down = self._hit_link(it.index, x, y)
        # 4) metin akışı seçimi
        ci = self.doc.char_at(it.index, x, y) if not (e.modifiers() & Qt.AltModifier) else -1
        if ci >= 0:
            if self._click_count >= 2:
                a, b = (self.doc.word_range if self._click_count == 2 else self.doc.line_range)(it.index, ci)
                self._set_range(it.index, a, b)
                self._smode = None
                self._emit_range()
                return
            self.clear_selection()
            self._smode = ("text", it.index, ci, sp)
            return
        # 5) boş alandan sürükleme: dikdörtgen seçimi (Alt ile her zaman)
        self.clear_selection()
        self._smode = ("rect", sp, it)
        self._band = QGraphicsRectItem(QRectF(sp, sp))
        self._band.setPen(QPen(QColor(theme.C["primary"]), 1.2, Qt.DashLine))
        c = QColor(theme.C["primary"])
        c.setAlpha(30)
        self._band.setBrush(QBrush(c))
        self._band.setZValue(10)
        self.scene().addItem(self._band)

    def _set_range(self, page, a, b):
        rects = self.doc.range_rects(page, a, b)
        self.set_selection_rects(page, rects)
        self._sel = (page, min(a, b), max(a, b))

    def _select_move(self, e):
        sp = self.mapToScene(e.position().toPoint())
        m = self._smode
        if m is None:
            self._hover(sp)
            return
        kind = m[0]
        if kind == "move":
            r0, start = m[1], m[2]
            self._move_ann_overlay(r0.translated(sp - start))
        elif kind == "resize":
            hk, r0, start = m[1], m[2], m[3]
            r = QRectF(r0)
            d = sp - start
            if hk in (0, 2):
                r.setLeft(r0.left() + d.x())
            else:
                r.setRight(r0.right() + d.x())
            if hk in (0, 1):
                r.setTop(r0.top() + d.y())
            else:
                r.setBottom(r0.bottom() + d.y())
            self._move_ann_overlay(r.normalized())
        elif kind == "text":
            page, a, start = m[1], m[2], m[3]
            if (sp - start).manhattanLength() < 3:
                return
            it = self.items_[page]
            r = QRectF(it.pos(), it.rect().size())
            spc = QPointF(min(max(sp.x(), r.left()), r.right()), min(max(sp.y(), r.top()), r.bottom()))
            x, y = self.to_pdf(it, spc)
            b = self.doc.nearest_char(page, x, y)
            if b >= 0:
                self._set_range(page, a, b)
        elif kind == "rect":
            start, it = m[1], m[2]
            r = QRectF(it.pos(), it.rect().size())
            spc = QPointF(min(max(sp.x(), r.left()), r.right()), min(max(sp.y(), r.top()), r.bottom()))
            self._band.setRect(QRectF(start, spc).normalized())
            self._drag_page = it
            self._live_select(QRectF(start, spc).normalized())

    def _hover(self, sp):
        it = self.page_at(sp)
        if not it or not self.doc:
            self.viewport().setCursor(Qt.ArrowCursor)
            return
        if self._handle_at(sp) is not None:
            self.viewport().setCursor(Qt.SizeFDiagCursor)
            return
        x, y = self.to_pdf(it, sp)
        d = self._hit_annot(it.index, x, y)
        if d and self.tool == "select":
            self.viewport().setCursor(Qt.SizeAllCursor)
            if d.get("contents"):
                from PySide6.QtWidgets import QToolTip
                QToolTip.showText(self.cursor().pos(), f"<b>{d['kind_tr']}</b> · {d['author']}<br>{d['contents'][:400]}", self)
            return
        if self._hit_link(it.index, x, y):
            self.viewport().setCursor(Qt.PointingHandCursor)
            return
        self.viewport().setCursor(Qt.IBeamCursor if self.doc.char_at(it.index, x, y) >= 0 else Qt.ArrowCursor)

    def _select_release(self, e):
        m, self._smode = self._smode, None
        sp = self.mapToScene(e.position().toPoint())
        if m is None:
            return
        kind = m[0]
        if kind in ("move", "resize") and self._ann_sel:
            d = self._ann_sel
            r = self._ann_items[0].rect().adjusted(2, 2, -2, -2)
            r0 = self.pdf_rect_to_scene(d["page"], d["rect"])
            if (r.topLeft() - r0.topLeft()).manhattanLength() > 2 or (r.bottomRight() - r0.bottomRight()).manhattanLength() > 2:
                it = self.items_[d["page"]]
                pr = self.doc.device_rect_to_page(d["page"], it.rect().width(), it.rect().height(),
                                                  r.left() - it.pos().x(), r.top() - it.pos().y(),
                                                  r.right() - it.pos().x(), r.bottom() - it.pos().y())
                self.annotationMoved.emit(d["page"], d["objgen"], pr)
            return
        if kind == "text":
            page = m[1]
            if self._sel and self._sel[0] == page and self._sel[2] > self._sel[1]:
                self._emit_range()
            elif getattr(self, "_link_down", None):
                self.linkActivated.emit(self._link_down)
            else:
                self.clear_selection()
            return
        if kind == "word":
            self._emit_range()
            return
        if kind == "rect":
            if self._band is not None:
                srect = self._band.rect()
                self.scene().removeItem(self._band)
                self._band = None
                it = m[2]
                if srect.width() < 3 and srect.height() < 3:
                    if getattr(self, "_link_down", None):
                        self.linkActivated.emit(self._link_down)
                    else:
                        self.pointClicked.emit(it.index, self.to_pdf(it, sp))
                    return
                pr = self.doc.device_rect_to_page(it.index, it.rect().width(), it.rect().height(),
                                                  srect.left() - it.pos().x(), srect.top() - it.pos().y(),
                                                  srect.right() - it.pos().x(), srect.bottom() - it.pos().y())
                words = [w for w in self._words(it.index)
                         if not (w[1][2] < pr[0] or w[1][0] > pr[2] or w[1][3] < pr[1] or w[1][1] > pr[3])]
                if words:
                    self.textSelected.emit(it.index, pr, self._words_to_text(words))

    def _emit_range(self):
        page, a, b = self._sel
        rects = self.doc.range_rects(page, a, b)
        text = self.doc.range_text(page, a, b).replace("\r\n", "\n").replace("\r", "\n")
        self.rangeSelected.emit(page, rects, text)

    def select_all_on_page(self):
        if not self.doc:
            return
        page = self._current
        n = self.doc.char_count(page)
        if n > 0:
            self._set_range(page, 0, n - 1)
            self._emit_range()

    def mouseDoubleClickEvent(self, e):
        # çift tıklama, basma olayında sayılıyor (üçlü tıklama için)
        self.mousePressEvent(e)

    def mousePressEvent(self, e):
        if self.tool in self.FLOW_TOOLS and e.button() == Qt.LeftButton:
            sp = self.mapToScene(e.position().toPoint())
            it = self.page_at(sp)
            if it:
                return self._select_press(e, it, sp)
            self.clear_selection()
            if self._ann_sel:
                self.select_annotation(None)
            return super().mousePressEvent(e)
        if e.button() == Qt.RightButton:
            sp = self.mapToScene(e.position().toPoint())
            it = self.page_at(sp)
            if it:
                self.contextRequested.emit(it.index, self.to_pdf(it, sp), e.globalPosition().toPoint())
            return
        if self.tool == "hand" or e.button() != Qt.LeftButton:
            return super().mousePressEvent(e)
        sp = self.mapToScene(e.position().toPoint())
        it = self.page_at(sp)
        if not it:
            self.clear_selection()
            return super().mousePressEvent(e)
        if self.tool in self.POINT_TOOLS:
            self.pointClicked.emit(it.index, self.to_pdf(it, sp))
            return
        self._drag_start = sp
        self._drag_page = it
        if self.tool == "ink":
            self._ink_points = [[sp]]
            path = QPainterPath(sp)
            self._ink_path = QGraphicsPathItem(path)
            self._ink_path.setPen(QPen(self.tool_color, 2.2, Qt.SolidLine, Qt.RoundCap, Qt.RoundJoin))
            self._ink_path.setZValue(10)
            self.scene().addItem(self._ink_path)
            return
        self._band = QGraphicsRectItem(QRectF(sp, sp))
        col = QColor(self.tool_color if self.tool != "select" else QColor(theme.C["primary"]))
        pen = QPen(col, 1.2, Qt.DashLine if self.tool in ("select", "rect_select", "ocr_area", "zoom_area") else Qt.SolidLine)
        self._band.setPen(pen)
        fill = QColor(col)
        fill.setAlpha(40 if self.tool != "redact" else 120)
        if self.tool == "redact":
            fill = QColor(0, 0, 0, 130)
        self._band.setBrush(QBrush(fill))
        self._band.setZValue(10)
        self.scene().addItem(self._band)

    def mouseMoveEvent(self, e):
        if self.tool in self.FLOW_TOOLS:
            return self._select_move(e)
        if self._drag_start is not None and self._drag_page is not None:
            sp = self.mapToScene(e.position().toPoint())
            r = QRectF(self._drag_page.pos(), self._drag_page.rect().size())
            sp = QPointF(min(max(sp.x(), r.left()), r.right()), min(max(sp.y(), r.top()), r.bottom()))
            if self._ink_path is not None:
                self._ink_points[-1].append(sp)
                p = self._ink_path.path()
                p.lineTo(sp)
                self._ink_path.setPath(p)
            elif self._band is not None:
                self._band.setRect(QRectF(self._drag_start, sp).normalized())
                if self.tool == "select":
                    self._live_select(QRectF(self._drag_start, sp).normalized())
            return
        super().mouseMoveEvent(e)

    def _live_select(self, srect: QRectF):
        it = self._drag_page
        if srect.width() < 3 and srect.height() < 3:
            return
        pr = self.doc.device_rect_to_page(it.index, it.rect().width(), it.rect().height(),
                                          srect.left() - it.pos().x(), srect.top() - it.pos().y(),
                                          srect.right() - it.pos().x(), srect.bottom() - it.pos().y())
        words = [w for w in self._words(it.index) if not (w[1][2] < pr[0] or w[1][0] > pr[2] or w[1][3] < pr[1] or w[1][1] > pr[3])]
        self.set_selection_rects(it.index, [w[1] for w in words])

    _word_cache: dict = {}

    def _words(self, page):
        key = (id(self.doc), page)
        if key not in self._word_cache:
            self._word_cache.clear() if len(self._word_cache) > 50 else None
            self._word_cache[key] = self.doc.words(page)
        return self._word_cache[key]

    def invalidate_words(self):
        self._word_cache.clear()

    def mouseReleaseEvent(self, e):
        if self.tool in self.FLOW_TOOLS and e.button() == Qt.LeftButton:
            return self._select_release(e)
        if self._drag_start is None:
            return super().mouseReleaseEvent(e)
        it = self._drag_page
        sp = self.mapToScene(e.position().toPoint())
        start = self._drag_start
        self._drag_start = None
        if self._ink_path is not None:
            pts = [[self.to_pdf(it, p) for p in stroke] for stroke in self._ink_points]
            self.scene().removeItem(self._ink_path)
            self._ink_path = None
            if sum(len(s) for s in pts) > 2:
                self.inkDrawn.emit(it.index, pts)
            return
        if self._band is not None:
            srect = self._band.rect()
            self.scene().removeItem(self._band)
            self._band = None
            pr = self.doc.device_rect_to_page(it.index, it.rect().width(), it.rect().height(),
                                              srect.left() - it.pos().x(), srect.top() - it.pos().y(),
                                              srect.right() - it.pos().x(), srect.bottom() - it.pos().y())
            if self.tool == "select":
                if srect.width() < 3 and srect.height() < 3:
                    self.clear_selection()
                    self.pointClicked.emit(it.index, self.to_pdf(it, sp))
                    return
                words = [w for w in self._words(it.index)
                         if not (w[1][2] < pr[0] or w[1][0] > pr[2] or w[1][3] < pr[1] or w[1][1] > pr[3])]
                text = self._words_to_text(words)
                if words:
                    self.textSelected.emit(it.index, pr, text)
                return
            if self.tool == "line":
                a = self.to_pdf(it, start)
                b = self.to_pdf(it, sp)
                self.rectDrawn.emit(it.index, (a[0], a[1], b[0], b[1]))
                return
            if srect.width() < 4 or srect.height() < 4:
                # tek tıklama: varsayılan boyutlu kutu
                x, y = self.to_pdf(it, sp)
                w, h = {"freetext": (180, 40), "field_text": (160, 20), "field_checkbox": (14, 14),
                        "field_radio": (60, 14), "field_combo": (140, 20), "field_list": (140, 60),
                        "field_button": (90, 24), "field_signature": (180, 50), "sign": (200, 70),
                        "square": (80, 50), "circle": (80, 50), "image": (150, 100), "add_text_box": (200, 40)}.get(self.tool, (0, 0))
                if not w:
                    return
                pr = (x, y - h, x + w, y)
            self.rectDrawn.emit(it.index, pr)

    @staticmethod
    def _words_to_text(words):
        lines, last_b = [], None
        for t, (l, b, r, tp) in sorted(words, key=lambda w: (-round(w[1][1] / 3), w[1][0])):
            if last_b is None or abs(b - last_b) > (tp - b) * 0.5:
                lines.append([t])
            else:
                lines[-1].append(t)
            last_b = b
        return "\n".join(" ".join(l) for l in lines)

    def keyPressEvent(self, e):
        if e.key() in (Qt.Key_Delete, Qt.Key_Backspace) and self._ann_sel:
            self.annotationDeleteRequested.emit(self._ann_sel["objgen"])
            return
        if e.key() == Qt.Key_Escape:
            self.clear_selection()
            if self._ann_sel:
                self.select_annotation(None)
        if e.key() == Qt.Key_A and e.modifiers() & Qt.ControlModifier:
            self.select_all_on_page()
            return
        if e.key() in (Qt.Key_PageDown,) or (e.key() == Qt.Key_Right and self.layout_mode == "single"):
            self.go_to(self._current + (2 if self.layout_mode == "facing" else 1))
            return
        if e.key() in (Qt.Key_PageUp,) or (e.key() == Qt.Key_Left and self.layout_mode == "single"):
            self.go_to(self._current - (2 if self.layout_mode == "facing" else 1))
            return
        if e.key() == Qt.Key_Home and e.modifiers() & Qt.ControlModifier:
            self.go_to(0)
            return
        if e.key() == Qt.Key_End and e.modifiers() & Qt.ControlModifier:
            self.go_to(len(self.items_) - 1)
            return
        super().keyPressEvent(e)
