"""İletişim kutuları: genel form oluşturucu ve özel pencereler."""
from __future__ import annotations

import os

from PySide6.QtCore import Qt, QTimer
from PySide6.QtGui import QColor, QPixmap
from PySide6.QtWidgets import (QDialog, QVBoxLayout, QHBoxLayout, QFormLayout, QLineEdit, QSpinBox, QDoubleSpinBox,
                               QComboBox, QCheckBox, QPushButton, QLabel, QColorDialog, QDialogButtonBox, QPlainTextEdit,
                               QWidget, QFileDialog, QListWidget, QListWidgetItem, QTableWidget, QTableWidgetItem,
                               QHeaderView, QAbstractItemView, QTabWidget, QGroupBox, QGridLayout, QSlider, QMessageBox)

from . import theme
from .common import LangPicker, Settings, button, label


class ColorButton(QPushButton):
    def __init__(self, color="#C62828"):
        super().__init__()
        self.setFixedWidth(70)
        self.set_color(color)
        self.clicked.connect(self._pick)

    def set_color(self, c):
        self._c = c
        self.setStyleSheet(f"background:{c}; border:1px solid {theme.C['border_strong']}; border-radius:6px; min-height:22px;")

    def _pick(self):
        c = QColorDialog.getColor(QColor(self._c), self, "Renk seç")
        if c.isValid():
            self.set_color(c.name())

    def value(self):
        return self._c


class FileField(QWidget):
    def __init__(self, default="", filt="Tüm dosyalar (*.*)", save=False, directory=False):
        super().__init__()
        lay = QHBoxLayout(self)
        lay.setContentsMargins(0, 0, 0, 0)
        self.edit = QLineEdit(default)
        b = QPushButton("Gözat…")
        lay.addWidget(self.edit, 1)
        lay.addWidget(b)

        def pick():
            if directory:
                p = QFileDialog.getExistingDirectory(self, "Klasör seç", self.edit.text())
            elif save:
                p, _ = QFileDialog.getSaveFileName(self, "Kaydet", self.edit.text(), filt)
            else:
                p, _ = QFileDialog.getOpenFileName(self, "Dosya seç", self.edit.text(), filt)
            if p:
                self.edit.setText(p)
        b.clicked.connect(pick)

    def value(self):
        return self.edit.text().strip()


