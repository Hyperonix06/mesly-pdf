"""Ortak arayüz yardımcıları: ayarlar, arka plan işleri, bildirimler, dosya seçiciler, dil seçici."""
from __future__ import annotations

import inspect
import json
import os
import traceback

from PySide6.QtCore import QObject, QThread, Signal, Qt, QTimer, QPoint, QSize
from PySide6.QtWidgets import (QWidget, QLabel, QHBoxLayout, QVBoxLayout, QFrame, QFileDialog, QMessageBox,
                               QProgressDialog, QPushButton, QToolButton, QMenu, QApplication, QGraphicsOpacityEffect,
                               QComboBox, QSizePolicy)

from ..core.paths import app_data_dir, LANG_NAMES
from . import theme

# ------------------------------------------------------------------ ayarlar
_DEFAULTS = {"ocr_lang": "tur+eng", "dark": False, "author": "", "recent": [], "out_dir": "", "dpi": 300,
             "open_with": "editor", "hotfolder_autostart": False, "export_mode": "formatted",
             "preprocess": {"auto_orient": True, "deskew": True, "whiten_background": True, "denoise": False,
                            "perspective": False, "binarize": False, "crop_borders": False, "split_pages": False}}


class Settings:
    _data: dict = {}

    @classmethod
    def path(cls):
        return os.path.join(app_data_dir(), "ayarlar.json")

    @classmethod
    def load(cls):
        cls._data = json.loads(json.dumps(_DEFAULTS))
        try:
            with open(cls.path(), encoding="utf-8") as f:
                cls._data.update(json.load(f))
        except Exception:
            pass

    @classmethod
    def get(cls, k, default=None):
        if not cls._data:
            cls.load()
        return cls._data.get(k, default if default is not None else _DEFAULTS.get(k))

    @classmethod
    def set(cls, k, v):
        if not cls._data:
            cls.load()
        cls._data[k] = v
        try:
            with open(cls.path(), "w", encoding="utf-8") as f:
                json.dump(cls._data, f, ensure_ascii=False, indent=2)
        except Exception:
            pass

    @classmethod
    def add_recent(cls, path: str):
        r = [p for p in cls.get("recent", []) if p != path]
        r.insert(0, os.path.abspath(path))
        cls.set("recent", r[:15])


# ------------------------------------------------------------------ arka plan işleri
class _Worker(QObject):
    progress = Signal(int, int)
    done = Signal(object)
    error = Signal(str)

    def __init__(self, fn, args, kwargs):
        super().__init__()
        self.fn, self.args, self.kwargs = fn, args, kwargs
        self.cancelled = False

    def run(self):
        try:
            params = inspect.signature(self.fn).parameters
            kw = dict(self.kwargs)
            if "progress" in params and "progress" not in kw:
                kw["progress"] = lambda i, n: self.progress.emit(int(i), int(n))
            if "cancel" in params and "cancel" not in kw:
                kw["cancel"] = lambda: self.cancelled
            self.done.emit(self.fn(*self.args, **kw))
        except InterruptedError:
            self.error.emit("__cancel__")
        except Exception as e:
            traceback.print_exc()
            self.error.emit(str(e) or e.__class__.__name__)


_running: list = []


class _Receiver(QObject):
    """Ana iş parçacığında yaşayan alıcı: sonuç geri çağrıları GUI iş parçacığında çalışsın."""

    def __init__(self, on_progress, on_ok, on_err):
        super().__init__()
        self._p, self._ok, self._err = on_progress, on_ok, on_err

    def progress(self, i, n):
        self._p(i, n)

    def ok(self, res):
        self._ok(res)

    def err(self, msg):
        self._err(msg)


