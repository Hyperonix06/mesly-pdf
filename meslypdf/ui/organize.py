"""Sayfaları Düzenle görünümü: küçük resim ızgarası, sürükle-bırak sıralama, çoklu seçim,
döndürme, silme, ekleme, çıkarma, boş sayfa tespiti. Değişiklikler 'Uygula' ile tek sürümde kaydedilir."""
from __future__ import annotations

import os

from PySide6.QtCore import Qt, QSize
from PySide6.QtGui import QIcon, QPixmap, QTransform, QPainter, QColor, QPen
from PySide6.QtWidgets import (QWidget, QVBoxLayout, QHBoxLayout, QListWidget, QListWidgetItem, QAbstractItemView,
                               QToolButton, QLabel, QSlider, QMenu)

from ..core import pages as pg, convert
from . import theme
from .common import button, label, toast, open_files, save_file, confirm
from .pageview import pil_to_qpixmap


class Organizer(QWidget):
    def __init__(self, tab):
        super().__init__()
        self.tab = tab
        v = QVBoxLayout(self)
        v.setContentsMargins(0, 0, 0, 0)
        v.setSpacing(0)
        bar = QWidget()
        bar.setObjectName("OrgBar")
        bar.setAttribute(Qt.WA_StyledBackground, True)
        bar.setStyleSheet(f"#OrgBar {{ background:{theme.C['surface']}; border-bottom:1px solid {theme.C['border']}; }}")
        h = QHBoxLayout(bar)
        h.setContentsMargins(12, 8, 12, 8)
        h.addWidget(label("Sayfaları düzenle", "H3"))
        self.info = label("", "Muted")
        h.addWidget(self.info)
        h.addStretch()

        def tb(ic, tip, fn):
            b = QToolButton()
            b.setIcon(theme.icon(ic))
            b.setIconSize(QSize(20, 20))
            b.setToolTip(tip)
            b.setAutoRaise(True)
            b.clicked.connect(fn)
            h.addWidget(b)
            return b
        tb("mdi6.rotate-left", "Sola döndür", lambda: self.rotate(-90))
        tb("mdi6.rotate-right", "Sağa döndür", lambda: self.rotate(90))
        tb("mdi6.delete-outline", "Sil", self.delete)
        tb("mdi6.content-duplicate", "Çoğalt", self.duplicate)
        tb("mdi6.file-plus-outline", "Dosyadan sayfa ekle", self.insert_file)
        tb("mdi6.file-outline", "Boş sayfa ekle", self.insert_blank)
        tb("mdi6.file-export-outline", "Seçilenleri ayrı PDF olarak kaydet", self.extract)
        tb("mdi6.file-hidden", "Boş sayfaları seç", self.select_blank)
        tb("mdi6.sort-reverse-variant", "Sırayı ters çevir", self.reverse)
        tb("mdi6.select-all", "Tümünü seç", lambda: self.list.selectAll())
        self.size = QSlider(Qt.Horizontal)
        self.size.setRange(90, 320)
        self.size.setValue(170)
        self.size.setFixedWidth(110)
        self.size.valueChanged.connect(self._resize)
        h.addWidget(self.size)
        h.addSpacing(10)
        h.addWidget(button("İptal", slot=self.cancel))
        h.addWidget(button("Uygula", "mdi6.check", obj="Primary", slot=self.apply))
        v.addWidget(bar)
        self.list = QListWidget()
        self.list.setViewMode(QListWidget.IconMode)
        self.list.setResizeMode(QListWidget.Adjust)
        self.list.setMovement(QListWidget.Snap)
        self.list.setDragDropMode(QAbstractItemView.InternalMove)
        self.list.setDefaultDropAction(Qt.MoveAction)
        self.list.setSelectionMode(QAbstractItemView.ExtendedSelection)
        self.list.setSpacing(14)
        self.list.setUniformItemSizes(True)
        self.list.setStyleSheet(f"QListWidget {{ background:{theme.C['canvas']}; border:none; padding: 10px; }}"
                                f"QListWidget::item:selected {{ background: {theme.C['primary_soft']}; border: 2px solid {theme.C['primary']}; border-radius: 8px; }}")
        self.list.setContextMenuPolicy(Qt.CustomContextMenu)
        self.list.customContextMenuRequested.connect(self._menu)
        self.list.itemSelectionChanged.connect(self._update_info)
        self.list.model().rowsMoved.connect(lambda *a: self._renumber())
        v.addWidget(self.list, 1)
        self._base = {}
        self.extra_files: dict[str, str] = {}
        self._load()

    def _load(self):
        doc = self.tab.doc
        for i in range(doc.page_count):
            w, h = doc.page_size(i)
            img = doc.render(i, scale=300 / max(w, h))
            pm = pil_to_qpixmap(img)
            self._add_item(("src", i), pm, 0)
        self._resize(self.size.value())
        self._renumber()

    def _add_item(self, ref, pm: QPixmap, rot: int, row: int | None = None):
        it = QListWidgetItem()
        it.setData(Qt.UserRole, {"ref": ref, "rot": rot})
        key = id(it)
        self._base[key] = pm
        it.setData(Qt.UserRole + 1, key)
        self._set_icon(it)
        if row is None:
            self.list.addItem(it)
        else:
            self.list.insertItem(row, it)
        return it

    def _set_icon(self, it):
        d = it.data(Qt.UserRole)
        pm = self._base[it.data(Qt.UserRole + 1)]
        if d["rot"]:
            pm = pm.transformed(QTransform().rotate(d["rot"]), Qt.SmoothTransformation)
        it.setIcon(QIcon(pm))

    def _resize(self, s):
        self.list.setIconSize(QSize(s, s))
        self.list.setGridSize(QSize(s + 30, s + 46))

    def _renumber(self):
        for r in range(self.list.count()):
            it = self.list.item(r)
            d = it.data(Qt.UserRole)
            suffix = "" if d["ref"][0] == "src" and d["ref"][1] == r else " •"
            it.setText(f"{r + 1}{suffix}")
            it.setTextAlignment(Qt.AlignHCenter)
        self._update_info()

    def _update_info(self):
        n = len(self.list.selectedItems())
        self.info.setText(f"  {self.list.count()} sayfa" + (f" · {n} seçili" if n else ""))

    def _selected_or_all(self):
        s = self.list.selectedItems()
        return s

    def rotate(self, ang):
        for it in self.list.selectedItems():
            d = it.data(Qt.UserRole)
            d["rot"] = (d["rot"] + ang) % 360
            it.setData(Qt.UserRole, d)
            self._set_icon(it)

    def delete(self):
        sel = self.list.selectedItems()
        if len(sel) >= self.list.count():
            toast(self, "Tüm sayfalar silinemez", "warn")
            return
        for it in sel:
            self.list.takeItem(self.list.row(it))
        self._renumber()

    def duplicate(self):
        for it in self.list.selectedItems():
            d = it.data(Qt.UserRole)
            self._add_item(d["ref"], self._base[it.data(Qt.UserRole + 1)], d["rot"], self.list.row(it) + 1)
        self._renumber()

    def _insert_row(self):
        sel = self.list.selectedItems()
        return (max(self.list.row(i) for i in sel) + 1) if sel else self.list.count()

    def insert_blank(self):
        w, h = self.tab.doc.page_size(0)
        pm = QPixmap(int(300 * w / max(w, h)), int(300 * h / max(w, h)))
        pm.fill(QColor("white"))
        self._add_item(("blank", (w, h)), pm, 0, self._insert_row())
        self._renumber()

    def insert_file(self):
        fs = open_files(self, "Eklenecek dosyalar")
        if not fs:
            return
        from ..core.document import PdfDocument
        row = self._insert_row()
        for f in fs:
            pdf = os.path.join(self.tab.workdir, f"ins_{len(self.extra_files)}.pdf")
            convert.any_to_pdf([f], pdf)
            self.extra_files[pdf] = f
            with PdfDocument(pdf) as d:
                for i in range(d.page_count):
                    w, h = d.page_size(i)
                    pm = pil_to_qpixmap(d.render(i, scale=300 / max(w, h)))
                    self._add_item(("file", pdf, i), pm, 0, row)
                    row += 1
        self._renumber()

    def select_blank(self):
        blanks = set(pg.detect_blank_pages(self.tab.path))
        self.list.clearSelection()
        n = 0
        for r in range(self.list.count()):
            d = self.list.item(r).data(Qt.UserRole)
            if (d["ref"][0] == "src" and d["ref"][1] in blanks) or d["ref"][0] == "blank":
                self.list.item(r).setSelected(True)
                n += 1
        toast(self, f"{n} boş sayfa seçildi" if n else "Boş sayfa bulunamadı", "info")

    def reverse(self):
        items = [self.list.takeItem(0) for _ in range(self.list.count())]
        for it in reversed(items):
            self.list.addItem(it)
        self._renumber()

    def extract(self):
        sel = sorted(self.list.selectedItems(), key=self.list.row)
        if not sel:
            toast(self, "Önce sayfa seçin", "warn")
            return
        out = save_file(self, "Seçili sayfaları kaydet", "secili_sayfalar.pdf")
        if out:
            self._build(out, sel)
            toast(self, f"{len(sel)} sayfa kaydedildi", "ok")

    def _menu(self, pos):
        m = QMenu(self)
        m.addAction(theme.icon("mdi6.rotate-right"), "Sağa döndür", lambda: self.rotate(90))
        m.addAction(theme.icon("mdi6.rotate-left"), "Sola döndür", lambda: self.rotate(-90))
        m.addAction(theme.icon("mdi6.delete-outline"), "Sil", self.delete)
        m.addAction(theme.icon("mdi6.content-duplicate"), "Çoğalt", self.duplicate)
        m.addAction(theme.icon("mdi6.file-export-outline"), "Ayrı PDF olarak kaydet…", self.extract)
        m.exec(self.list.mapToGlobal(pos))

    def _build(self, out, items):
        import pikepdf
        src = pikepdf.open(self.tab.path)
        extras = {}
        dst = pikepdf.new()
        for it in items:
            d = it.data(Qt.UserRole)
            ref = d["ref"]
            if ref[0] == "src":
                dst.pages.append(src.pages[ref[1]])
            elif ref[0] == "file":
                if ref[1] not in extras:
                    extras[ref[1]] = pikepdf.open(ref[1])
                dst.pages.append(extras[ref[1]].pages[ref[2]])
            else:
                dst.add_blank_page(page_size=ref[1])
            if d["rot"]:
                dst.pages[-1].rotate(d["rot"], relative=True)
        try:
            with src.open_outline() as _:
                pass
        except Exception:
            pass
        dst.save(out)
        for e in extras.values():
            e.close()
        src.close()

    def apply(self):
        items = [self.list.item(r) for r in range(self.list.count())]
        unchanged = all(it.data(Qt.UserRole)["ref"] == ("src", r) and not it.data(Qt.UserRole)["rot"] for r, it in enumerate(items)) \
            and len(items) == self.tab.doc.page_count
        if unchanged:
            self.tab.close_organizer()
            return
        self.tab.apply("Sayfa düzeni güncellendi", lambda src, out: self._build(out, items))
        self.tab.close_organizer()

    def cancel(self):
        self.tab.close_organizer()