class FormDialog(QDialog):
    """fields: [(anahtar, etiket, tür, varsayılan, seçenekler/ayar)]
    türler: text, password, int, float, combo, check, color, file, savefile, dir, multiline, lang, slider, info"""

    def __init__(self, parent, title, fields, subtitle="", ok_text="Uygula", width=460, preview=None):
        super().__init__(parent)
        self.setWindowTitle(title)
        self.setMinimumWidth(width)
        self.widgets = {}
        outer = QVBoxLayout(self)
        outer.setContentsMargins(22, 18, 22, 16)
        outer.setSpacing(10)
        h = label(title, "H2")
        outer.addWidget(h)
        if subtitle:
            outer.addWidget(label(subtitle, "Muted", wrap=True))
        body = QHBoxLayout()
        form = QFormLayout()
        form.setLabelAlignment(Qt.AlignRight | Qt.AlignVCenter)
        form.setHorizontalSpacing(14)
        form.setVerticalSpacing(9)
        for f in fields:
            key, lab, typ = f[0], f[1], f[2]
            default = f[3] if len(f) > 3 else None
            opt = f[4] if len(f) > 4 else None
            w = self._make(typ, default, opt)
            self.widgets[key] = (typ, w)
            if typ == "check":
                form.addRow("", w)
                w.setText(lab)
            elif typ == "info":
                form.addRow(w)
            else:
                form.addRow(lab, w)
        body.addLayout(form, 1)
        self.preview_label = None
        if preview:
            self.preview_fn = preview
            self.preview_label = QLabel()
            self.preview_label.setFixedSize(220, 300)
            self.preview_label.setAlignment(Qt.AlignCenter)
            self.preview_label.setStyleSheet(f"background:{theme.C['canvas']}; border-radius:8px;")
            body.addWidget(self.preview_label)
            self._ptimer = QTimer(self, singleShot=True, interval=250, timeout=self._update_preview)
            for typ, w in self.widgets.values():
                for sig in ("textChanged", "valueChanged", "currentIndexChanged", "toggled", "clicked"):
                    if hasattr(w, sig):
                        try:
                            getattr(w, sig).connect(lambda *_: self._ptimer.start())
                        except Exception:
                            pass
            QTimer.singleShot(50, self._update_preview)
        outer.addLayout(body)
        bb = QDialogButtonBox()
        ok = bb.addButton(ok_text, QDialogButtonBox.AcceptRole)
        ok.setObjectName("Primary")
        bb.addButton("İptal", QDialogButtonBox.RejectRole)
        bb.accepted.connect(self.accept)
        bb.rejected.connect(self.reject)
        outer.addSpacing(6)
        outer.addWidget(bb)

    def _update_preview(self):
        if self.preview_label is None:
            return
        try:
            img = self.preview_fn(self.values())
            if img is not None:
                from .pageview import pil_to_qpixmap
                pm = pil_to_qpixmap(img).scaled(self.preview_label.size(), Qt.KeepAspectRatio, Qt.SmoothTransformation)
                self.preview_label.setPixmap(pm)
        except Exception as e:
            self.preview_label.setText(f"Önizleme yok\n{e}")

    def _make(self, typ, default, opt):
        if typ in ("text", "password"):
            w = QLineEdit("" if default is None else str(default))
            if typ == "password":
                w.setEchoMode(QLineEdit.Password)
            if opt:
                w.setPlaceholderText(opt)
            return w
        if typ == "int":
            w = QSpinBox()
            lo, hi = opt or (0, 999999)
            w.setRange(lo, hi)
            w.setValue(int(default or 0))
            return w
        if typ == "float":
            w = QDoubleSpinBox()
            lo, hi, step = opt or (0, 9999, 1)
            w.setRange(lo, hi)
            w.setSingleStep(step)
            w.setDecimals(2)
            w.setValue(float(default or 0))
            return w
        if typ == "combo":
            w = QComboBox()
            for o in opt:
                if isinstance(o, tuple):
                    w.addItem(o[1], o[0])
                else:
                    w.addItem(o, o)
            if default is not None:
                idx = w.findData(default)
                w.setCurrentIndex(max(0, idx))
            return w
        if typ == "check":
            w = QCheckBox()
            w.setChecked(bool(default))
            return w
        if typ == "color":
            return ColorButton(default or "#C62828")
        if typ in ("file", "savefile", "dir"):
            return FileField(default or "", opt or "Tüm dosyalar (*.*)", save=typ == "savefile", directory=typ == "dir")
        if typ == "multiline":
            w = QPlainTextEdit(default or "")
            w.setMinimumHeight(80)
            return w
        if typ == "lang":
            return LangPicker(default)
        if typ == "slider":
            w = QSlider(Qt.Horizontal)
            lo, hi = opt or (0, 100)
            w.setRange(lo, hi)
            w.setValue(int(default or 0))
            return w
        if typ == "info":
            l = QLabel(default or "")
            l.setWordWrap(True)
            l.setObjectName("Muted")
            return l
        raise ValueError(typ)

    def values(self) -> dict:
        out = {}
        for k, (typ, w) in self.widgets.items():
            if typ in ("text", "password"):
                out[k] = w.text()
            elif typ in ("int", "float", "slider"):
                out[k] = w.value()
            elif typ == "combo":
                out[k] = w.currentData()
            elif typ == "check":
                out[k] = w.isChecked()
            elif typ in ("color", "file", "savefile", "dir", "lang"):
                out[k] = w.value()
            elif typ == "multiline":
                out[k] = w.toPlainText()
        return out

    @staticmethod
    def ask(parent, title, fields, **kw):
        d = FormDialog(parent, title, fields, **kw)
        if d.exec() == QDialog.Accepted:
            return d.values()
        return None


# ------------------------------------------------------------------ ön tanımlı alan kümeleri
def page_scope_field(count, current):
    return ("pages", "Sayfalar", "combo", "all",
            [("all", f"Tüm sayfalar (1–{count})"), ("current", f"Geçerli sayfa ({current + 1})"), ("custom", "Özel aralık…")])


