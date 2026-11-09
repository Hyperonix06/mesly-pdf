"""Ayarlar ekranı."""
from __future__ import annotations

import os
import sys

from PySide6.QtCore import Qt, Signal
from PySide6.QtWidgets import (QWidget, QVBoxLayout, QHBoxLayout, QFormLayout, QLineEdit, QCheckBox, QComboBox, QSpinBox,
                               QGroupBox, QListWidget, QListWidgetItem, QScrollArea, QMessageBox, QLabel)

from .. import __version__
from ..core import ocr as ocrmod, integration
from . import theme
from .common import Settings, LangPicker, button, label, toast, run_task, pick_dir, open_path


class SettingsView(QWidget):
    titleChanged = Signal()
    title = "Ayarlar"
    themeChanged = Signal(bool)

    def __init__(self, main):
        super().__init__()
        self.main = main
        sc = QScrollArea(self)
        sc.setWidgetResizable(True)
        host = QWidget()
        host.setObjectName("Page")
        v = QVBoxLayout(host)
        v.setContentsMargins(36, 26, 36, 26)
        v.setSpacing(14)
        v.addWidget(label("Ayarlar", "H1"))

        g = QGroupBox("Genel")
        f = QFormLayout(g)
        self.author = QLineEdit(Settings.get("author") or "")
        self.author.setPlaceholderText("Yorumlarda ve imzalarda görünen ad")
        self.author.editingFinished.connect(lambda: Settings.set("author", self.author.text()))
        f.addRow("Kullanıcı adı", self.author)
        self.dark = QCheckBox("Koyu tema")
        self.dark.setChecked(Settings.get("dark"))
        self.dark.toggled.connect(self._theme)
        f.addRow("", self.dark)
        out_row = QHBoxLayout()
        self.out_dir = QLineEdit(Settings.get("out_dir") or "")
        self.out_dir.setPlaceholderText("Boş = son kullanılan klasör")
        self.out_dir.editingFinished.connect(lambda: Settings.set("out_dir", self.out_dir.text()))
        out_row.addWidget(self.out_dir)
        out_row.addWidget(button("Seç…", slot=self._pick_out))
        f.addRow("Varsayılan kayıt klasörü", out_row)
        v.addWidget(g)

        g = QGroupBox("OCR ve dönüştürme")
        f = QFormLayout(g)
        self.lang = LangPicker()
        self.lang.menu_.triggered.connect(lambda *_: Settings.set("ocr_lang", self.lang.value()))
        f.addRow("Varsayılan OCR dili", self.lang)
        self.dpi = QSpinBox()
        self.dpi.setRange(150, 600)
        self.dpi.setSingleStep(50)
        self.dpi.setValue(int(Settings.get("dpi")))
        self.dpi.valueChanged.connect(lambda x: Settings.set("dpi", x))
        f.addRow("Tanıma çözünürlüğü (dpi)", self.dpi)
        self.mode = QComboBox()
        self.mode.addItem("Düzenlenebilir kopya (biçimlendirme korunur)", "formatted")
        self.mode.addItem("Düz metin", "plain")
        self.mode.setCurrentIndex(0 if Settings.get("export_mode") == "formatted" else 1)
        self.mode.currentIndexChanged.connect(lambda i: Settings.set("export_mode", self.mode.currentData()))
        f.addRow("Word dışa aktarma", self.mode)
        self.autopre = QCheckBox("OCR öncesi görüntüleri otomatik iyileştir")
        self.autopre.setChecked(Settings.get("auto_preprocess", True))
        self.autopre.toggled.connect(lambda c: Settings.set("auto_preprocess", c))
        f.addRow("", self.autopre)
        self.autorec = QCheckBox("OCR Düzenleyiciye eklenen sayfaları otomatik tanı")
        self.autorec.setChecked(Settings.get("auto_recognize", True))
        self.autorec.toggled.connect(lambda c: Settings.set("auto_recognize", c))
        f.addRow("", self.autorec)
        st = "hazır · sürüm " + str(ocrmod.pytesseract.get_tesseract_version()) if ocrmod.tesseract_ok() else "BULUNAMADI"
        f.addRow("OCR motoru", label(f"Tesseract {st}", "Muted"))
        v.addWidget(g)

        g = QGroupBox("OCR dil paketleri")
        gv = QVBoxLayout(g)
        gv.addWidget(label("Yüklü diller aşağıda listelenir. Ek dilleri (100+ dil) GitHub tessdata deposundan indirebilirsiniz.", "Muted", True))
        self.langs = QListWidget()
        self.langs.setMaximumHeight(150)
        gv.addWidget(self.langs)
        hb = QHBoxLayout()
        self.dl = QComboBox()
        self.dl.setEditable(True)
        for code, name in sorted(ocrmod.LANG_NAMES.items(), key=lambda x: x[1]):
            self.dl.addItem(f"{name} ({code})", code)
        hb.addWidget(self.dl, 1)
        hb.addWidget(button("İndir ve yükle", "mdi6.download", slot=self._download))
        gv.addLayout(hb)
        v.addWidget(g)
        self._fill_langs()

        g = QGroupBox("Windows entegrasyonu")
        gv = QVBoxLayout(g)
        gv.addWidget(label("Gezgin'de dosyalara sağ tıklayınca 'Mesly PDF ile aç / Word'e dönüştür / Aranabilir PDF yap / Karşılaştır' "
                           "komutları, 'Birlikte aç' listesi ve 'Gönder' menüsü.", "Muted", True))
        hb = QHBoxLayout()
        b1 = button("Entegrasyonu etkinleştir", "mdi6.microsoft-windows", slot=self._integrate)
        b2 = button("Kaldır", slot=self._unintegrate)
        self.autostart = QCheckBox("Windows açılışında Hot Folder hizmetini başlat")
        self.autostart.setChecked(Settings.get("hotfolder_autostart"))
        self.autostart.toggled.connect(self._autostart)
        for w in (b1, b2, self.autostart):
            w.setEnabled(integration.is_windows())
            hb.addWidget(w)
        hb.addStretch()
        gv.addLayout(hb)
        if not integration.is_windows():
            gv.addWidget(label("(Bu bölüm yalnızca Windows'ta etkindir.)", "Small"))
        v.addWidget(g)

        g = QGroupBox("Hakkında")
        gv = QVBoxLayout(g)
        gv.addWidget(label(f"Mesly PDF {__version__} — PDF düzenleme, OCR, dönüştürme, karşılaştırma ve otomasyon.", "H3"))
        gv.addWidget(label("Açık kaynak bileşenler: PDFium (pypdfium2), qpdf (pikepdf), Tesseract OCR, pyHanko, OpenCV, Qt (PySide6), "
                           "reportlab, python-docx, openpyxl, python-pptx, odfpy, zxing-cpp, DejaVu yazı tipleri.", "Muted", True))
        hb = QHBoxLayout()
        hb.addWidget(button("Veri klasörünü aç", "mdi6.folder-cog-outline", slot=lambda: open_path(ocrmod.app_data_dir())))
        hb.addStretch()
        gv.addLayout(hb)
        v.addWidget(g)
        v.addStretch()
        sc.setWidget(host)
        lay = QVBoxLayout(self)
        lay.setContentsMargins(0, 0, 0, 0)
        lay.addWidget(sc)

    def close_tab(self):
        return True

    def _fill_langs(self):
        self.langs.clear()
        for l in ocrmod.available_languages():
            self.langs.addItem(QListWidgetItem(theme.icon("mdi6.check", theme.C["success"]), f"{ocrmod.LANG_NAMES.get(l, l)}  ({l})"))

    def _download(self):
        code = self.dl.currentData() or self.dl.currentText().strip()
        run_task(self, f"{code} dil paketi indiriliyor…", lambda progress=None: ocrmod.download_language(code, lambda p: progress and progress(p, 100)),
                 on_done=lambda _: (self._fill_langs(), toast(self, "Dil paketi yüklendi", "ok")))

    def _pick_out(self):
        d = pick_dir(self, "Varsayılan kayıt klasörü")
        if d:
            self.out_dir.setText(d)
            Settings.set("out_dir", d)

    def _theme(self, dark):
        Settings.set("dark", dark)
        QMessageBox.information(self, "Tema", "Tema değişikliği uygulamayı yeniden başlattığınızda tam olarak uygulanır.")
        self.themeChanged.emit(dark)

    def _integrate(self):
        try:
            done = integration.register()
            toast(self, "Etkinleştirildi: " + ", ".join(done), "ok")
        except Exception as e:
            QMessageBox.critical(self, "Hata", str(e))

    def _unintegrate(self):
        integration.unregister()
        toast(self, "Windows entegrasyonu kaldırıldı", "ok")

    def _autostart(self, c):
        Settings.set("hotfolder_autostart", c)
        try:
            integration.set_autostart(c)
        except Exception as e:
            QMessageBox.warning(self, "Hata", str(e))
