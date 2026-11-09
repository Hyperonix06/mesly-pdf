"""Başlangıç ekranı: görev kartları, sürükle-bırak alanı, son kullanılanlar."""
from __future__ import annotations

import os

from PySide6.QtCore import Qt, QSize, Signal
from PySide6.QtWidgets import (QWidget, QVBoxLayout, QHBoxLayout, QGridLayout, QPushButton, QLabel, QScrollArea, QFrame,
                               QListWidget, QListWidgetItem, QButtonGroup, QToolButton)

from .. import __version__
from . import theme
from .common import Settings, label

GROUPS = [
    ("open", "Aç", [
        ("mdi6.file-pdf-box", "#D93025", "PDF Düzenleyicide aç", "Görüntüle, düzenle, yorum ekle, koru", "open_pdf"),
        ("mdi6.text-box-search-outline", "#2457D6", "OCR Düzenleyicide aç", "Taramaları gelişmiş tanıma ile işleyin", "open_ocr"),
        ("mdi6.file-word-outline", "#2B579A", "Word belgesine dönüştür", "PDF ve taramalardan düzenlenebilir DOCX", "to_docx"),
        ("mdi6.file-excel-outline", "#217346", "Excel'e dönüştür", "Tabloları elektronik tabloya aktarın", "to_xlsx"),
        ("mdi6.file-powerpoint-outline", "#B7472A", "PowerPoint'e dönüştür", "Sayfaları birebir düzenle slayta", "to_pptx"),
        ("mdi6.file-export-outline", "#5B6577", "Diğer biçimlere dönüştür", "ODT, RTF, TXT, CSV, HTML, EPUB, FB2", "to_other"),
    ]),
    ("new", "PDF Oluştur", [
        ("mdi6.file-plus-outline", "#D93025", "Dosyalardan PDF oluştur", "Office, görüntü, metin — tek PDF", "create_pdf"),
        ("mdi6.file-search-outline", "#2457D6", "Aranabilir PDF oluştur", "Taramaları OCR ile aranabilir yapın", "searchable"),
        ("mdi6.archive-outline", "#6A1B9A", "PDF/A arşivi oluştur", "Uzun süreli saklama standardı", "pdfa"),
        ("mdi6.file-merge", "#00838F", "PDF'leri birleştir", "Birden çok dosyayı sırayla birleştirin", "merge"),
        ("mdi6.file-outline", "#5B6577", "Boş PDF", "Sıfırdan sayfa veya form oluşturun", "blank"),
        ("mdi6.zip-box-outline", "#EF6C00", "PDF'i sıkıştır", "Dosya boyutunu küçültün", "compress"),
    ]),
    ("scan", "Tara", [
        ("mdi6.scanner", "#2457D6", "Tara ve OCR Düzenleyicide aç", "Tarayıcıdan sayfa alın", "scan_ocr"),
        ("mdi6.file-pdf-box", "#D93025", "Tara ve PDF olarak kaydet", "Aranabilir PDF'e tarayın", "scan_pdf"),
        ("mdi6.file-word-outline", "#2B579A", "Tara ve Word'e aktar", "Taramadan düzenlenebilir belge", "scan_docx"),
        ("mdi6.cellphone-screenshot", "#00838F", "Ekrandan metin yakala", "Ekranın bir bölümünü OCR'layın", "screen_text"),
        ("mdi6.table-search", "#217346", "Ekrandan tablo yakala", "Ekrandaki tabloyu Excel'e kopyalayın", "screen_table"),
    ]),
    ("compare", "Karşılaştır", [
        ("mdi6.compare", "#2457D6", "Belgeleri karşılaştır", "İki sürüm arasındaki farkları bulun", "compare"),
    ]),
    ("auto", "Otomasyon", [
        ("mdi6.folder-sync-outline", "#2457D6", "Hot Folder", "Klasörleri izleyip otomatik işleyin", "hotfolder"),
        ("mdi6.playlist-play", "#6A1B9A", "Otomatik görevler", "Kendi iş akışlarınızı oluşturun", "tasks"),
    ]),
]