def resolve_pages(vals, count, current, custom_key="range"):
    from ..core.pages import parse_ranges
    sc = vals.get("pages", "all")
    if sc == "current":
        return [current]
    if sc == "custom":
        return parse_ranges(vals.get(custom_key, ""), count)
    return None


class TableDialog(QDialog):
    """Basit tablo düzenleyici (yer imleri, form alanları vb.)"""

    def __init__(self, parent, title, headers, rows, editable=True, width=640, buttons=None):
        super().__init__(parent)
        self.setWindowTitle(title)
        self.resize(width, 460)
        lay = QVBoxLayout(self)
        lay.setContentsMargins(18, 16, 18, 14)
        lay.addWidget(label(title, "H2"))
        self.table = QTableWidget(len(rows), len(headers))
        self.table.setHorizontalHeaderLabels(headers)
        self.table.horizontalHeader().setSectionResizeMode(0, QHeaderView.Stretch)
        self.table.verticalHeader().setVisible(False)
        self.table.setAlternatingRowColors(True)
        self.table.setSelectionBehavior(QAbstractItemView.SelectRows)
        if not editable:
            self.table.setEditTriggers(QAbstractItemView.NoEditTriggers)
        for r, row in enumerate(rows):
            for c, v in enumerate(row):
                self.table.setItem(r, c, QTableWidgetItem("" if v is None else str(v)))
        lay.addWidget(self.table, 1)
        hb = QHBoxLayout()
        if editable:
            hb.addWidget(button("Satır ekle", "mdi6.plus", slot=self._add))
            hb.addWidget(button("Sil", "mdi6.delete-outline", slot=self._del))
            hb.addWidget(button("Yukarı", "mdi6.arrow-up", slot=lambda: self._move(-1)))
            hb.addWidget(button("Aşağı", "mdi6.arrow-down", slot=lambda: self._move(1)))
        for txt, ic, fn in buttons or []:
            hb.addWidget(button(txt, ic, slot=lambda checked=False, fn=fn: fn(self)))
        hb.addStretch()
        ok = button("Tamam", obj="Primary", slot=self.accept)
        hb.addWidget(ok)
        hb.addWidget(button("İptal", slot=self.reject))
        lay.addLayout(hb)

    def _add(self):
        r = self.table.rowCount()
        self.table.insertRow(r)
        for c in range(self.table.columnCount()):
            self.table.setItem(r, c, QTableWidgetItem(""))

    def _del(self):
        for r in sorted({i.row() for i in self.table.selectedIndexes()}, reverse=True):
            self.table.removeRow(r)

    def _move(self, d):
        r = self.table.currentRow()
        n = r + d
        if r < 0 or n < 0 or n >= self.table.rowCount():
            return
        for c in range(self.table.columnCount()):
            a, b = self.table.takeItem(r, c), self.table.takeItem(n, c)
            self.table.setItem(r, c, b)
            self.table.setItem(n, c, a)
        self.table.setCurrentCell(n, 0)

    def rows(self):
        return [[(self.table.item(r, c).text() if self.table.item(r, c) else "") for c in range(self.table.columnCount())]
                for r in range(self.table.rowCount())]


class TextDialog(QDialog):
    def __init__(self, parent, title, text, monospace=False, actions=None):
        super().__init__(parent)
        self.setWindowTitle(title)
        self.resize(640, 480)
        lay = QVBoxLayout(self)
        lay.addWidget(label(title, "H2"))
        self.edit = QPlainTextEdit(text)
        if monospace:
            f = self.edit.font()
            f.setFamily("Consolas")
            self.edit.setFont(f)
        lay.addWidget(self.edit, 1)
        hb = QHBoxLayout()
        for txt, ic, fn in actions or []:
            hb.addWidget(button(txt, ic, slot=lambda checked=False, fn=fn: fn(self.edit.toPlainText())))
        hb.addStretch()
        hb.addWidget(button("Kapat", obj="Primary", slot=self.accept))
        lay.addLayout(hb)
