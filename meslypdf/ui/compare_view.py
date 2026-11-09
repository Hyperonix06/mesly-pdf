"""Belge Karşılaştırma ekranı: iki belge yan yana, fark listesi, eşzamanlı kaydırma, dışa aktarma."""
from __future__ import annotations

import os

from PySide6.QtCore import Qt, Signal
from PySide6.QtGui import QColor, QBrush, QPen
from PySide6.QtWidgets import (QWidget, QVBoxLayout, QHBoxLayout, QSplitter, QListWidget, QListWidgetItem, QCheckBox,
                               QLabel, QFrame, QComboBox, QToolButton, QMenu, QGraphicsRectItem, QSizePolicy, QPushButton)

from ..core import compare as cmp
from ..core.document import PdfDocument
from . import theme
from .common import (run_task, toast, Settings, button, label, open_files, save_file, LangPicker, open_path, pick_dir,
                     ALL_SUPPORTED)
from .pageview import PageView

KIND_COLORS = {"Eklendi": "#2E7D32", "Silindi": "#C62828", "Değiştirildi": "#E8A200"}


class DocSlot(QFrame):
    changed = Signal()

    def __init__(self, title):
        super().__init__()
        self.setObjectName("Card")
        self.path = ""
        h = QHBoxLayout(self)
        h.setContentsMargins(12, 8, 8, 8)
        ic = QLabel()
        ic.setPixmap(theme.icon("mdi6.file-document-outline", theme.C["primary"]).pixmap(26, 26))
        h.addWidget(ic)
        v = QVBoxLayout()
        v.setSpacing(0)
        v.addWidget(label(title, "Small"))
        self.name = label("Belge seçin…", "H3")
        v.addWidget(self.name)
        h.addLayout(v, 1)
        h.addWidget(button("Seç…", "mdi6.folder-open-outline", slot=self.pick))
        self.setAcceptDrops(True)

    def pick(self):
        f = open_files(self, "Belge seç", ALL_SUPPORTED, multi=False)
        if f:
            self.set_path(f[0])

    def set_path(self, p):
        self.path = p
        self.name.setText(os.path.basename(p))
        self.name.setToolTip(p)
        self.changed.emit()

    def dragEnterEvent(self, e):
        if e.mimeData().hasUrls():
            e.acceptProposedAction()

    def dropEvent(self, e):
        urls = e.mimeData().urls()
        if urls:
            self.set_path(urls[0].toLocalFile())