def run_task(parent: QWidget, _title: str, fn, *args, on_done=None, on_error=None, _modal: bool = True, **kwargs):
    title, modal = _title, _modal
    """fn'i arka planda çalıştırır; ilerleme penceresi gösterir. fn 'progress' ve 'cancel' parametreleri alabilir."""
    th = QThread()
    w = _Worker(fn, args, kwargs)
    w.moveToThread(th)
    dlg = None
    if modal:
        dlg = QProgressDialog(title, "İptal", 0, 0, parent)
        dlg.setWindowTitle("Mesly PDF")
        dlg.setWindowModality(Qt.WindowModal)
        dlg.setMinimumDuration(250)
        dlg.setMinimumWidth(380)
        dlg.setAutoClose(False)
        dlg.setAutoReset(False)
        dlg.canceled.connect(lambda: setattr(w, "cancelled", True))

    def prog(i, n):
        if dlg is not None and n:
            dlg.setMaximum(n)
            dlg.setValue(min(i, n))

    def finish():
        if dlg:
            dlg.reset()
            dlg.close()
        th.quit()
        th.wait()
        for item in list(_running):
            if item[0] is th:
                _running.remove(item)

    def ok(res):
        finish()
        if on_done:
            on_done(res)

    def err(msg):
        finish()
        if msg == "__cancel__":
            toast(parent, "İşlem iptal edildi")
            return
        if on_error:
            on_error(msg)
        else:
            QMessageBox.critical(parent, "Hata", f"{title.rstrip('.…')} sırasında hata oluştu:\n\n{msg}")
    rcv = _Receiver(prog, ok, err)
    w.progress.connect(rcv.progress, Qt.QueuedConnection)
    w.done.connect(rcv.ok, Qt.QueuedConnection)
    w.error.connect(rcv.err, Qt.QueuedConnection)
    th.started.connect(w.run)
    _running.append((th, w, rcv))
    th.start()
    return w


# ------------------------------------------------------------------ bildirim
class Toast(QFrame):
    _current = {}

    def __init__(self, parent, text, kind="info", ms=3200):
        win = parent.window()
        old = Toast._current.get(id(win))
        if old is not None:
            try:
                old.hide()
                old.deleteLater()
            except RuntimeError:
                pass
        super().__init__(win)
        Toast._current[id(win)] = self
        self.setObjectName("Toast")
        lay = QHBoxLayout(self)
        lay.setContentsMargins(14, 10, 16, 10)
        ic = {"info": ("mdi6.information-outline", "#9CC2FF"), "ok": ("mdi6.check-circle-outline", "#7BE495"),
              "warn": ("mdi6.alert-outline", "#FFD27A"), "err": ("mdi6.alert-circle-outline", "#FF8A80")}[kind]
        il = QLabel()
        il.setPixmap(theme.icon(ic[0], ic[1]).pixmap(20, 20))
        lay.addWidget(il)
        lb = QLabel(text)
        lb.setWordWrap(True)
        lb.setMaximumWidth(460)
        lay.addWidget(lb)
        self.adjustSize()
        win = parent.window()
        self.move(win.width() - self.width() - 24, win.height() - self.height() - 40)
        self.show()
        self.raise_()
        eff = QGraphicsOpacityEffect(self)
        self.setGraphicsEffect(eff)
        QTimer.singleShot(ms, self.deleteLater)


def toast(parent, text, kind="info"):
    try:
        Toast(parent, text, kind)
    except Exception:
        pass


# ------------------------------------------------------------------ dosya seçiciler
PDF_FILTER = "PDF dosyaları (*.pdf)"
IMG_FILTER = "Görüntüler (*.png *.jpg *.jpeg *.tif *.tiff *.bmp *.gif *.webp)"
ALL_SUPPORTED = ("Desteklenen dosyalar (*.pdf *.png *.jpg *.jpeg *.tif *.tiff *.bmp *.gif *.webp *.docx *.doc "
                 "*.xlsx *.xls *.pptx *.ppt *.odt *.ods *.odp *.rtf *.txt *.html *.htm *.csv)")


def _start_dir():
    r = Settings.get("recent", [])
    return os.path.dirname(r[0]) if r else os.path.expanduser("~")


def open_files(parent, title="Dosya seç", filt=ALL_SUPPORTED, multi=True):
    if multi:
        fs, _ = QFileDialog.getOpenFileNames(parent, title, _start_dir(), filt + ";;Tüm dosyalar (*.*)")
        return fs
    f, _ = QFileDialog.getOpenFileName(parent, title, _start_dir(), filt + ";;Tüm dosyalar (*.*)")
    return [f] if f else []


def save_file(parent, title, suggested, filt=PDF_FILTER):
    d = Settings.get("out_dir") or _start_dir()
    f, _ = QFileDialog.getSaveFileName(parent, title, os.path.join(d, suggested), filt)
    if f:
        ext = os.path.splitext(suggested)[1]
        if ext and not f.lower().endswith(ext.lower()):
            f += ext
    return f