class ActionCard(QPushButton):
    def __init__(self, ic, color, title, sub, key, on_click):
        super().__init__()
        self.setObjectName("ActionCard")
        self.setCursor(Qt.PointingHandCursor)
        self.setMinimumSize(250, 74)
        h = QHBoxLayout(self)
        h.setContentsMargins(14, 10, 14, 10)
        h.setSpacing(12)
        badge = QLabel()
        badge.setFixedSize(42, 42)
        badge.setAlignment(Qt.AlignCenter)
        from PySide6.QtGui import QColor
        c = QColor(color)
        badge.setStyleSheet(f"background: rgba({c.red()},{c.green()},{c.blue()},26); border-radius: 10px;")
        badge.setPixmap(theme.icon(ic, color).pixmap(24, 24))
        h.addWidget(badge)
        v = QVBoxLayout()
        v.setSpacing(1)
        t = QLabel(title)
        t.setStyleSheet("font-weight: 600; font-size: 13px; background: transparent;")
        s = QLabel(sub)
        s.setStyleSheet(f"color: {theme.C['text2']}; font-size: 11px; background: transparent;")
        s.setWordWrap(True)
        v.addWidget(t)
        v.addWidget(s)
        h.addLayout(v, 1)
        for w in (t, s, badge):
            w.setAttribute(Qt.WA_TransparentForMouseEvents)
        self.clicked.connect(lambda: on_click(key))


class DropZone(QFrame):
    dropped = Signal(list)

    def __init__(self):
        super().__init__()
        self.setObjectName("DropZone")
        self.setAcceptDrops(True)
        self.setMinimumHeight(96)
        h = QHBoxLayout(self)
        ic = QLabel()
        ic.setPixmap(theme.icon("mdi6.tray-arrow-down", theme.C["primary"]).pixmap(34, 34))
        h.addStretch()
        h.addWidget(ic)
        v = QVBoxLayout()
        v.addWidget(label("Dosyaları buraya sürükleyin", "H3"))
        v.addWidget(label("PDF'ler düzenleyicide, görüntüler OCR Düzenleyicide açılır", "Muted"))
        h.addLayout(v)
        h.addStretch()

    def dragEnterEvent(self, e):
        if e.mimeData().hasUrls():
            e.acceptProposedAction()
            self.setProperty("hover", True)
            self.style().polish(self)

    def dragLeaveEvent(self, e):
        self.setProperty("hover", False)
        self.style().polish(self)

    def dropEvent(self, e):
        self.setProperty("hover", False)
        self.style().polish(self)
        files = [u.toLocalFile() for u in e.mimeData().urls() if u.isLocalFile()]
        if files:
            self.dropped.emit(files)


