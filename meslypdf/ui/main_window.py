"""Ana pencere: navigasyon rayı, sekmeli çalışma alanı, sistem tepsisi."""
from __future__ import annotations

import os

from PySide6.QtCore import Qt, QSize, QTimer
from PySide6.QtGui import QIcon, QAction, QKeySequence, QShortcut, QPixmap, QPainter, QColor, QFont
from PySide6.QtWidgets import (QMainWindow, QWidget, QHBoxLayout, QVBoxLayout, QToolButton, QTabWidget, QLabel,
                               QButtonGroup, QSystemTrayIcon, QMenu, QMessageBox, QApplication, QTabBar)

from .. import APP_NAME, __version__
from ..core import convert, security, editing, pages as pg, ocr as ocrmod
from ..core.document import is_encrypted
from ..core.hotfolder import HotFolderService
from . import theme
from .common import (Settings, run_task, toast, open_files, save_file, ask_password, confirm, open_path, PDF_FILTER,
                     ALL_SUPPORTED, IMG_FILTER)
from .dialogs import FormDialog
from .start_page import StartPage


def app_icon() -> QIcon:
    from ..core.fonts import asset_path
    p = asset_path("icon.png")
    if os.path.exists(p):
        return QIcon(p)
    pm = QPixmap(256, 256)
    pm.fill(Qt.transparent)
    qp = QPainter(pm)
    qp.setRenderHint(QPainter.Antialiasing)
    qp.setBrush(QColor("#2457D6"))
    qp.setPen(Qt.NoPen)
    qp.drawRoundedRect(16, 16, 224, 224, 48, 48)
    qp.setPen(QColor("white"))
    f = QFont("Segoe UI", 110, QFont.Bold)
    qp.setFont(f)
    qp.drawText(pm.rect(), Qt.AlignCenter, "M")
    qp.end()
    return QIcon(pm)


