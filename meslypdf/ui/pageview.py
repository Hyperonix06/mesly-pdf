"""PDF sayfa görüntüleyici: sürekli/tek/iki sayfa düzeni, yakınlaştırma, tembel çizim,
araç modları (seçim, el, dikdörtgen çizme, tıklama, serbest çizim), arama vurguları."""
from __future__ import annotations

from PySide6.QtCore import Qt, Signal, QRectF, QPointF, QTimer
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
        self._render_timer = QTimer(self, singleShot=True, interval=40, timeout=self._render_visible)
        self.verticalScrollBar().valueChanged.connect(self._on_scroll)
        self.horizontalScrollBar().valueChanged.connect(lambda *_: self._render_timer.start())
        self.night = False

    # ------------------------------------------------------------ belge
    def set_document(self, doc: PdfDocument | None, keep_position: bool = False):
        pos = self.verticalScrollBar().value() if keep_position else 0
        cur = self._current if keep_position else 0
        self.scene().clear()
        self.items_ = []
        self._search_items = []
        self._sel_items = []
        self.doc = doc
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
        for it in self.items_:
            if it.rendered_scale and abs(it.rendered_scale - s) > 1e-3:
                # Eski görüntüyü ölçekleyerek göster (yeni çizim gelene kadar)
                pm = it.pix.pixmap()
                if not pm.isNull():
                    it.pix.setScale(s / it.rendered_scale)
        self._refresh_overlays()
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
        if not self.doc:
            return
        vis = self.mapToScene(self.viewport().rect()).boundingRect().adjusted(0, -300, 0, 300)
        s = self.scale_
        dpr = self.devicePixelRatioF()
        for it in self.items_:
            if not it.isVisible():
                continue
            r = QRectF(it.pos(), it.rect().size())
            if r.intersects(vis):
                if abs(it.rendered_scale - s) > 1e-3:
                    try:
                        img = self.doc.render(it.index, scale=s * dpr)
                    except Exception:
                        continue
                    pm = pil_to_qpixmap(img)
                    if self.night:
                        im = pm.toImage()
                        im.invertPixels()
                        pm = QPixmap.fromImage(im)
                    pm.setDevicePixelRatio(dpr)
                    it.pix.setPixmap(pm)
                    it.pix.setScale(1.0)
                    it.rendered_scale = s
            elif it.rendered_scale and abs(r.center().y() - vis.center().y()) > vis.height() * 4:
                it.pix.setPixmap(QPixmap())  # bellek
                it.rendered_scale = 0.0

    def refresh(self):
        for it in self.items_:
            it.rendered_scale = 0.0
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
            self.scene().removeItem(it)
        self._sel_items = []

    # ------------------------------------------------------------ araçlar
    RECT_TOOLS = {"rect_select", "highlight_area", "redact", "square", "circle", "freetext", "link", "field_text",
                  "field_checkbox", "field_radio", "field_combo", "field_list", "field_button", "field_signature",
                  "sign", "crop", "add_text_box", "image", "ocr_area", "line", "zoom_area"}
    POINT_TOOLS = {"note", "add_text", "edit_text", "stamp"}

    def set_tool(self, tool: str, color: str | None = None):
        self.tool = tool
        if color:
            self.tool_color = QColor(color)
        self.setDragMode(QGraphicsView.ScrollHandDrag if tool == "hand" else QGraphicsView.NoDrag)
        cur = {"hand": Qt.OpenHandCursor, "select": Qt.IBeamCursor, "ink": Qt.CrossCursor, "note": Qt.PointingHandCursor,
               "add_text": Qt.IBeamCursor, "edit_text": Qt.IBeamCursor, "stamp": Qt.PointingHandCursor}.get(tool, Qt.CrossCursor)
        self.viewport().setCursor(cur)

    def mousePressEvent(self, e):
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
