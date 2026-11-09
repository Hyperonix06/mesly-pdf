"""Ekran Okuyucu (ScreenshotReader benzeri): ekranın bir bölgesini seçip metin/tablo/görüntü olarak yakalar."""
from __future__ import annotations

from PySide6.QtCore import Qt, QRect, QPoint, Signal, QMimeData, QBuffer, QIODevice
from PySide6.QtGui import QPainter, QColor, QPen, QGuiApplication, QCursor, QPixmap
from PySide6.QtWidgets import QWidget, QMenu, QFileDialog
from PIL import Image

from ..core import ocr as ocrmod
from . import theme
from .common import Settings, toast, run_task
from .dialogs import TextDialog


def qpixmap_to_pil(pm: QPixmap) -> Image.Image:
    buf = QBuffer()
    buf.open(QIODevice.WriteOnly)
    pm.save(buf, "PNG")
    import io
    return Image.open(io.BytesIO(bytes(buf.data()))).convert("RGB")


class CaptureOverlay(QWidget):
    captured = Signal(QPixmap, str)

    def __init__(self, mode="text"):
        super().__init__(None, Qt.FramelessWindowHint | Qt.WindowStaysOnTopHint | Qt.Tool)
        self.mode = mode
        screen = QGuiApplication.screenAt(QCursor.pos()) or QGuiApplication.primaryScreen()
        self.shot = screen.grabWindow(0)
        self.setGeometry(screen.geometry())
        self.origin = None
        self.sel = QRect()
        self.setCursor(Qt.CrossCursor)
        self.setMouseTracking(True)

    def paintEvent(self, e):
        p = QPainter(self)
        p.drawPixmap(self.rect(), self.shot)
        p.fillRect(self.rect(), QColor(0, 0, 0, 110))
        if not self.sel.isNull():
            src = QRect(self.sel.topLeft() * self.shot.devicePixelRatio(), self.sel.size() * self.shot.devicePixelRatio())
            p.drawPixmap(self.sel, self.shot, src)
            p.setPen(QPen(QColor(theme.C["primary"]), 2))
            p.drawRect(self.sel)
            p.setPen(QColor("white"))
            p.drawText(self.sel.topLeft() + QPoint(4, -6), f"{self.sel.width()} × {self.sel.height()}")
        p.setPen(QColor("white"))
        p.drawText(20, 30, "Yakalamak istediğiniz alanı seçin · Esc: iptal · "
                   + {"text": "Metin", "table": "Tablo", "image": "Görüntü"}[self.mode] + " olarak")

    def mousePressEvent(self, e):
        self.origin = e.position().toPoint()
        self.sel = QRect(self.origin, self.origin)

    def mouseMoveEvent(self, e):
        if self.origin is not None:
            self.sel = QRect(self.origin, e.position().toPoint()).normalized()
            self.update()

    def mouseReleaseEvent(self, e):
        if self.sel.width() > 5 and self.sel.height() > 5:
            dpr = self.shot.devicePixelRatio()
            crop = self.shot.copy(QRect(self.sel.topLeft() * dpr, self.sel.size() * dpr))
            self.close()
            self.captured.emit(crop, self.mode)
        else:
            self.origin = None

    def keyPressEvent(self, e):
        if e.key() == Qt.Key_Escape:
            self.close()


_overlays = []


def start_capture(parent, mode="text"):
    from PySide6.QtCore import QTimer
    win = parent.window() if parent else None
    if win:
        win.showMinimized()

    def go():
        ov = CaptureOverlay(mode)
        _overlays.append(ov)
        ov.captured.connect(lambda pm, m: _handle(parent, pm, m))
        ov.showFullScreen()
        ov.activateWindow()
    QTimer.singleShot(350, go)


def _handle(parent, pm: QPixmap, mode):
    win = parent.window() if parent else None
    if win:
        win.showNormal()
        win.activateWindow()
    cb = QGuiApplication.clipboard()
    if mode == "image":
        cb.setPixmap(pm)
        toast(parent, "Görüntü panoya kopyalandı", "ok")
        return
    img = qpixmap_to_pil(pm)
    # ekran görüntüleri düşük çözünürlüklüdür: OCR için büyüt
    scale = 3 if img.height < 400 else 2
    img = img.resize((img.width * scale, img.height * scale), Image.LANCZOS)
    lang = Settings.get("ocr_lang")

    def work():
        if mode == "table":
            return ocrmod.recognize_image(img, lang, 300, areas=[{"kind": "table", "box": (0, 0, img.width, img.height)}])
        return ocrmod.recognize_image(img, lang, 300, detect_layout=False)

    def done(res):
        if mode == "table":
            tsv = "\n".join(b.text for b in res.blocks)
            md = QMimeData()
            md.setText(tsv)
            md.setHtml("".join("<table border=1>" + "".join("<tr>" + "".join(f"<td>{c['text']}</td>" for c in r) + "</tr>" for r in b.cells) + "</table>"
                               for b in res.blocks if b.kind == "table"))
            cb.setMimeData(md)
            toast(parent, "Tablo panoya kopyalandı — Excel'e yapıştırın", "ok")
            return
        text = "\n".join(" ".join(w.text for w in l.words) for b in res.blocks for l in b.lines)
        cb.setText(text)
        TextDialog(parent, "Yakalanan metin (panoya kopyalandı)", text,
                   actions=[("Yeniden kopyala", "mdi6.content-copy", lambda t: cb.setText(t))]).exec()
    run_task(parent, "Metin tanınıyor…", work, on_done=done)