class CompareView(QWidget):
    titleChanged = Signal()

    def __init__(self, main, old: str | None = None, new: str | None = None):
        super().__init__()
        self.main = main
        self.result: cmp.CompareResult | None = None
        self._marks = []
        v = QVBoxLayout(self)
        v.setContentsMargins(0, 0, 0, 0)
        v.setSpacing(0)
        top = QWidget()
        top.setObjectName("Ribbon")
        top.setAttribute(Qt.WA_StyledBackground, True)
        th = QHBoxLayout(top)
        th.setContentsMargins(12, 10, 12, 10)
        self.a = DocSlot("ESKİ / ORİJİNAL BELGE")
        self.b = DocSlot("YENİ / DEĞİŞTİRİLMİŞ BELGE")
        sw = QToolButton()
        sw.setIcon(theme.icon("mdi6.swap-horizontal"))
        sw.setToolTip("Yer değiştir")
        sw.setAutoRaise(True)
        sw.clicked.connect(self.swap)
        th.addWidget(self.a, 1)
        th.addWidget(sw)
        th.addWidget(self.b, 1)
        opts = QVBoxLayout()
        self.lang = LangPicker()
        opts.addWidget(self.lang)
        o2 = QHBoxLayout()
        self.ign_case = QCheckBox("Büyük/küçük harf yoksay")
        self.ign_punct = QCheckBox("Noktalamayı yoksay")
        self.ign_hdr = QCheckBox("Üst/altbilgiyi yoksay")
        for c in (self.ign_case, self.ign_punct, self.ign_hdr):
            o2.addWidget(c)
        opts.addLayout(o2)
        th.addLayout(opts)
        self.run_btn = button("Karşılaştır", "mdi6.compare", obj="Primary", slot=self.run)
        self.run_btn.setMinimumHeight(40)
        th.addWidget(self.run_btn)
        v.addWidget(top)

        split = QSplitter(Qt.Horizontal)
        self.va = PageView()
        self.vb = PageView()
        for pv, t in ((self.va, "Eski"), (self.vb, "Yeni")):
            w = QWidget()
            l = QVBoxLayout(w)
            l.setContentsMargins(0, 0, 0, 0)
            l.setSpacing(0)
            l.addWidget(pv)
            split.addWidget(w)
        side = QWidget()
        side.setObjectName("RightPanel")
        side.setAttribute(Qt.WA_StyledBackground, True)
        sv = QVBoxLayout(side)
        sv.setContentsMargins(10, 10, 10, 10)
        hh = QHBoxLayout()
        hh.addWidget(label("Farklar", "H2"))
        hh.addStretch()
        self.export_btn = QToolButton()
        self.export_btn.setText("Dışa aktar")
        self.export_btn.setIcon(theme.icon("mdi6.export"))
        self.export_btn.setToolButtonStyle(Qt.ToolButtonTextBesideIcon)
        self.export_btn.setPopupMode(QToolButton.InstantPopup)
        m = QMenu(self.export_btn)
        m.addAction(theme.icon("mdi6.file-word-outline", "#2B579A"), "Word – değişiklik izleme (.docx)", lambda: self.export("docx"))
        m.addAction(theme.icon("mdi6.file-excel-outline", "#217346"), "Fark tablosu (.xlsx)", lambda: self.export("xlsx"))
        m.addAction(theme.icon("mdi6.file-pdf-box", "#D93025"), "İşaretli PDF'ler", lambda: self.export("pdf"))
        m.addAction(theme.icon("mdi6.text"), "Metin raporu (.txt)", lambda: self.export("txt"))
        self.export_btn.setMenu(m)
        self.export_btn.setEnabled(False)
        hh.addWidget(self.export_btn)
        sv.addLayout(hh)
        self.summary = label("İki belge seçip 'Karşılaştır'a basın. PDF, taranmış PDF, görüntü, Word, Excel ve metin dosyaları birbirleriyle karşılaştırılabilir.", "Muted", True)
        sv.addWidget(self.summary)
        self.filter = QComboBox()
        self.filter.addItem("Tüm farklar", None)
        for k in ("Değiştirildi", "Eklendi", "Silindi"):
            self.filter.addItem(k, k)
        self.filter.currentIndexChanged.connect(self._fill)
        sv.addWidget(self.filter)
        self.list = QListWidget()
        self.list.setWordWrap(True)
        self.list.setStyleSheet("QListWidget::item { border-bottom: 1px solid %s; padding: 8px; }" % theme.C["border"])
        self.list.currentRowChanged.connect(self._select)
        sv.addWidget(self.list, 1)
        self.sync = QCheckBox("Kaydırmayı eşitle")
        self.sync.setChecked(True)
        sv.addWidget(self.sync)
        split.addWidget(side)
        split.setSizes([520, 520, 360])
        v.addWidget(split, 1)
        self.va.verticalScrollBar().valueChanged.connect(lambda val: self._sync(self.va, self.vb))
        self.vb.verticalScrollBar().valueChanged.connect(lambda val: self._sync(self.vb, self.va))
        self._syncing = False
        for s in (self.a, self.b):
            s.changed.connect(self._preview)
        if old:
            self.a.set_path(old)
        if new:
            self.b.set_path(new)

    @property
    def title(self):
        if self.a.path and self.b.path:
            return f"Karşılaştır · {os.path.basename(self.a.path)} ↔ {os.path.basename(self.b.path)}"
        return "Belge karşılaştırma"

    def close_tab(self):
        return True

    def swap(self):
        pa, pb = self.a.path, self.b.path
        if pb:
            self.a.set_path(pb)
        if pa:
            self.b.set_path(pa)

    def _preview(self):
        self.titleChanged.emit()
        for slot, view in ((self.a, self.va), (self.b, self.vb)):
            if slot.path and slot.path.lower().endswith(".pdf") and (view.doc is None or view.doc.path != os.path.abspath(slot.path)):
                try:
                    view.set_document(PdfDocument(slot.path))
                except Exception:
                    pass

    def _sync(self, src, dst):
        if not self.sync.isChecked() or self._syncing or not src.doc or not dst.doc:
            return
        self._syncing = True
        sb, db = src.verticalScrollBar(), dst.verticalScrollBar()
        if sb.maximum():
            db.setValue(int(sb.value() / sb.maximum() * db.maximum()))
        self._syncing = False

    def run(self):
        if not (self.a.path and self.b.path):
            toast(self, "Önce iki belgeyi de seçin", "warn")
            return

        def done(res):
            self.result = res
            self._marks = []
            self.va.set_document(PdfDocument(res.old_pdf))
            self.vb.set_document(PdfDocument(res.new_pdf))
            self._draw_marks()
            s = res.summary()
            self.summary.setText(f"<b>{s['Toplam']}</b> fark bulundu · "
                                 f"<span style='color:{KIND_COLORS['Değiştirildi']}'>{s['Değiştirildi']} değişiklik</span> · "
                                 f"<span style='color:{KIND_COLORS['Eklendi']}'>{s['Eklendi']} ekleme</span> · "
                                 f"<span style='color:{KIND_COLORS['Silindi']}'>{s['Silindi']} silme</span>"
                                 if s["Toplam"] else "Belgeler arasında metin farkı bulunamadı. ✓")
            self.export_btn.setEnabled(True)
            self._fill()
            self.titleChanged.emit()
        run_task(self, "Belgeler karşılaştırılıyor…", cmp.compare, self.a.path, self.b.path, self.lang.value(),
                 self.ign_case.isChecked(), self.ign_punct.isChecked(), self.ign_hdr.isChecked(), on_done=done)

    def _draw_marks(self):
        if not getattr(self, "_connected", False):
            for view in (self.va, self.vb):
                view.layoutChanged.connect(self._reposition)
            self._connected = True
        self._marks = []
        for d in self.result.diffs:
            col = QColor(KIND_COLORS[d.kind])
            for view, toks in ((self.va, d.old_tokens), (self.vb, d.new_tokens)):
                for t in toks:
                    it = QGraphicsRectItem()
                    fill = QColor(col)
                    fill.setAlpha(70)
                    it.setBrush(QBrush(fill))
                    it.setPen(QPen(col, 0.8))
                    it.setZValue(5)
                    view.scene().addItem(it)
                    self._marks.append((view, it, t.page, t.rect))
        self._reposition()

    def _reposition(self):
        for view, it, page, rect in list(self._marks):
            if view.doc and page < len(view.items_) and it.scene() is view.scene():
                l, b, r, t = rect
                it.setRect(view.pdf_rect_to_scene(page, (l - 1, b - 1, r + 1, t + 1)))

    def _fill(self):
        self.list.clear()
        if not self.result:
            return
        f = self.filter.currentData()
        zone_tr = {"body": "", "header": " · üstbilgi", "footer": " · altbilgi"}
        for k, d in enumerate(self.result.diffs):
            if f and d.kind != f:
                continue
            pg_ = d.new_page if d.new_page is not None else d.old_page
            if d.kind == "Değiştirildi":
                txt = f"“{d.old_text}”  →  “{d.new_text}”"
            elif d.kind == "Eklendi":
                txt = f"+ “{d.new_text}”"
            else:
                txt = f"− “{d.old_text}”"
            it = QListWidgetItem(f"{d.kind} · sayfa {pg_ + 1}{zone_tr[d.zone]}\n{txt}")
            it.setForeground(QColor(theme.C["text"]))
            it.setIcon(theme.icon({"Eklendi": "mdi6.plus-circle", "Silindi": "mdi6.minus-circle", "Değiştirildi": "mdi6.swap-horizontal-circle"}[d.kind],
                                  KIND_COLORS[d.kind]))
            it.setData(Qt.UserRole, k)
            self.list.addItem(it)

    def _select(self, row):
        if row < 0 or not self.result:
            return
        d = self.result.diffs[self.list.item(row).data(Qt.UserRole)]
        self._syncing = True
        if d.old_tokens:
            self.va.go_to(d.old_tokens[0].page, d.old_tokens[0].rect[3])
            self.va.set_selection_rects(d.old_tokens[0].page, [t.rect for t in d.old_tokens])
        if d.new_tokens:
            self.vb.go_to(d.new_tokens[0].page, d.new_tokens[0].rect[3])
            self.vb.set_selection_rects(d.new_tokens[0].page, [t.rect for t in d.new_tokens])
        self._syncing = False

    def export(self, kind):
        if not self.result:
            return
        base = f"karsilastirma_{os.path.splitext(os.path.basename(self.a.path))[0]}"
        if kind == "pdf":
            d = pick_dir(self, "İşaretli PDF'lerin kaydedileceği klasör", os.path.dirname(self.b.path))
            if d:
                o1, o2 = os.path.join(d, base + "_eski.pdf"), os.path.join(d, base + "_yeni.pdf")
                run_task(self, "Dışa aktarılıyor…", cmp.export_annotated_pdfs, self.result, o1, o2,
                         on_done=lambda r: (toast(self, "İşaretli PDF'ler kaydedildi", "ok"), self.main.open_document(o2)))
            return
        filt = {"docx": "Word (*.docx)", "xlsx": "Excel (*.xlsx)", "txt": "Metin (*.txt)"}[kind]
        out = save_file(self, "Karşılaştırma sonucunu kaydet", f"{base}.{kind}", filt)
        if not out:
            return
        if kind == "docx":
            cmp.export_docx_track_changes(self.result, out, Settings.get("author") or "Mesly PDF")
        elif kind == "xlsx":
            cmp.export_xlsx(self.result, out)
        else:
            open(out, "w", encoding="utf-8").write(cmp.export_report_text(self.result))
        toast(self, "Kaydedildi", "ok")
        open_path(out)