def pick_dir(parent, title="Klasör seç", start=""):
    return QFileDialog.getExistingDirectory(parent, title, start or _start_dir())


def ask_password(parent, name: str) -> str | None:
    from PySide6.QtWidgets import QInputDialog, QLineEdit
    pw, ok = QInputDialog.getText(parent, "Parola gerekli", f"“{name}” parola korumalı.\nParolayı girin:",
                                  QLineEdit.Password)
    return pw if ok else None


# ------------------------------------------------------------------ küçük bileşenler
def hline():
    f = QFrame()
    f.setFrameShape(QFrame.HLine)
    f.setStyleSheet(f"color: {theme.C['border']};")
    return f


def label(text, obj=None, wrap=False):
    l = QLabel(text)
    if obj:
        l.setObjectName(obj)
    l.setWordWrap(wrap)
    return l


def button(text, icon_name=None, obj=None, slot=None, tip=None):
    b = QPushButton(text)
    if icon_name:
        b.setIcon(theme.icon(icon_name, "#FFFFFF" if obj in ("Primary", "Danger") else None))
    if obj:
        b.setObjectName(obj)
    if slot:
        b.clicked.connect(slot)
    if tip:
        b.setToolTip(tip)
    b.setCursor(Qt.PointingHandCursor)
    return b


class LangPicker(QToolButton):
    """OCR dillerini çoklu seçen düğme. value -> 'tur+eng' biçimi; 'auto' otomatik algılama."""

    def __init__(self, value: str | None = None, parent=None):
        super().__init__(parent)
        self.setPopupMode(QToolButton.InstantPopup)
        self.setToolButtonStyle(Qt.ToolButtonTextBesideIcon)
        self.setIcon(theme.icon("mdi6.translate"))
        self.setStyleSheet("QToolButton { border: 1px solid %s; border-radius: 7px; padding: 5px 10px; background: %s; }"
                           % (theme.C["border_strong"], theme.C["surface"]))
        self.menu_ = QMenu(self)
        self.setMenu(self.menu_)
        from ..core.ocr import available_languages
        self._langs = available_languages()
        self._value = value or Settings.get("ocr_lang")
        self._build()

    def _build(self):
        self.menu_.clear()
        sel = set(self._value.split("+"))
        a = self.menu_.addAction("Otomatik algıla")
        a.setCheckable(True)
        a.setChecked(self._value == "auto")
        a.triggered.connect(lambda: self.set_value("auto"))
        self.menu_.addSeparator()
        prio = ["tur", "eng"]
        langs = prio + sorted([l for l in self._langs if l not in prio], key=lambda l: LANG_NAMES.get(l, l))
        for l in langs:
            if l not in self._langs:
                continue
            act = self.menu_.addAction(LANG_NAMES.get(l, l))
            act.setCheckable(True)
            act.setChecked(l in sel and self._value != "auto")
            act.triggered.connect(lambda checked, l=l: self._toggle(l, checked))
        self._update_text()

    def _toggle(self, l, checked):
        cur = [] if self._value == "auto" else [x for x in self._value.split("+") if x]
        if checked and l not in cur:
            cur.append(l)
        if not checked and l in cur:
            cur.remove(l)
        self.set_value("+".join(cur) or "tur")

    def set_value(self, v):
        self._value = v
        self._build()

    def value(self):
        return self._value

    def _update_text(self):
        if self._value == "auto":
            self.setText("Dil: Otomatik")
        else:
            names = [LANG_NAMES.get(l, l) for l in self._value.split("+")]
            self.setText("Dil: " + (", ".join(names) if len(names) <= 2 else f"{names[0]} +{len(names) - 1}"))
        self.setMinimumWidth(self.fontMetrics().horizontalAdvance(self.text()) + 64)


def confirm(parent, title, text) -> bool:
    return QMessageBox.question(parent, title, text, QMessageBox.Yes | QMessageBox.No) == QMessageBox.Yes


def open_path(path: str):
    from PySide6.QtGui import QDesktopServices
    from PySide6.QtCore import QUrl
    QDesktopServices.openUrl(QUrl.fromLocalFile(path))