class StartPage(QWidget):
    action = Signal(str)
    openRecent = Signal(str)
    filesDropped = Signal(list)
    title = "Başlangıç"

    def __init__(self):
        super().__init__()
        self.setObjectName("Page")
        outer = QHBoxLayout(self)
        outer.setContentsMargins(0, 0, 0, 0)
        outer.setSpacing(0)
        # sol: grup seçici
        side = QFrame()
        side.setFixedWidth(220)
        side.setObjectName("StartSide")
        side.setStyleSheet(f"#StartSide {{ background: {theme.C['surface']}; border-right: 1px solid {theme.C['border']}; }}")
        sv = QVBoxLayout(side)
        sv.setContentsMargins(14, 24, 14, 14)
        sv.setSpacing(4)
        self.group_btns = QButtonGroup(self)
        icons = {"open": "mdi6.folder-open-outline", "new": "mdi6.file-plus-outline", "scan": "mdi6.scanner",
                 "compare": "mdi6.compare", "auto": "mdi6.robot-outline", "recent": "mdi6.history"}
        for k, (key, name, _) in enumerate(GROUPS + [("recent", "Son kullanılanlar", [])]):
            b = QToolButton()
            b.setText("  " + name)
            b.setIcon(theme.icon(icons[key]))
            b.setIconSize(QSize(20, 20))
            b.setToolButtonStyle(Qt.ToolButtonTextBesideIcon)
            b.setCheckable(True)
            b.setCursor(Qt.PointingHandCursor)
            b.setMinimumHeight(40)
            b.setSizePolicy(b.sizePolicy().horizontalPolicy().Expanding, b.sizePolicy().verticalPolicy())
            b.setStyleSheet(f"QToolButton {{ text-align: left; border: none; border-radius: 8px; padding: 6px 10px; font-size: 13px; }}"
                            f"QToolButton:hover {{ background: {theme.C['surface2']}; }}"
                            f"QToolButton:checked {{ background: {theme.C['primary_soft']}; color: {theme.C['primary']}; font-weight: 600; }}")
            self.group_btns.addButton(b, k)
            sv.addWidget(b)
        sv.addStretch()
        ver = label(f"Mesly PDF {__version__}", "Small")
        sv.addWidget(ver)
        outer.addWidget(side)
        # sağ: içerik
        sc = QScrollArea()
        sc.setWidgetResizable(True)
        host = QWidget()
        host.setObjectName("Page")
        self.v = QVBoxLayout(host)
        self.v.setContentsMargins(36, 28, 36, 28)
        self.v.setSpacing(14)
        self.h1 = label("", "H1")
        self.sub = label("", "Muted")
        self.v.addWidget(self.h1)
        self.v.addWidget(self.sub)
        self.drop = DropZone()
        self.drop.dropped.connect(self.filesDropped.emit)
        self.v.addWidget(self.drop)
        self.grid_host = QWidget()
        self.grid = QGridLayout(self.grid_host)
        self.grid.setSpacing(12)
        self.grid.setContentsMargins(0, 6, 0, 0)
        self.v.addWidget(self.grid_host)
        self.recent = QListWidget()
        self.recent.setIconSize(QSize(22, 22))
        self.recent.itemActivated.connect(lambda it: self.openRecent.emit(it.data(Qt.UserRole)))
        self.recent.itemClicked.connect(lambda it: self.openRecent.emit(it.data(Qt.UserRole)))
        self.v.addWidget(self.recent, 1)
        self.v.addStretch()
        sc.setWidget(host)
        outer.addWidget(sc, 1)
        self.group_btns.idClicked.connect(self.show_group)
        self.group_btns.button(0).setChecked(True)
        self.show_group(0)

    def show_group(self, k):
        while self.grid.count():
            w = self.grid.takeAt(0).widget()
            if w:
                w.deleteLater()
        if k >= len(GROUPS):
            self.h1.setText("Son kullanılanlar")
            self.sub.setText("Son açtığınız belgeler")
            self.grid_host.hide()
            self.drop.hide()
            self.recent.show()
            self.refresh_recent()
            return
        self.recent.hide()
        self.grid_host.show()
        self.drop.show()
        key, name, cards = GROUPS[k]
        subs = {"open": "Bir belge açın veya dönüştürün", "new": "Farklı kaynaklardan PDF oluşturun",
                "scan": "Kağıt belgeleri ve ekran içeriğini dijitalleştirin", "compare": "Farklı biçimlerdeki iki belgeyi karşılaştırın",
                "auto": "Belge işlerini otomatikleştirin"}
        self.h1.setText(name)
        self.sub.setText(subs.get(key, ""))
        for i, (ic, col, t, s, akey) in enumerate(cards):
            self.grid.addWidget(ActionCard(ic, col, t, s, akey, self.action.emit), i // 2, i % 2)

    def refresh_recent(self):
        self.recent.clear()
        for p in Settings.get("recent", []):
            if not os.path.exists(p):
                continue
            ic = "mdi6.file-pdf-box" if p.lower().endswith(".pdf") else ("mdi6.text-box-search-outline" if p.endswith(".meslyocr") else "mdi6.file-outline")
            it = QListWidgetItem(theme.icon(ic, "#D93025" if p.lower().endswith(".pdf") else None), f"{os.path.basename(p)}\n{os.path.dirname(p)}")
            it.setData(Qt.UserRole, p)
            self.recent.addItem(it)
        if not self.recent.count():
            it = QListWidgetItem("Henüz belge açılmadı")
            it.setFlags(Qt.NoItemFlags)
            self.recent.addItem(it)