class MainWindow(QMainWindow):
    def __init__(self):
        super().__init__()
        self.setWindowTitle(APP_NAME)
        self.setWindowIcon(app_icon())
        self.resize(1440, 900)
        self.setMinimumSize(1000, 640)
        self.hotfolder_service = HotFolderService()
        root = QWidget()
        h = QHBoxLayout(root)
        h.setContentsMargins(0, 0, 0, 0)
        h.setSpacing(0)
        h.addWidget(self._nav())
        self.tabs = QTabWidget()
        self.tabs.setObjectName("DocTabs")
        self.tabs.tabBar().setObjectName("DocTabsBar")
        self.tabs.setDocumentMode(True)
        self.tabs.setTabsClosable(False)
        self.tabs.setMovable(True)
        self.tabs.tabCloseRequested.connect(self.close_tab)
        self.tabs.currentChanged.connect(self._tab_changed)
        self.start = StartPage()
        self.start.action.connect(self.on_action)
        self.start.openRecent.connect(self.open_any)
        self.start.filesDropped.connect(self.open_many)
        self.tabs.addTab(self.start, theme.icon("mdi6.home-outline"), "Başlangıç")
        self.tabs.tabBar().setTabButton(0, QTabBar.RightSide, None)
        h.addWidget(self.tabs, 1)
        self.setCentralWidget(root)
        self.setAcceptDrops(True)
        self._tray()
        QShortcut(QKeySequence("Ctrl+O"), self, self.open_dialog)
        QShortcut(QKeySequence("Ctrl+W"), self, lambda: self.close_tab(self.tabs.currentIndex()))
        QShortcut(QKeySequence("F11"), self, self.toggle_fullscreen)
        QShortcut(QKeySequence("Ctrl+Tab"), self, lambda: self.tabs.setCurrentIndex((self.tabs.currentIndex() + 1) % self.tabs.count()))
        if Settings.get("hotfolder_autostart"):
            QTimer.singleShot(1500, self.hotfolder_service.start)

    # ------------------------------------------------------------ navigasyon
    def _nav(self):
        nav = QWidget()
        nav.setObjectName("NavRail")
        nav.setAttribute(Qt.WA_StyledBackground, True)
        nav.setFixedWidth(84)
        v = QVBoxLayout(nav)
        v.setContentsMargins(0, 14, 0, 12)
        v.setSpacing(2)
        logo = QLabel()
        logo.setPixmap(app_icon().pixmap(38, 38))
        logo.setAlignment(Qt.AlignCenter)
        v.addWidget(logo)
        name = QLabel("Mesly PDF")
        name.setObjectName("NavLogo")
        name.setAlignment(Qt.AlignCenter)
        name.setStyleSheet("color: white; font-weight: 700; font-size: 11px;")
        v.addWidget(name)
        v.addSpacing(14)
        self.nav_group = QButtonGroup(self)
        items = [("mdi6.home-variant-outline", "Başlangıç", lambda: self.tabs.setCurrentIndex(0)),
                 ("mdi6.folder-open-outline", "Aç", self.open_dialog),
                 ("mdi6.text-box-search-outline", "OCR", lambda: self.open_ocr_editor([])),
                 ("mdi6.compare", "Karşılaştır", lambda: self.open_compare()),
                 ("mdi6.monitor-screenshot", "Ekran OCR", self.screen_capture_menu),
                 ("mdi6.robot-outline", "Otomasyon", self.open_automation)]
        for ic, text, fn in items:
            v.addWidget(self._nav_btn(ic, text, fn))
        v.addStretch()
        v.addWidget(self._nav_btn("mdi6.cog-outline", "Ayarlar", self.open_settings))
        return nav

    def _nav_btn(self, ic, text, fn):
        b = QToolButton()
        b.setObjectName("NavButton")
        b.setIcon(theme.icon(ic, "#C8D1E6"))
        b.setIconSize(QSize(24, 24))
        b.setText(text)
        b.setToolButtonStyle(Qt.ToolButtonTextUnderIcon)
        b.setMinimumHeight(60)
        b.setCursor(Qt.PointingHandCursor)
        b.clicked.connect(fn)
        b.setSizePolicy(b.sizePolicy().horizontalPolicy().Expanding, b.sizePolicy().verticalPolicy())
        return b

    def screen_capture_menu(self):
        from .screenshot import start_capture
        m = QMenu(self)
        m.addAction(theme.icon("mdi6.format-text"), "Metin yakala", lambda: start_capture(self, "text"))
        m.addAction(theme.icon("mdi6.table"), "Tablo yakala", lambda: start_capture(self, "table"))
        m.addAction(theme.icon("mdi6.image-outline"), "Görüntü yakala", lambda: start_capture(self, "image"))
        m.exec(self.cursor().pos())

    def toggle_fullscreen(self):
        self.showNormal() if self.isFullScreen() else self.showFullScreen()

    # ------------------------------------------------------------ sekmeler
    def add_tab(self, w, icon_name, select=True):
        i = self.tabs.addTab(w, theme.icon(icon_name), self._short(w.title))
        self.tabs.setTabToolTip(i, w.title)
        xb = QToolButton()
        xb.setIcon(theme.icon("mdi6.close"))
        xb.setAutoRaise(True)
        xb.setFixedSize(20, 20)
        xb.setToolTip("Kapat (Ctrl+W)")
        xb.setStyleSheet(f"QToolButton {{ border: none; border-radius: 4px; }} QToolButton:hover {{ background: {theme.C['border']}; }}")
        xb.clicked.connect(lambda checked=False, w=w: self.close_tab(self.tabs.indexOf(w)))
        self.tabs.tabBar().setTabButton(i, QTabBar.RightSide, xb)
        if hasattr(w, "titleChanged"):
            w.titleChanged.connect(lambda w=w: self._retitle(w))
        if select:
            self.tabs.setCurrentWidget(w)
        return w

    @staticmethod
    def _short(t, n=34):
        return t if len(t) <= n else t[:n - 1] + "…"

    def _retitle(self, w):
        i = self.tabs.indexOf(w)
        if i >= 0:
            self.tabs.setTabText(i, self._short(w.title))
            self.tabs.setTabToolTip(i, w.title)
            if self.tabs.currentIndex() == i:
                self.setWindowTitle(f"{w.title} — {APP_NAME}")

    def _tab_changed(self, i):
        w = self.tabs.widget(i)
        self.setWindowTitle(f"{w.title} — {APP_NAME}" if i > 0 and hasattr(w, "title") else APP_NAME)
        if i == 0:
            self.start.refresh_recent()

    def close_tab(self, i):
        if i <= 0:
            return
        w = self.tabs.widget(i)
        if hasattr(w, "close_tab") and not w.close_tab():
            return
        self.tabs.removeTab(i)
        w.deleteLater()

    def find_tab(self, cls):
        for i in range(self.tabs.count()):
            if isinstance(self.tabs.widget(i), cls):
                return self.tabs.widget(i)
        return None

    # ------------------------------------------------------------ açma
    def open_dialog(self):
        fs = open_files(self, "Belge aç", ALL_SUPPORTED + ";;OCR projesi (*.meslyocr)")
        self.open_many(fs)

    def open_many(self, files):
        pdfs = [f for f in files if f.lower().endswith(".pdf")]
        projects = [f for f in files if f.lower().endswith(".meslyocr")]
        others = [f for f in files if f not in pdfs and f not in projects]
        for f in pdfs:
            self.open_document(f)
        for f in projects:
            self.open_ocr_project(f)
        if others:
            imgs = [f for f in others if f.lower().endswith(convert.IMAGE_EXT)]
            office = [f for f in others if f not in imgs]
            if imgs:
                self.open_ocr_editor(imgs)
            for f in office:
                self.open_any(f)

    def open_any(self, path):
        if not os.path.exists(path):
            toast(self, "Dosya bulunamadı", "err")
            return
        low = path.lower()
        if low.endswith(".pdf"):
            self.open_document(path)
        elif low.endswith(".meslyocr"):
            self.open_ocr_project(path)
        elif low.endswith(convert.IMAGE_EXT):
            self.open_ocr_editor([path])
        else:
            out = os.path.join(ocrmod.app_data_dir(), "donusturulen", os.path.splitext(os.path.basename(path))[0] + ".pdf")
            os.makedirs(os.path.dirname(out), exist_ok=True)
            run_task(self, "PDF'e dönüştürülüyor…", convert.any_to_pdf, [path], out, on_done=lambda _: self.open_document(out))

    def open_document(self, path):
        from .document_tab import DocumentTab
        path = os.path.abspath(path)
        for i in range(self.tabs.count()):
            w = self.tabs.widget(i)
            if isinstance(w, DocumentTab) and w.orig_path == path:
                self.tabs.setCurrentIndex(i)
                return w
        pw = None
        try:
            if is_encrypted(path):
                for _ in range(3):
                    pw = ask_password(self, os.path.basename(path))
                    if pw is None:
                        return None
                    try:
                        import pikepdf
                        pikepdf.open(path, password=pw).close()
                        break
                    except Exception:
                        QMessageBox.warning(self, "Parola", "Parola yanlış.")
                        pw = None
                if pw is None:
                    return None
            tab = DocumentTab(self, path, pw)
        except Exception as e:
            QMessageBox.critical(self, "Açılamadı", f"“{os.path.basename(path)}” açılamadı:\n{e}")
            return None
        Settings.add_recent(path)
        return self.add_tab(tab, "mdi6.file-pdf-box")

    def offer_open(self, path):
        if confirm(self, "Kaydedildi", f"{os.path.basename(path)} oluşturuldu. Açılsın mı?"):
            self.open_any(path)

    def open_ocr_editor(self, sources, name=""):
        from .ocr_editor import OcrEditor
        return self.add_tab(OcrEditor(self, sources, name), "mdi6.text-box-search-outline")

    def open_ocr_project(self, path):
        from .ocr_editor import OcrEditor
        Settings.add_recent(path)
        return self.add_tab(OcrEditor(self, project=path), "mdi6.text-box-search-outline")

    def open_compare(self, old=None, new=None):
        from .compare_view import CompareView
        return self.add_tab(CompareView(self, old, new), "mdi6.compare")

    def open_automation(self):
        from .automation import AutomationView
        w = self.find_tab(AutomationView)
        if w:
            self.tabs.setCurrentWidget(w)
            return w
        return self.add_tab(AutomationView(self), "mdi6.robot-outline")

    def open_settings(self):
        from .settings_view import SettingsView
        w = self.find_tab(SettingsView)
        if w:
            self.tabs.setCurrentWidget(w)
            return w
        return self.add_tab(SettingsView(self), "mdi6.cog-outline")

    # ------------------------------------------------------------ başlangıç eylemleri
    def on_action(self, key):
        lang = Settings.get("ocr_lang")
        if key == "open_pdf":
            for f in open_files(self, "PDF aç", PDF_FILTER):
                self.open_document(f)
        elif key == "open_ocr":
            fs = open_files(self, "OCR Düzenleyicide aç", ALL_SUPPORTED)
            if fs:
                self.open_ocr_editor(fs)
        elif key in ("to_docx", "to_xlsx", "to_pptx", "to_other"):
            self._convert_flow(key[3:] if key != "to_other" else None)
        elif key == "create_pdf":
            fs = open_files(self, "PDF'e dönüştürülecek dosyalar", ALL_SUPPORTED)
            if fs:
                v = FormDialog.ask(self, "PDF oluştur", [("ocr", "Görüntüleri aranabilir yap (OCR)", "check", True), ("lang", "OCR dili", "lang", lang),
                                                       ("merge", "Tek PDF'te birleştir" if len(fs) > 1 else "Tek dosya", "check", True)])
                if v:
                    self._make_pdfs(fs, v["ocr"], v["lang"], v["merge"], "pdf")
        elif key == "searchable":
            fs = open_files(self, "Aranabilir yapılacak dosyalar", ALL_SUPPORTED)
            if fs:
                self._make_pdfs(fs, True, lang, len(fs) > 1 and confirm(self, "Birleştir", "Tüm dosyalar tek PDF'te birleştirilsin mi?"), "pdf")
        elif key == "pdfa":
            fs = open_files(self, "PDF/A'ya dönüştürülecek dosyalar", ALL_SUPPORTED)
            if fs:
                self._make_pdfs(fs, True, lang, False, "pdfa")
        elif key == "merge":
            fs = open_files(self, "Birleştirilecek dosyalar (sırayla)", ALL_SUPPORTED)
            if len(fs) >= 1:
                from .dialogs import TableDialog
                d = TableDialog(self, "Birleştirme sırası", ["Dosya"], [[f] for f in fs], editable=True)
                d.table.setEditTriggers(d.table.NoEditTriggers)
                if d.exec():
                    order = [r[0] for r in d.rows() if r[0]]
                    out = save_file(self, "Birleştirilmiş PDF", "birlesik.pdf")
                    if out:
                        run_task(self, "Birleştiriliyor…", convert.any_to_pdf, order, out, on_done=lambda _: self.open_document(out))
        elif key == "blank":
            v = FormDialog.ask(self, "Boş PDF", [("n", "Sayfa sayısı", "int", 1, (1, 500)),
                                                ("size", "Boyut", "combo", "a4", [("a4", "A4 dikey"), ("a4l", "A4 yatay"), ("letter", "Letter"), ("a5", "A5")])])
            if v:
                out = save_file(self, "Boş PDF", "yeni.pdf")
                if out:
                    sz = {"a4": (595.28, 841.89), "a4l": (841.89, 595.28), "letter": (612, 792), "a5": (419.53, 595.28)}[v["size"]]
                    pg.new_blank(out, v["n"], sz)
                    self.open_document(out)
        elif key == "compress":
            for f in open_files(self, "Sıkıştırılacak PDF", PDF_FILTER, multi=False):
                out = save_file(self, "Sıkıştırılmış PDF", os.path.splitext(os.path.basename(f))[0] + "_kucuk.pdf")
                if out:
                    run_task(self, "Sıkıştırılıyor…", editing.compress, f, out, "medium",
                             on_done=lambda r: toast(self, f"{r[1] / 1024:.0f} KB → {r[2] / 1024:.0f} KB", "ok"))
        elif key in ("scan_ocr", "scan_pdf", "scan_docx"):
            from .scanner import acquire_images
            imgs = acquire_images(self)
            if not imgs:
                return
            if key == "scan_ocr":
                self.open_ocr_editor(imgs, "Tarama")
            elif key == "scan_pdf":
                self._make_pdfs(imgs, True, lang, True, "pdf")
            else:
                out = save_file(self, "Word belgesi", "tarama.docx", "Word (*.docx)")
                if out:
                    run_task(self, "Dönüştürülüyor…", convert.convert, imgs, out, "docx", lang, on_done=lambda _: open_path(out))
        elif key in ("screen_text", "screen_table"):
            from .screenshot import start_capture
            start_capture(self, key[7:])
        elif key == "compare":
            self.open_compare()
        elif key == "hotfolder":
            self.open_automation()
        elif key == "tasks":
            w = self.open_automation()
            w.findChild(QTabWidget).setCurrentIndex(1)

    def _convert_flow(self, fmt):
        fs = open_files(self, "Dönüştürülecek dosyalar", ALL_SUPPORTED)
        if not fs:
            return
        fields = []
        if fmt is None:
            fields.append(("fmt", "Biçim", "combo", "odt", [(k, v) for k, v in convert.EXPORT_FORMATS.items() if k not in ("docx", "xlsx", "pptx")]))
        fields += [("lang", "OCR dili", "lang", Settings.get("ocr_lang"))]
        if len(fs) > 1:
            fields.append(("merge", "Tüm dosyaları tek belgede birleştir", "check", False))
        if fmt == "docx" or fmt is None:
            fields.append(("mode", "Biçimlendirme", "combo", Settings.get("export_mode"), [("formatted", "Düzenlenebilir kopya"), ("plain", "Düz metin")]))
        v = FormDialog.ask(self, "Dönüştür", fields, ok_text="Dönüştür")
        if not v:
            return
        fmt = fmt or v["fmt"]
        ext = {"pdfa": "pdf", "pdfua": "pdf"}.get(fmt, fmt)
        if len(fs) == 1 or v.get("merge"):
            out = save_file(self, "Kaydet", os.path.splitext(os.path.basename(fs[0]))[0] + "." + ext, convert.EXPORT_FORMATS[fmt])
            if out:
                run_task(self, "Dönüştürülüyor…", convert.convert, fs, out, fmt, v["lang"], mode=v.get("mode", "formatted"),
                         on_done=lambda _: (toast(self, "Dönüştürme tamamlandı", "ok"), open_path(out) if confirm(self, "Tamamlandı", f"{os.path.basename(out)} açılsın mı?") else None))
        else:
            from .common import pick_dir
            d = pick_dir(self, "Çıktı klasörü", os.path.dirname(fs[0]))
            if d:
                def done(res):
                    errs = [r for r in res if r[1].startswith("HATA")]
                    QMessageBox.information(self, "Toplu dönüştürme", f"{len(res) - len(errs)} dosya dönüştürüldü." +
                                            (f"\n{len(errs)} hata:\n" + "\n".join(f"{os.path.basename(a)}: {b}" for a, b in errs[:8]) if errs else ""))
                    open_path(d)
                run_task(self, "Dönüştürülüyor…", convert.batch_convert, fs, d, fmt, lang=v["lang"], mode=v.get("mode", "formatted"), on_done=done)

    def _make_pdfs(self, fs, ocr, lang, merge, fmt):
        if merge or len(fs) == 1:
            out = save_file(self, "PDF kaydet", os.path.splitext(os.path.basename(fs[0]))[0] + (".pdf" if fmt == "pdf" else "_PDFA.pdf"))
            if not out:
                return
            if fmt == "pdfa":
                run_task(self, "PDF/A oluşturuluyor…", convert.convert, fs, out, "pdfa", lang, on_done=lambda _: self.open_document(out))
            else:
                run_task(self, "PDF oluşturuluyor…", convert.any_to_pdf, fs, out, ocr, lang, on_done=lambda _: self.open_document(out))
        else:
            from .common import pick_dir
            d = pick_dir(self, "Çıktı klasörü", os.path.dirname(fs[0]))
            if d:
                run_task(self, "PDF'ler oluşturuluyor…", convert.batch_convert, fs, d, fmt, lang=lang,
                         on_done=lambda r: (toast(self, f"{len(r)} dosya işlendi", "ok"), open_path(d)))

    # ------------------------------------------------------------ tepsi
    def _tray(self):
        self.tray = QSystemTrayIcon(app_icon(), self)
        m = QMenu()
        m.addAction("Mesly PDF'i göster", self.show_normal)
        m.addSeparator()
        from .screenshot import start_capture
        m.addAction(theme.icon("mdi6.format-text"), "Ekrandan metin yakala", lambda: start_capture(self, "text"))
        m.addAction(theme.icon("mdi6.table"), "Ekrandan tablo yakala", lambda: start_capture(self, "table"))
        m.addSeparator()
        self.tray_hf = m.addAction("Hot Folder'ı başlat", self._toggle_hf)
        m.addSeparator()
        m.addAction("Çıkış", self.quit_app)
        self.tray.setContextMenu(m)
        self.tray.setToolTip(APP_NAME)
        self.tray.activated.connect(lambda r: r == QSystemTrayIcon.Trigger and self.show_normal())
        if QSystemTrayIcon.isSystemTrayAvailable():
            self.tray.show()

    def _toggle_hf(self):
        if self.hotfolder_service.running:
            self.hotfolder_service.stop()
        else:
            self.hotfolder_service.start()
        self.update_tray()

    def update_tray(self):
        on = self.hotfolder_service.running
        self.tray_hf.setText("Hot Folder'ı durdur" if on else "Hot Folder'ı başlat")
        self.tray.setToolTip(APP_NAME + (" — Hot Folder çalışıyor" if on else ""))

    def show_normal(self):
        self.showNormal()
        self.raise_()
        self.activateWindow()

    def closeEvent(self, e):
        if self.hotfolder_service.running and QSystemTrayIcon.isSystemTrayAvailable():
            self.hide()
            self.tray.showMessage(APP_NAME, "Hot Folder arka planda çalışmaya devam ediyor.", QSystemTrayIcon.Information, 3000)
            e.ignore()
            return
        if not self._close_all():
            e.ignore()
            return
        e.accept()

    def _close_all(self):
        for i in range(self.tabs.count() - 1, 0, -1):
            w = self.tabs.widget(i)
            if hasattr(w, "close_tab") and not w.close_tab():
                return False
        return True

    def quit_app(self):
        if self._close_all():
            self.hotfolder_service.stop() if self.hotfolder_service.running else None
            QApplication.quit()

    def dragEnterEvent(self, e):
        if e.mimeData().hasUrls():
            e.acceptProposedAction()

    def dropEvent(self, e):
        self.open_many([u.toLocalFile() for u in e.mimeData().urls() if u.isLocalFile()])
