"""PDF Düzenleyici sekmesi: şerit, yan paneller, görüntüleyici, araçlar, sürüm geçmişi (geri al/yinele)."""
from __future__ import annotations

import os
import shutil
import tempfile

from PySide6.QtCore import Qt, QSize, QTimer, Signal, QRectF
from PySide6.QtGui import QKeySequence, QShortcut, QColor, QPen, QBrush, QGuiApplication, QImage, QPainter
from PySide6.QtWidgets import (QWidget, QVBoxLayout, QHBoxLayout, QSplitter, QStackedWidget, QToolButton,
                               QButtonGroup, QLabel, QSpinBox, QComboBox, QMenu, QMessageBox, QInputDialog,
                               QFileDialog, QFrame, QGraphicsRectItem, QGraphicsProxyWidget, QLineEdit, QCheckBox,
                               QPlainTextEdit, QDialog, QApplication, QColorDialog)

from ..core import (pages as pg, editing, annotations as ann, security, forms, sign as signmod, convert,
                    ocr as ocrmod)
from ..core.document import PdfDocument
from . import theme
from .common import (run_task, toast, Settings, save_file, open_files, button, label, ask_password, confirm,
                     open_path, PDF_FILTER, IMG_FILTER)
from .dialogs import FormDialog, page_scope_field, resolve_pages, TableDialog, TextDialog
from .pageview import PageView
from .panels import (ThumbnailPanel, BookmarkPanel, SearchPanel, CommentsPanel, AttachmentsPanel, SignaturesPanel,
                     FieldsPanel)
from .ribbon import Ribbon

MARKUP_COLORS = {"highlight": "#FFEB3B", "underline": "#1E88E5", "strike": "#E53935"}


class PageBox(QLineEdit):
    """Sayfa numarası girişi (QSpinBox yerine; stil sayfasıyla sorunsuz)."""

    def __init__(self):
        super().__init__("1")
        from PySide6.QtGui import QIntValidator
        self._max = 1
        self._val = QIntValidator(1, 1, self)
        self.setValidator(self._val)
        self.setFixedWidth(52)
        self.setAlignment(Qt.AlignCenter)
        self.setToolTip("Sayfaya git")

    def setMaximum(self, n):
        self._max = max(1, n)
        self._val.setTop(self._max)

    def setMinimum(self, n):
        self._val.setBottom(n)

    def value(self):
        try:
            return max(1, min(self._max, int(self.text())))
        except ValueError:
            return 1

    def setValue(self, v):
        if not self.hasFocus():
            self.setText(str(v))


class DocumentTab(QWidget):
    titleChanged = Signal()

    def __init__(self, main, path: str, password: str | None = None):
        super().__init__()
        self.main = main
        self.orig_path = os.path.abspath(path)
        self.orig_password = password
        self.workdir = tempfile.mkdtemp(prefix="meslypdf_")
        first = os.path.join(self.workdir, "v0000.pdf")
        if password:
            security.decrypt(path, first, password)
        else:
            shutil.copyfile(path, first)
        self.versions = [first]
        self.labels = ["Açıldı"]
        self.vidx = 0
        self.saved_vidx = 0
        self.doc: PdfDocument | None = None
        self.color = "#E53935"
        self.pending_redactions: dict[int, list] = {}
        self._redact_items = []
        self._edit_items = []
        self._form_proxies = []
        self.form_mode = False
        self.form_values: dict = {}
        self._build()
        self.reload(first_time=True)

    # ------------------------------------------------------------ özellikler
    @property
    def path(self):
        return self.versions[self.vidx]

    @property
    def title(self):
        return os.path.basename(self.orig_path) + (" •" if self.dirty else "")

    @property
    def dirty(self):
        return self.vidx != self.saved_vidx

    # ------------------------------------------------------------ arayüz
    def _build(self):
        v = QVBoxLayout(self)
        v.setContentsMargins(0, 0, 0, 0)
        v.setSpacing(0)
        self.ribbon = Ribbon()
        v.addWidget(self.ribbon)
        self.banner = QFrame()
        self.banner.setObjectName("Banner")
        self.banner.setStyleSheet(f"#Banner {{ background: {theme.C['primary_soft']}; border-bottom: 1px solid {theme.C['border']}; }}")
        bl = QHBoxLayout(self.banner)
        bl.setContentsMargins(14, 6, 14, 6)
        self.banner_icon = QLabel()
        self.banner_text = QLabel()
        self.banner_btns = QHBoxLayout()
        bl.addWidget(self.banner_icon)
        bl.addWidget(self.banner_text, 1)
        bl.addLayout(self.banner_btns)
        self.banner.hide()
        v.addWidget(self.banner)

        self.split = QSplitter(Qt.Horizontal)
        # sol: panel seçici + panel yığını
        left = QWidget()
        left.setObjectName("Sidebar")
        left.setAttribute(Qt.WA_StyledBackground, True)
        lh = QHBoxLayout(left)
        lh.setContentsMargins(0, 0, 0, 0)
        lh.setSpacing(0)
        rail = QVBoxLayout()
        rail.setContentsMargins(6, 8, 4, 8)
        rail.setSpacing(4)
        self.panel_stack = QStackedWidget()
        self.thumbs = ThumbnailPanel(self)
        self.bookmarks = BookmarkPanel(self)
        self.searchp = SearchPanel(self)
        self.comments = CommentsPanel(self)
        self.attachments = AttachmentsPanel(self)
        self.signatures = SignaturesPanel(self)
        self.fields = FieldsPanel(self)
        self.panels = [("mdi6.file-multiple-outline", "Sayfalar", self.thumbs), ("mdi6.bookmark-multiple-outline", "Yer imleri", self.bookmarks),
                       ("mdi6.magnify", "Ara", self.searchp), ("mdi6.comment-multiple-outline", "Yorumlar", self.comments),
                       ("mdi6.form-select", "Form alanları", self.fields), ("mdi6.paperclip", "Ekler", self.attachments),
                       ("mdi6.certificate-outline", "İmzalar", self.signatures)]
        self.panel_group = QButtonGroup(self)
        for k, (ic, tip, w) in enumerate(self.panels):
            b = QToolButton()
            b.setObjectName("SideTab")
            b.setIcon(theme.icon(ic))
            b.setIconSize(QSize(20, 20))
            b.setToolTip(tip)
            b.setCheckable(True)
            b.setCursor(Qt.PointingHandCursor)
            self.panel_group.addButton(b, k)
            rail.addWidget(b)
            self.panel_stack.addWidget(w)
        rail.addStretch()
        self.panel_group.button(0).setChecked(True)
        self.panel_group.idClicked.connect(self._show_panel)
        lh.addLayout(rail)
        sep = QFrame()
        sep.setFixedWidth(1)
        sep.setStyleSheet(f"background:{theme.C['border']}")
        lh.addWidget(sep)
        lh.addWidget(self.panel_stack, 1)
        left.setMinimumWidth(200)
        self.split.addWidget(left)

        center = QWidget()
        cv = QVBoxLayout(center)
        cv.setContentsMargins(0, 0, 0, 0)
        cv.setSpacing(0)
        self.center_stack = QStackedWidget()
        self.view = PageView()
        self.center_stack.addWidget(self.view)
        cv.addWidget(self.center_stack, 1)
        cv.addWidget(self._statusbar())
        self.split.addWidget(center)
        self.split.setStretchFactor(1, 1)
        self.split.setSizes([290, 1000])
        v.addWidget(self.split, 1)

        self.view.pageChanged.connect(self._page_changed)
        self.view.zoomChanged.connect(lambda z: self.zoom_combo.setEditText(f"%{round(z * 100)}"))
        self.view.rectDrawn.connect(self.on_rect)
        self.view.pointClicked.connect(self.on_point)
        self.view.inkDrawn.connect(self.on_ink)
        self.view.textSelected.connect(self.on_text_selected)
        self.view.contextRequested.connect(self.on_context)
        self.view.layoutChanged.connect(self._reposition_overlays)
        self._build_ribbon()
        for seq, fn in (("Ctrl+Z", self.undo), ("Ctrl+Y", self.redo), ("Ctrl+Shift+Z", self.redo), ("Ctrl+S", self.save),
                        ("Ctrl+Shift+S", self.save_as), ("Ctrl+F", self.focus_search), ("Ctrl+P", self.print_doc),
                        ("Ctrl++", self.view.zoom_in), ("Ctrl+=", self.view.zoom_in), ("Ctrl+-", self.view.zoom_out),
                        ("Escape", lambda: self.set_tool("select")), ("Ctrl+C", self.copy_selection)):
            QShortcut(QKeySequence(seq), self, fn, context=Qt.WidgetWithChildrenShortcut)
        self._selected = None

    def _statusbar(self):
        bar = QFrame()
        bar.setObjectName("DocStatus")
        bar.setStyleSheet(f"#DocStatus {{ background: {theme.C['surface']}; border-top: 1px solid {theme.C['border']}; }}")
        h = QHBoxLayout(bar)
        h.setContentsMargins(10, 3, 10, 3)
        h.setSpacing(6)

        def tb(ic, tip, fn, checkable=False):
            b = QToolButton()
            b.setIcon(theme.icon(ic))
            b.setToolTip(tip)
            b.setAutoRaise(True)
            b.setCheckable(checkable)
            b.clicked.connect(fn)
            h.addWidget(b)
            return b
        tb("mdi6.page-first", "İlk sayfa", lambda: self.view.go_to(0))
        tb("mdi6.chevron-left", "Önceki sayfa", lambda: self.view.go_to(self.view.current_page() - 1))
        self.page_spin = PageBox()
        self.page_spin.editingFinished.connect(lambda: self.view.go_to(self.page_spin.value() - 1))
        h.addWidget(self.page_spin)
        self.page_total = QLabel("/ 0")
        h.addWidget(self.page_total)
        tb("mdi6.chevron-right", "Sonraki sayfa", lambda: self.view.go_to(self.view.current_page() + 1))
        tb("mdi6.page-last", "Son sayfa", lambda: self.view.go_to(self.doc.page_count - 1))
        h.addStretch()
        self.status_msg = label("", "Small")
        h.addWidget(self.status_msg)
        h.addStretch()
        self.lay_group = QButtonGroup(self)
        for ic, tip, mode in (("mdi6.view-sequential-outline", "Sürekli kaydırma", "continuous"),
                              ("mdi6.file-outline", "Tek sayfa", "single"), ("mdi6.book-open-page-variant-outline", "İki sayfa", "facing")):
            b = tb(ic, tip, lambda checked=False, m=mode: self.view.set_layout(m), True)
            self.lay_group.addButton(b)
            if mode == "continuous":
                b.setChecked(True)
        tb("mdi6.arrow-expand-horizontal", "Genişliğe sığdır", lambda: self.view.set_fit("width"))
        tb("mdi6.fit-to-page-outline", "Sayfaya sığdır", lambda: self.view.set_fit("page"))
        tb("mdi6.magnify-minus-outline", "Uzaklaştır", self.view.zoom_out)
        self.zoom_combo = QComboBox()
        self.zoom_combo.setEditable(True)
        self.zoom_combo.setFixedWidth(84)
        for z in (50, 75, 100, 125, 150, 200, 300, 400):
            self.zoom_combo.addItem(f"%{z}", z)
        self.zoom_combo.activated.connect(lambda i: self._set_zoom_text(self.zoom_combo.itemText(i)))
        self.zoom_combo.lineEdit().returnPressed.connect(lambda: self._set_zoom_text(self.zoom_combo.currentText()))
        h.addWidget(self.zoom_combo)
        tb("mdi6.magnify-plus-outline", "Yakınlaştır", self.view.zoom_in)
        return bar

    def _set_zoom_text(self, t):
        try:
            z = float(t.replace("%", "").strip()) / 100
            self.view.fit_mode = "none"
            self.view.set_zoom(z)
        except ValueError:
            pass

    def _tool_btn(self, page, text, ic, tool, tip=""):
        b = page.add(text, ic, lambda: self.set_tool(tool), tip, checkable=True)
        b.setProperty("tool", tool)
        self.ribbon.tool_group.addButton(b)
        return b

    def _build_ribbon(self):
        r = self.ribbon
        # -------- Giriş
        p = r.page("Giriş")
        p.group("Dosya")
        p.add("Kaydet", "mdi6.content-save-outline", self.save, "Kaydet (Ctrl+S)")
        p.add("Farklı kaydet", "mdi6.content-save-edit-outline", self.save_as)
        p.add("Yazdır", "mdi6.printer-outline", self.print_doc, "Yazdır (Ctrl+P)")
        p.group("Geçmiş")
        self.b_undo = p.add("Geri al", "mdi6.undo", self.undo, "Geri al (Ctrl+Z)")
        self.b_redo = p.add("Yinele", "mdi6.redo", self.redo, "Yinele (Ctrl+Y)")
        p.group("Araçlar")
        sel = self._tool_btn(p, "Seç", "mdi6.cursor-text", "select", "Metin seç (Esc)")
        sel.setChecked(True)
        self._tool_btn(p, "El", "mdi6.hand-back-right-outline", "hand", "Kaydırma aracı")
        self._tool_btn(p, "Yakınlaştır", "mdi6.magnify-scan", "zoom_area", "Seçilen alanı yakınlaştır")
        p.add("Ara", "mdi6.magnify", self.focus_search, "Ara (Ctrl+F)")
        p.group("Görünüm")
        p.add("Gece modu", "mdi6.weather-night", self.toggle_night, checkable=True)
        p.add("Tam ekran", "mdi6.fullscreen", self.main.toggle_fullscreen)
        p.add("Özellikler", "mdi6.information-outline", self.op_metadata)
        p.group("Hızlı işlemler")
        p.add("OCR uygula", "mdi6.text-recognition", self.op_ocr, "Taranmış sayfalara aranabilir metin katmanı ekle")
        p.add("Word'e aktar", "mdi6.file-word-outline", lambda: self.op_export("docx"), color="#2B579A")
        p.add("Excel'e aktar", "mdi6.file-excel-outline", lambda: self.op_export("xlsx"), color="#217346")
        p.add("Karşılaştır", "mdi6.compare", self.op_compare)

        # -------- Düzenle
        p = r.page("Düzenle")
        p.group("Metin")
        self._tool_btn(p, "Metni düzenle", "mdi6.text-box-edit-outline", "edit_text", "Paragrafa tıklayıp metni düzenleyin")
        self._tool_btn(p, "Metin ekle", "mdi6.format-text", "add_text")
        self._tool_btn(p, "Görsel ekle", "mdi6.image-plus", "image")
        self._tool_btn(p, "Bağlantı", "mdi6.link-variant-plus", "link")
        p.add("Bul / değiştir", "mdi6.find-replace", self.focus_search)
        p.group("Sayfa ögeleri")
        p.add("Filigran", "mdi6.watermark", menu=[("Filigran ekle…", "mdi6.watermark", self.op_watermark),
                                                 ("Filigranları kaldır", "mdi6.eraser", lambda: self.op_remove_marks("watermark"))])
        p.add("Üst/Altbilgi", "mdi6.page-layout-header-footer", menu=[("Ekle…", "mdi6.page-layout-header-footer", self.op_header_footer),
                                                                    ("Kaldır", "mdi6.eraser", lambda: self.op_remove_marks("headerfooter"))])
        p.add("Bates", "mdi6.numeric", self.op_bates, "Bates numaralandırma")
        p.add("Damga", "mdi6.stamper", menu=[(f"{k}", "mdi6.stamper", lambda checked=False, k=k: self._stamp_mode(k)) for k in editing.STAMPS] +
              [None, ("Özel metin damgası…", "mdi6.format-text", self._custom_stamp), ("Görsel damga…", "mdi6.image-outline", self._image_stamp),
               ("Damgaları kaldır", "mdi6.eraser", lambda: self.op_remove_marks("stamp"))])
        p.group("Belge")
        p.add("Yer imleri", "mdi6.bookmark-multiple-outline", lambda: self._show_panel(1, True))
        p.add("Ekler", "mdi6.paperclip", lambda: self._show_panel(5, True))
        p.add("Meta veri", "mdi6.tag-text-outline", self.op_metadata)
        p.add("Sıkıştır", "mdi6.zip-box-outline", self.op_compress)

        # -------- Yorum
        p = r.page("Yorum")
        p.group("İşaretleme")
        self._tool_btn(p, "Vurgula", "mdi6.marker", "highlight")
        self._tool_btn(p, "Altını çiz", "mdi6.format-underline", "underline")
        self._tool_btn(p, "Üstünü çiz", "mdi6.format-strikethrough", "strike")
        self._tool_btn(p, "Not", "mdi6.comment-text-outline", "note")
        self._tool_btn(p, "Metin kutusu", "mdi6.form-textbox", "freetext")
        p.group("Çizim")
        self._tool_btn(p, "Dikdörtgen", "mdi6.square-outline", "square")
        self._tool_btn(p, "Elips", "mdi6.circle-outline", "circle")
        self._tool_btn(p, "Ok", "mdi6.arrow-top-right", "line")
        self._tool_btn(p, "Kalem", "mdi6.draw", "ink")
        self.color_btn = p.add("Renk", "mdi6.palette-outline", self._pick_color)
        self._update_color_icon()
        p.group("Yönet")
        p.add("Yorumlar", "mdi6.comment-multiple-outline", lambda: self._show_panel(3, True))
        p.add("Düzleştir", "mdi6.layers-triple-outline", self.comments.flatten)
        p.add("Dışa aktar", "mdi6.export", self.comments.export)

        # -------- Sayfalar
        p = r.page("Sayfalar")
        p.group("Düzenle")
        p.add("Sayfaları düzenle", "mdi6.view-grid-outline", self.show_organizer, "Küçük resimlerle sürükle-bırak düzenleme")
        p.add("Sağa döndür", "mdi6.rotate-right", lambda: self.op_rotate(None, 90))
        p.add("Sola döndür", "mdi6.rotate-left", lambda: self.op_rotate(None, -90))
        p.add("Sil", "mdi6.file-remove-outline", lambda: self.op_delete(None))
        p.group("Ekle")
        p.add("Dosyadan", "mdi6.file-plus-outline", self.op_insert_file)
        p.add("Boş sayfa", "mdi6.file-outline", lambda: self.op_insert_blank(self.view.current_page() + 1))
        p.add("Tarayıcıdan", "mdi6.scanner", self.op_insert_scan)
        p.group("Yapı")
        p.add("Çıkar", "mdi6.file-export-outline", lambda: self.op_extract(None))
        p.add("Böl", "mdi6.call-split", self.op_split)
        self._tool_btn(p, "Kırp", "mdi6.crop", "crop")
        p.add("Boş sayfalar", "mdi6.file-hidden", self.op_blank_pages, "Boş sayfaları bul ve sil")
        p.add("Numaralandır", "mdi6.order-numeric-ascending", self.op_page_labels)

        # -------- Formlar
        p = r.page("Formlar")
        p.group("Doldur")
        self.b_fill = p.add("Formu doldur", "mdi6.form-textbox", self.toggle_form_fill, checkable=True)
        p.add("İçe aktar", "mdi6.import", self.op_import_form, "FDF / JSON verisi içe aktar")
        p.add("Dışa aktar", "mdi6.export", self.op_export_form)
        p.add("Düzleştir", "mdi6.layers-triple-outline", self.op_flatten_form)
        p.group("Alan ekle")
        self._tool_btn(p, "Metin alanı", "mdi6.form-textbox", "field_text")
        self._tool_btn(p, "Onay kutusu", "mdi6.checkbox-marked-outline", "field_checkbox")
        self._tool_btn(p, "Seçenek", "mdi6.radiobox-marked", "field_radio")
        self._tool_btn(p, "Açılır liste", "mdi6.form-dropdown", "field_combo")
        self._tool_btn(p, "Liste", "mdi6.format-list-bulleted", "field_list")
        self._tool_btn(p, "Düğme", "mdi6.gesture-tap-button", "field_button")
        self._tool_btn(p, "İmza alanı", "mdi6.draw-pen", "field_signature")
        p.group("Alanlar")
        p.add("Alan listesi", "mdi6.format-list-checks", lambda: self._show_panel(4, True))

        # -------- Koruma
        p = r.page("Koruma")
        p.group("Karartma")
        self._tool_btn(p, "Alan karart", "mdi6.square", "redact")
        p.add("Ara ve karart", "mdi6.text-search", self.op_redact_search, "Metin veya kişisel veri desenlerini bul ve karart")
        p.group("Gizlilik")
        p.add("Parola", "mdi6.lock-outline", self.op_protect, "Parola ve izinler")
        p.add("Parolayı kaldır", "mdi6.lock-open-variant-outline", self.op_unprotect)
        p.add("Gizli verileri temizle", "mdi6.broom", self.op_clean)
        p.group("Dijital imza")
        self._tool_btn(p, "İmzala", "mdi6.draw-pen", "sign", "İmza kutusunu çizin (görünmez imza için tek tıklayın)")
        p.add("Görünmez imza", "mdi6.certificate-outline", lambda: self.op_sign(None, None))
        p.add("Doğrula", "mdi6.shield-check-outline", lambda: self._show_panel(6, True))
        p.add("Sertifika oluştur", "mdi6.card-account-details-outline", self.op_create_cert)
        p.group("Faks imzası")
        p.add("İmza görseli", "mdi6.signature-image", self._image_stamp, "Taranmış imza görselini yerleştir")

        # -------- Dönüştür
        p = r.page("Dönüştür")
        p.group("Office")
        p.add("Word", "mdi6.file-word-outline", lambda: self.op_export("docx"), color="#2B579A")
        p.add("Excel", "mdi6.file-excel-outline", lambda: self.op_export("xlsx"), color="#217346")
        p.add("PowerPoint", "mdi6.file-powerpoint-outline", lambda: self.op_export("pptx"), color="#B7472A")
        p.add("Diğer", "mdi6.file-export-outline", menu=[(convert.EXPORT_FORMATS[k], None, lambda checked=False, k=k: self.op_export(k))
                                                         for k in ("odt", "rtf", "txt", "csv", "html", "epub", "fb2")] +
              [None, ("Sayfaları görüntü olarak (PNG/JPG/TIFF)…", "mdi6.image-multiple-outline", self.op_export_images)])
        p.group("PDF standartları")
        p.add("PDF/A", "mdi6.archive-outline", self.op_pdfa, "Uzun süreli arşiv")
        p.add("PDF/UA", "mdi6.human", self.op_pdfua, "Erişilebilir, etiketli PDF")
        p.group("OCR")
        p.add("Metin katmanı", "mdi6.text-recognition", self.op_ocr)
        p.add("OCR Düzenleyici", "mdi6.text-box-search-outline", self.open_in_ocr_editor, "Gelişmiş tanıma ve doğrulama")

    def _show_panel(self, k, force=False):
        self.panel_group.button(k).setChecked(True)
        self.panel_stack.setCurrentIndex(k)
        w = self.panels[k][2]
        if w is not self.thumbs and hasattr(w, "reload") and w is not self.searchp:
            w.reload()

    def focus_search(self):
        self._show_panel(2)
        sel = self._selected[2] if self._selected else None
        self.searchp.focus(sel if sel and len(sel) < 60 else None)

    def toggle_night(self):
        self.view.night = not self.view.night
        self.view.refresh()

    # ------------------------------------------------------------ yükleme / sürümler
    def reload(self, first_time=False):
        old = self.doc
        self.doc = PdfDocument(self.path)
        self.view.invalidate_words()
        # sahne temizlenecek: katman nesnelerini önce düzgünce kaldır
        self._clear_edit_overlay()
        self._clear_form_proxies()
        pending = dict(self.pending_redactions)
        self._redact_items = []
        self.pending_redactions = {}
        self.view.set_document(self.doc, keep_position=not first_time)
        for pgi, rects in pending.items():
            if pgi < self.doc.page_count:
                for r in rects:
                    self._add_redact_mark(pgi, r)
        if old:
            old.close()
        self.page_spin.setMaximum(max(1, self.doc.page_count))
        self.page_total.setText(f"/ {self.doc.page_count}")
        self.thumbs.reload()
        cur = self.panel_stack.currentWidget()
        if cur is not self.thumbs and hasattr(cur, "reload") and cur is not self.searchp:
            cur.reload()
        self.b_undo.setEnabled(self.vidx > 0)
        self.b_redo.setEnabled(self.vidx < len(self.versions) - 1)
        self.titleChanged.emit()
        self._clear_edit_overlay()
        if self.form_mode:
            self._build_form_proxies()
        if first_time:
            self._check_initial_state()

    def _check_initial_state(self):
        msgs = []
        try:
            flds = forms.list_fields(self.path)
        except Exception:
            flds = []
        if flds and any(f["type"] != "signature" for f in flds):
            self.show_banner("mdi6.form-textbox", f"Bu belgede {len(flds)} doldurulabilir form alanı var.",
                             [("Formu doldur", self.toggle_form_fill)])
            return
        if not self.doc.has_text_layer():
            self.show_banner("mdi6.text-recognition", "Bu belge taranmış görünüyor: metin seçilemez ve aranamaz.",
                             [("OCR uygula", self.op_ocr), ("OCR Düzenleyicide aç", self.open_in_ocr_editor)])
            return
        try:
            sigs = signmod.signature_fields(self.path)
        except Exception:
            sigs = []
        if any(s["signed"] for s in sigs):
            self.show_banner("mdi6.certificate-outline", "Bu belge dijital olarak imzalanmış.",
                             [("İmzaları doğrula", lambda: self._show_panel(6, True))])

    def show_banner(self, ic, text, actions=(), kind="info"):
        self.banner_icon.setPixmap(theme.icon(ic, theme.C["primary"]).pixmap(18, 18))
        self.banner_text.setText(text)
        while self.banner_btns.count():
            w = self.banner_btns.takeAt(0).widget()
            if w:
                w.hide()
                w.setParent(None)
                w.deleteLater()
        for t, fn in actions:
            b = button(t, obj="Primary" if not self.banner_btns.count() else None, slot=fn)
            b.setMinimumWidth(b.fontMetrics().horizontalAdvance(t) + 36)
            self.banner_btns.addWidget(b)
        x = QToolButton()
        x.setIcon(theme.icon("mdi6.close"))
        x.setAutoRaise(True)
        x.clicked.connect(self.banner.hide)
        self.banner_btns.addWidget(x)
        self.banner.show()

    def _next_path(self):
        return os.path.join(self.workdir, f"v{len(self.versions):04d}_{self.vidx:04d}.pdf")

    def push_version(self, new_path, label_text):
        self.versions = self.versions[:self.vidx + 1] + [new_path]
        self.labels = self.labels[:self.vidx + 1] + [label_text]
        self.vidx = len(self.versions) - 1
        if self.saved_vidx > self.vidx:
            self.saved_vidx = -1
        self.reload()
        self.status_msg.setText(label_text)

    def apply(self, msg, fn, *args, **kw):
        """Senkron işlem: fn(src, out, *args) -> yeni sürüm."""
        out = self._next_path()
        try:
            QApplication.setOverrideCursor(Qt.WaitCursor)
            fn(self.path, out, *args, **kw)
        except Exception as e:
            QApplication.restoreOverrideCursor()
            QMessageBox.critical(self, "Hata", f"İşlem başarısız:\n{e}")
            return False
        QApplication.restoreOverrideCursor()
        if not os.path.exists(out):
            return False
        self.push_version(out, msg)
        toast(self, msg, "ok")
        return True

    def apply_async(self, title, msg, fn, *args, **kw):
        out = self._next_path()

        def done(_):
            if os.path.exists(out):
                self.push_version(out, msg)
                toast(self, msg, "ok")
        run_task(self, title, fn, self.path, out, *args, on_done=done, **kw)

    def undo(self):
        if self.vidx > 0:
            self.vidx -= 1
            self.reload()
            toast(self, f"Geri alındı: {self.labels[self.vidx + 1]}")

    def redo(self):
        if self.vidx < len(self.versions) - 1:
            self.vidx += 1
            self.reload()
            toast(self, f"Yinelendi: {self.labels[self.vidx]}")

    def save(self):
        return self._save_to(self.orig_path)

    def save_as(self):
        p = save_file(self, "Farklı kaydet", os.path.basename(self.orig_path))
        if p:
            if self._save_to(p):
                self.orig_path = os.path.abspath(p)
                self.titleChanged.emit()
                Settings.add_recent(p)

    def _save_to(self, target):
        if self.form_mode and self.form_values:
            self.commit_form()
        try:
            if self.orig_password:
                security.encrypt(self.path, target + ".tmp", self.orig_password, method="AES-256")
            else:
                shutil.copyfile(self.path, target + ".tmp")
            os.replace(target + ".tmp", target)
        except PermissionError:
            QMessageBox.warning(self, "Kaydedilemedi", "Dosya başka bir uygulamada açık veya yazma izni yok.\n'Farklı kaydet' ile başka bir yere kaydedin.")
            return False
        except Exception as e:
            QMessageBox.critical(self, "Hata", str(e))
            return False
        self.saved_vidx = self.vidx
        self.titleChanged.emit()
        toast(self, f"Kaydedildi: {os.path.basename(target)}", "ok")
        return True

    def close_tab(self) -> bool:
        if self.dirty:
            r = QMessageBox.question(self, "Kaydedilmemiş değişiklikler",
                                     f"“{os.path.basename(self.orig_path)}” üzerindeki değişiklikler kaydedilsin mi?",
                                     QMessageBox.Save | QMessageBox.Discard | QMessageBox.Cancel)
            if r == QMessageBox.Cancel:
                return False
            if r == QMessageBox.Save and not self.save():
                return False
        if self.doc:
            self.doc.close()
        shutil.rmtree(self.workdir, ignore_errors=True)
        return True

    def _page_changed(self, i):
        self.page_spin.setValue(i + 1)
        self.thumbs.set_current(i)

    # ------------------------------------------------------------ araçlar
    def set_tool(self, tool):
        self.view.set_tool(tool, MARKUP_COLORS.get(tool, self.color))
        for b in self.ribbon.tool_group.buttons():
            if b.property("tool") == tool:
                b.setChecked(True)
        self._clear_edit_overlay()
        if tool == "edit_text":
            self._show_edit_overlay()
            self.status_msg.setText("Düzenlemek istediğiniz paragrafa tıklayın")
        elif tool == "redact":
            self.status_msg.setText("Karartılacak alanları çizin, sonra 'Karartmayı uygula'ya basın")
        elif tool == "sign":
            self.status_msg.setText("İmzanın görüneceği kutuyu çizin")
        elif tool.startswith("field_"):
            self.status_msg.setText("Alanın konumunu çizin")
        else:
            self.status_msg.setText("")

    def _pick_color(self):
        c = QColorDialog.getColor(QColor(self.color), self, "Çizim rengi")
        if c.isValid():
            self.color = c.name()
            self._update_color_icon()
            self.view.tool_color = QColor(self.color)

    def _update_color_icon(self):
        self.color_btn.setIcon(theme.icon("mdi6.palette", self.color))

    def author(self):
        return Settings.get("author") or ann.default_author()

    # ---- metin düzenleme katmanı
    def _show_edit_overlay(self):
        self._edit_blocks = {}
        for i in range(self.doc.page_count):
            if i > 300:
                break
            try:
                self._edit_blocks[i] = editing.text_blocks(self.path, i)
            except Exception:
                self._edit_blocks[i] = []
        for i, blocks in self._edit_blocks.items():
            for b in blocks:
                it = QGraphicsRectItem()
                it.setPen(QPen(QColor(theme.C["primary"]), 1, Qt.DashLine))
                c = QColor(theme.C["primary"])
                c.setAlpha(18)
                it.setBrush(QBrush(c))
                it.setZValue(4)
                self.view.scene().addItem(it)
                self._edit_items.append((it, i, b["bounds"]))
        self._reposition_overlays()

    def _clear_edit_overlay(self):
        for it, _, _ in self._edit_items:
            try:
                if it.scene():
                    self.view.scene().removeItem(it)
            except RuntimeError:
                pass
        self._edit_items = []

    def _reposition_overlays(self):
        for it, page, rect in self._edit_items + self._redact_items:
            l, b, r, t = rect
            it.setRect(self.view.pdf_rect_to_scene(page, (l - 2, b - 2, r + 2, t + 2)))
        if self.form_mode:
            self._position_form_proxies()

    def _edit_block_at(self, page, pt):
        x, y = pt
        for b in getattr(self, "_edit_blocks", {}).get(page, []):
            l, bb, r, t = b["bounds"]
            if l - 3 <= x <= r + 3 and bb - 3 <= y <= t + 3:
                return b
        return None

    def _open_text_editor(self, page, block):
        d = QDialog(self)
        d.setWindowTitle("Metni düzenle")
        d.resize(560, 360)
        v = QVBoxLayout(d)
        v.addWidget(label("Metni düzenle", "H2"))
        v.addWidget(label("Paragraf yeni metne göre yeniden akıtılır. Satır sonu için Enter kullanın.", "Muted"))
        ed = QPlainTextEdit(block["text"])
        v.addWidget(ed, 1)
        h = QHBoxLayout()
        size = QSpinBox()
        size.setRange(4, 120)
        size.setValue(round(block["size"]))
        bold = QCheckBox("Kalın")
        bold.setChecked(block["bold"])
        align = QComboBox()
        for k, t in (("left", "Sola"), ("center", "Ortala"), ("right", "Sağa"), ("justify", "İki yana")):
            align.addItem(t, k)
        col = ["#000000"]
        cb = button("Renk", "mdi6.palette-outline")

        def pick():
            c = QColorDialog.getColor(QColor(col[0]), d)
            if c.isValid():
                col[0] = c.name()
                cb.setIcon(theme.icon("mdi6.palette", col[0]))
        cb.clicked.connect(pick)
        h.addWidget(QLabel("Boyut"))
        h.addWidget(size)
        h.addWidget(bold)
        h.addWidget(align)
        h.addWidget(cb)
        h.addStretch()
        v.addLayout(h)
        hb = QHBoxLayout()
        hb.addStretch()
        hb.addWidget(button("İptal", slot=d.reject))
        hb.addWidget(button("Uygula", obj="Primary", slot=d.accept))
        v.addLayout(hb)
        if d.exec():
            txt = ed.toPlainText()
            ratio = size.value() / max(1, round(block["size"]))
            self.apply("Metin düzenlendi", editing.edit_text_block, page, block["bounds"], txt, float(size.value()),
                       block["first_baseline"], (block["leading"] or block["size"] * 1.2) * ratio, bold.isChecked(),
                       col[0], "Sans", True, align.currentData())
            self.set_tool("edit_text")

    # ---- olaylar
    def on_point(self, page, pt):
        tool = self.view.tool
        x, y = pt
        if tool == "select":
            hit = self._annotation_at(page, pt)
            if hit:
                self.annotation_menu(hit, self.cursor().pos())
            return
        if tool == "edit_text":
            b = self._edit_block_at(page, pt)
            if b:
                self._open_text_editor(page, b)
            return
        if tool == "note":
            t, ok = QInputDialog.getMultiLineText(self, "Not ekle", "Not:")
            if ok and t:
                self.apply("Not eklendi", ann.add_note, page, x, y, t, "#FFC107", self.author())
            return
        if tool == "add_text":
            v = FormDialog.ask(self, "Metin ekle", [("text", "Metin", "multiline", ""), ("size", "Boyut", "float", 12, (4, 144, 1)),
                                                  ("bold", "Kalın", "check", False), ("color", "Renk", "color", "#000000"),
                                                  ("family", "Yazı tipi", "combo", "Sans", [("Sans", "Sans"), ("Serif", "Serif"), ("Mono", "Eş aralıklı")])])
            if v and v["text"]:
                px, py = self._pdf_to_visual(page, x, y)
                self.apply("Metin eklendi", editing.add_text, page, px, py - v["size"], v["text"], v["size"], v["color"],
                           v["bold"], v["family"])
            return
        if tool == "stamp":
            px, py = self._pdf_to_visual(page, x, y)
            kind = getattr(self, "_stamp_kind", ("ONAYLANDI", None))
            self.apply("Damga eklendi", editing.add_stamp, kind[0] if not kind[1] else "", [page], px - 80, py - 30,
                       None, True, kind[1], 160, self.author())

    def _pdf_to_visual(self, page, x, y):
        """PDF sayfa koordinatını, kullanıcıya göründüğü (döndürülmüş) sol-alt orijinli koordinata çevirir."""
        it = self.view.items_[page]
        dx, dy = self.doc.page_to_device(page, it.w_pt, it.h_pt, x, y)
        return dx, it.h_pt - dy

    def _visual_rect(self, page, rect):
        l, b, r, t = rect
        x0, y0 = self._pdf_to_visual(page, l, b)
        x1, y1 = self._pdf_to_visual(page, r, t)
        return min(x0, x1), min(y0, y1), max(x0, x1), max(y0, y1)

    def on_rect(self, page, rect):
        tool = self.view.tool
        if tool == "zoom_area":
            sr = self.view.pdf_rect_to_scene(page, rect)
            z = self.view.zoom() * min(self.view.viewport().width() / max(sr.width(), 10), self.view.viewport().height() / max(sr.height(), 10)) * 0.9
            self.view.fit_mode = "none"
            self.view.set_zoom(z)
            sr = self.view.pdf_rect_to_scene(page, rect)
            self.view.centerOn(sr.center())
            return
        if tool in ("square", "circle"):
            self.apply("Şekil eklendi", ann.add_shape, page, rect, "Square" if tool == "square" else "Circle", self.color,
                       1.5, "", self.author())
        elif tool == "line":
            self.apply("Ok eklendi", ann.add_shape, page, rect, "Line", self.color, 1.5, "", self.author())
        elif tool == "freetext":
            t, ok = QInputDialog.getMultiLineText(self, "Metin kutusu", "Metin:")
            if ok and t:
                self.apply("Metin kutusu eklendi", ann.add_freetext, page, rect, t, 11, "#000000", self.color, "#FFFFFF", self.author())
        elif tool == "image":
            f = open_files(self, "Görsel seç", IMG_FILTER, multi=False)
            if f:
                l, b, r, t = self._visual_rect(page, rect)
                from PIL import Image
                im = Image.open(f[0])
                w = r - l
                h = w * im.height / im.width
                if h > t - b:
                    h = t - b
                    w = h * im.width / im.height
                self.apply("Görsel eklendi", editing.add_image, page, f[0], l, t - h, w, h)
        elif tool == "link":
            v = FormDialog.ask(self, "Bağlantı ekle", [("kind", "Tür", "combo", "url", [("url", "Web adresi"), ("page", "Belgedeki sayfa")]),
                                                     ("url", "Adres", "text", "https://"), ("page", "Sayfa", "int", 1, (1, self.doc.page_count))])
            if v:
                self.apply("Bağlantı eklendi", editing.add_link, page, rect, v["url"] if v["kind"] == "url" else None,
                           None if v["kind"] == "url" else v["page"] - 1)
        elif tool == "redact":
            self._add_redact_mark(page, rect)
            n = sum(len(v) for v in self.pending_redactions.values())
            self.show_banner("mdi6.square", f"{n} alan karartma için işaretlendi. Uygulandığında içerik kalıcı olarak silinir.",
                             [("Karartmayı uygula", self.apply_redactions), ("Temizle", self.clear_redactions)])
        elif tool == "crop":
            v = FormDialog.ask(self, "Sayfaları kırp", [page_scope_field(self.doc.page_count, page), ("range", "Özel aralık", "text", "", "örn. 1-3,5")],
                               subtitle="Çizdiğiniz alan dışında kalan bölümler gizlenecek (kırpma kutusu).")
            if v:
                idx = resolve_pages(v, self.doc.page_count, page) or list(range(self.doc.page_count))
                l, b, r, t = rect
                import pikepdf
                with pikepdf.open(self.path) as pdf:
                    mb = [float(x) for x in pdf.pages[page].mediabox]
                margins = (l - mb[0], b - mb[1], mb[2] - r, mb[3] - t)
                self.apply("Sayfalar kırpıldı", lambda s, o: pg.crop(s, idx, margins, o))
            self.set_tool("select")
        elif tool == "sign":
            self.op_sign(page, rect)
        elif tool.startswith("field_"):
            self._add_field(page, rect, tool[6:])

    def _add_redact_mark(self, page, rect):
        self.pending_redactions.setdefault(page, []).append(rect)
        it = QGraphicsRectItem()
        it.setBrush(QBrush(QColor(0, 0, 0, 160)))
        it.setPen(QPen(QColor("#C62828"), 1.2))
        it.setZValue(8)
        self.view.scene().addItem(it)
        self._redact_items.append((it, page, rect))
        self._reposition_overlays()

    def on_ink(self, page, strokes):
        self.apply("Çizim eklendi", ann.add_ink, page, strokes, self.color, 2, self.author())

    def on_text_selected(self, page, rect, text):
        tool = self.view.tool
        words = [w for w in self.doc.words(page) if not (w[1][2] < rect[0] or w[1][0] > rect[2] or w[1][3] < rect[1] or w[1][1] > rect[3])]
        rects = self._line_rects([w[1] for w in words])
        if tool in ("highlight", "underline", "strike"):
            kind = {"highlight": "Highlight", "underline": "Underline", "strike": "StrikeOut"}[tool]
            self.apply("İşaretleme eklendi", ann.add_markup, page, rects, kind, MARKUP_COLORS[tool], self.author(), "")
            self.view.clear_selection()
            return
        self._selected = (page, rects, text)
        m = QMenu(self)
        m.addAction(theme.icon("mdi6.content-copy"), "Kopyala", self.copy_selection)
        m.addAction(theme.icon("mdi6.table"), "Tablo olarak kopyala", lambda: self.copy_table(page, rect))
        m.addSeparator()
        m.addAction(theme.icon("mdi6.marker", "#F9A825"), "Vurgula", lambda: self._markup_sel("Highlight", "#FFEB3B"))
        m.addAction(theme.icon("mdi6.format-underline"), "Altını çiz", lambda: self._markup_sel("Underline", "#1E88E5"))
        m.addAction(theme.icon("mdi6.format-strikethrough"), "Üstünü çiz", lambda: self._markup_sel("StrikeOut", "#E53935"))
        m.addAction(theme.icon("mdi6.comment-text-outline"), "Yorumla vurgula…", self._markup_comment)
        m.addSeparator()
        m.addAction(theme.icon("mdi6.square"), "Karart", lambda: self._redact_sel())
        m.addAction(theme.icon("mdi6.magnify"), "Belgede ara", lambda: (self._show_panel(2), self.searchp.focus(text), self.searchp.search()))
        m.addAction(theme.icon("mdi6.link-variant"), "Bağlantı ekle…", lambda: (setattr(self.view, "tool", "link"), self.on_rect(page, rect), self.set_tool("select")))
        m.exec(self.cursor().pos())

    @staticmethod
    def _line_rects(rects):
        rects = sorted(rects, key=lambda r: (-round(r[1]), r[0]))
        out = []
        for r in rects:
            if out and abs(out[-1][1] - r[1]) < (r[3] - r[1]) * 0.5:
                l, b, rr, t = out[-1]
                out[-1] = (min(l, r[0]), min(b, r[1]), max(rr, r[2]), max(t, r[3]))
            else:
                out.append(tuple(r))
        return out

    def copy_selection(self):
        if self._selected:
            QGuiApplication.clipboard().setText(self._selected[2])
            toast(self, "Metin panoya kopyalandı", "ok")

    def copy_table(self, page, rect):
        it = self.view.items_[page]
        dpi = 300
        img = self.doc.render(page, scale=dpi / 72)
        x0, y0, x1, y1 = self.doc.rect_to_device(page, img.width, img.height, rect)
        res = ocrmod.recognize_image(img, Settings.get("ocr_lang"), dpi, areas=[{"kind": "table", "box": (x0, y0, x1, y1)}])
        tsv = "\n".join(b.text for b in res.blocks)
        html = "".join("<table border=1>" + "".join("<tr>" + "".join(f"<td>{c['text']}</td>" for c in row) + "</tr>" for row in b.cells) + "</table>"
                       for b in res.blocks if b.kind == "table")
        from PySide6.QtCore import QMimeData
        md = QMimeData()
        md.setText(tsv)
        if html:
            md.setHtml(html)
        QGuiApplication.clipboard().setMimeData(md)
        toast(self, "Tablo panoya kopyalandı (Excel'e yapıştırabilirsiniz)", "ok")

    def _markup_sel(self, kind, color, contents=""):
        if self._selected:
            page, rects, _ = self._selected
            self.apply("İşaretleme eklendi", ann.add_markup, page, rects, kind, color, self.author(), contents)
            self.view.clear_selection()

    def _markup_comment(self):
        t, ok = QInputDialog.getMultiLineText(self, "Yorum", "Yorumunuz:")
        if ok:
            self._markup_sel("Highlight", "#FFEB3B", t)

    def _redact_sel(self):
        if self._selected:
            page, rects, _ = self._selected
            for r in rects:
                self.view.tool = "redact"
                self.on_rect(page, r)
            self.view.tool = "select"
            self.view.clear_selection()

    def apply_redactions(self):
        regions = dict(self.pending_redactions)
        self.clear_redactions()
        self.banner.hide()
        self.apply_async("Karartma uygulanıyor…", "Karartma uygulandı (içerik kalıcı olarak silindi)",
                         security.redact_regions, regions, "#000000", True, 200, Settings.get("ocr_lang"))
        self.set_tool("select")

    def clear_redactions(self):
        for it, _, _ in self._redact_items:
            try:
                self.view.scene().removeItem(it)
            except RuntimeError:
                pass
        self._redact_items = []
        self.pending_redactions = {}
        self.banner.hide()

    def _annotation_at(self, page, pt):
        x, y = pt
        try:
            for d in ann.list_annotations(self.path):
                if d["page"] == page:
                    l, b, r, t = d["rect"]
                    if l - 2 <= x <= r + 2 and b - 2 <= y <= t + 2:
                        return d
        except Exception:
            pass
        return None

    def annotation_menu(self, d, gpos):
        m = QMenu(self)
        m.addSection(f"{d['kind_tr']} · {d['author']}")

        def edit():
            t, ok = QInputDialog.getMultiLineText(self, "Yorumu düzenle", "Metin:", d["contents"])
            if ok:
                self.apply("Yorum güncellendi", ann.edit_contents, d["objgen"], t)

        def rep():
            t, ok = QInputDialog.getMultiLineText(self, "Yanıtla", "Yanıtınız:")
            if ok and t:
                self.apply("Yanıt eklendi", ann.reply, d["objgen"], t, self.author())
        m.addAction(theme.icon("mdi6.pencil-outline"), "Düzenle…", edit)
        m.addAction(theme.icon("mdi6.reply-outline"), "Yanıtla…", rep)
        sm = m.addMenu(theme.icon("mdi6.flag-outline"), "Durum")
        for k, t in ann.STATES.items():
            sm.addAction(t, lambda k=k: self.apply(f"Durum: {ann.STATES[k]}", ann.set_status, d["objgen"], k, self.author()))
        m.addSeparator()
        m.addAction(theme.icon("mdi6.delete-outline"), "Sil", lambda: self.apply("Yorum silindi", ann.delete_annotation, d["objgen"]))
        m.exec(gpos)

    def on_context(self, page, pt, gpos):
        d = self._annotation_at(page, pt)
        if d:
            return self.annotation_menu(d, gpos)
        m = QMenu(self)
        if self._selected:
            m.addAction(theme.icon("mdi6.content-copy"), "Seçimi kopyala", self.copy_selection)
        m.addAction(theme.icon("mdi6.text"), "Sayfa metnini kopyala", lambda: (QGuiApplication.clipboard().setText(self.doc.page_text(page)), toast(self, "Sayfa metni kopyalandı", "ok")))
        m.addSeparator()
        m.addAction(theme.icon("mdi6.rotate-right"), "Sayfayı döndür", lambda: self.op_rotate([page], 90))
        m.addAction(theme.icon("mdi6.delete-outline"), "Sayfayı sil", lambda: self.op_delete([page]))
        m.addAction(theme.icon("mdi6.text-recognition"), "Bu sayfayı OCR'la", lambda: self.op_ocr([page]))
        m.addAction(theme.icon("mdi6.image-outline"), "Sayfayı görüntü olarak kaydet…", lambda: self.op_save_page_image(page))
        m.exec(gpos)

    # ------------------------------------------------------------ sayfa işlemleri
    def _pages_or_current(self, idx):
        return idx if idx is not None else [self.view.current_page()]

    def op_rotate(self, idx, angle):
        idx = self._pages_or_current(idx)
        self.apply("Sayfa döndürüldü", lambda s, o: pg.rotate(s, idx, angle, o))

    def op_delete(self, idx):
        idx = self._pages_or_current(idx)
        if self.doc.page_count - len(idx) < 1:
            QMessageBox.warning(self, "Silinemez", "Belgede en az bir sayfa kalmalıdır.")
            return
        self.apply(f"{len(idx)} sayfa silindi", lambda s, o: pg.delete(s, idx, o))

    def op_extract(self, idx):
        if idx is None:
            v = FormDialog.ask(self, "Sayfaları çıkar", [("range", "Sayfalar", "text", str(self.view.current_page() + 1), "örn. 1-3,5"),
                                                       ("delete", "Çıkarılan sayfaları bu belgeden sil", "check", False)])
            if not v:
                return
            idx = pg.parse_ranges(v["range"], self.doc.page_count)
            delete = v["delete"]
        else:
            delete = False
        out = save_file(self, "Çıkarılan sayfaları kaydet", os.path.splitext(os.path.basename(self.orig_path))[0] + "_sayfalar.pdf")
        if out:
            pg.extract_pages(self.path, idx, out)
            toast(self, f"{len(idx)} sayfa kaydedildi", "ok")
            if delete:
                self.op_delete(idx)
            self.main.offer_open(out)

    def op_insert_blank(self, at):
        self.apply("Boş sayfa eklendi", lambda s, o: pg.insert_blank(s, at, o))

    def op_insert_file(self):
        fs = open_files(self, "Eklenecek dosyalar")
        if not fs:
            return
        v = FormDialog.ask(self, "Sayfa ekle", [("where", "Konum", "combo", "after", [("after", "Geçerli sayfadan sonra"), ("before", "Geçerli sayfadan önce"),
                                                                                     ("end", "Belgenin sonuna"), ("start", "Belgenin başına")])])
        if not v:
            return
        at = {"after": self.view.current_page() + 1, "before": self.view.current_page(), "end": self.doc.page_count, "start": 0}[v["where"]]

        def op(src, out):
            tmp = out + ".in.pdf"
            convert.any_to_pdf(fs, tmp)
            pg.insert_from(src, tmp, at, out)
            os.remove(tmp)
            return out
        self.apply_async("Sayfalar ekleniyor…", "Sayfalar eklendi", op)

    def op_insert_scan(self):
        from .scanner import acquire_images
        imgs = acquire_images(self)
        if not imgs:
            return

        def op(src, out):
            tmp = out + ".scan.pdf"
            ocrmod.ocr_images_to_pdf(imgs, tmp, Settings.get("ocr_lang"))
            pg.insert_from(src, tmp, self.view.current_page() + 1, out)
            return out
        self.apply_async("Taranan sayfalar ekleniyor…", "Taranan sayfalar eklendi", op)

    def op_split(self):
        v = FormDialog.ask(self, "Belgeyi böl", [
            ("mode", "Yöntem", "combo", "every", [("every", "Her N sayfada bir"), ("ranges", "Sayfa aralıklarına göre"),
                                                  ("bookmarks", "Üst düzey yer imlerine göre"), ("size", "Dosya boyutuna göre (MB)"),
                                                  ("blank", "Boş sayfalara göre (ayırıcı)")]),
            ("n", "N sayfa", "int", 1, (1, 10000)), ("ranges", "Aralıklar", "text", "", "örn. 1-3;4-10;11-"),
            ("mb", "En fazla MB", "float", 5, (0.1, 2000, 0.5)), ("dir", "Çıktı klasörü", "dir", os.path.dirname(self.orig_path))])
        if not v:
            return
        if v["mode"] == "blank":
            from ..core.tasks import Task, run_task as rt
            t = Task("böl", [{"type": "split_blank", "params": {}}, {"type": "save", "params": {}}])
            src = os.path.join(self.workdir, os.path.splitext(os.path.basename(self.orig_path))[0] + ".pdf")
            shutil.copyfile(self.path, src)
            run_task(self, "Bölünüyor…", rt, t, [src], v["dir"], log=lambda m: None, on_done=lambda outs: self._split_done(outs, v["dir"]))
            return
        base = os.path.join(self.workdir, "split_src")
        os.makedirs(base, exist_ok=True)
        src = os.path.join(base, os.path.splitext(os.path.basename(self.orig_path))[0] + ".pdf")
        shutil.copyfile(self.path, src)
        val = {"every": v["n"], "ranges": v["ranges"].replace(";", ";"), "bookmarks": None, "size": v["mb"]}[v["mode"]]
        run_task(self, "Bölünüyor…", pg.split, src, v["dir"], v["mode"], val, on_done=lambda outs: self._split_done(outs, v["dir"]))

    def _split_done(self, outs, d):
        toast(self, f"{len(outs)} dosya oluşturuldu", "ok")
        if confirm(self, "Bölme tamamlandı", f"{len(outs)} dosya oluşturuldu.\nKlasör açılsın mı?"):
            open_path(d)

    def op_blank_pages(self):
        blanks = pg.detect_blank_pages(self.path)
        if not blanks:
            toast(self, "Boş sayfa bulunamadı", "info")
            return
        if len(blanks) >= self.doc.page_count:
            QMessageBox.information(self, "Boş sayfalar", "Tüm sayfalar boş görünüyor.")
            return
        if confirm(self, "Boş sayfalar", f"{len(blanks)} boş sayfa bulundu: {', '.join(str(b + 1) for b in blanks[:30])}\n\nSilinsin mi?"):
            self.apply(f"{len(blanks)} boş sayfa silindi", lambda s, o: pg.delete(s, blanks, o))

    def op_page_labels(self):
        v = FormDialog.ask(self, "Sayfa numaralandırma", [("style", "Biçim", "combo", "D", [("D", "1, 2, 3"), ("r", "i, ii, iii"), ("R", "I, II, III"),
                                                                                             ("a", "a, b, c"), ("A", "A, B, C")]),
                                                         ("start", "Başlangıç", "int", 1, (1, 100000)), ("prefix", "Önek", "text", "")],
                           subtitle="Görüntüleyicilerde gösterilen sayfa etiketlerini değiştirir.")
        if v:
            self.apply("Sayfa etiketleri güncellendi", pg.page_labels_renumber, v["start"], v["style"], v["prefix"])

    def op_save_page_image(self, i):
        out = save_file(self, "Sayfayı görüntü olarak kaydet", f"sayfa_{i + 1}.png", "PNG (*.png);;JPEG (*.jpg)")
        if out:
            self.doc.render_dpi(i, 200).save(out)
            toast(self, "Görüntü kaydedildi", "ok")

    def op_export_images(self):
        v = FormDialog.ask(self, "Sayfaları görüntü olarak kaydet", [("fmt", "Biçim", "combo", "png", [("png", "PNG"), ("jpg", "JPEG"), ("tif", "TIFF (çok sayfalı)")]),
                                                                    ("dpi", "Çözünürlük (dpi)", "int", 200, (50, 600)),
                                                                    ("dir", "Klasör", "dir", os.path.dirname(self.orig_path))])
        if not v:
            return
        base = os.path.splitext(os.path.basename(self.orig_path))[0]

        def work(progress=None):
            outs = []
            imgs = []
            with PdfDocument(self.path) as d:
                for i in range(d.page_count):
                    if progress:
                        progress(i, d.page_count)
                    im = d.render_dpi(i, v["dpi"])
                    if v["fmt"] == "tif":
                        imgs.append(im)
                    else:
                        p = os.path.join(v["dir"], f"{base}_{i + 1:03d}.{v['fmt']}")
                        im.save(p, quality=90)
                        outs.append(p)
            if imgs:
                p = os.path.join(v["dir"], f"{base}.tif")
                imgs[0].save(p, save_all=True, append_images=imgs[1:], compression="tiff_lzw", dpi=(v["dpi"], v["dpi"]))
                outs.append(p)
            return outs
        run_task(self, "Görüntüler kaydediliyor…", work, on_done=lambda outs: toast(self, f"{len(outs)} dosya kaydedildi", "ok"))

    # ------------------------------------------------------------ düzenleme işlemleri
    def _preview_page(self, fn):
        """Önizleme: geçerli sayfaya işlemi uygulayıp küçük görüntü döndürür."""
        def prev(vals):
            src = os.path.join(self.workdir, "prev_src.pdf")
            out = os.path.join(self.workdir, "prev_out.pdf")
            pg.extract_pages(self.path, [self.view.current_page()], src)
            fn(src, out, vals)
            with PdfDocument(out) as d:
                return d.render(0, 0.45)
        return prev

    def op_watermark(self):
        fields = [("text", "Metin", "text", "GİZLİ"), ("image", "veya görsel", "file", "", IMG_FILTER), ("size", "Boyut", "float", 60, (6, 300, 2)),
                  ("color", "Renk", "color", "#C62828"), ("opacity", "Opaklık (%)", "slider", 25, (5, 100)),
                  ("angle", "Açı", "int", 45, (-180, 180)), ("under", "İçeriğin arkasına yerleştir", "check", False),
                  page_scope_field(self.doc.page_count, self.view.current_page()), ("range", "Özel aralık", "text", "", "örn. 1-3,5")]

        def run(src, out, v):
            editing.add_watermark(src, out, v["text"], v["image"] or None, v["size"], v["color"], v["opacity"] / 100,
                                  v["angle"], v["under"])
        d = FormDialog(self, "Filigran ekle", fields, preview=self._preview_page(run))
        if d.exec():
            v = d.values()
            self.apply("Filigran eklendi", editing.add_watermark, v["text"], v["image"] or None, v["size"], v["color"],
                       v["opacity"] / 100, v["angle"], v["under"], resolve_pages(v, self.doc.page_count, self.view.current_page()))

    def op_header_footer(self):
        tips = "Kullanılabilir alanlar: {sayfa} {toplam} {tarih} {saat} {dosya}"
        fields = [("hl", "Üst sol", "text", ""), ("hc", "Üst orta", "text", ""), ("hr", "Üst sağ", "text", "{dosya}"),
                  ("fl", "Alt sol", "text", "{tarih}"), ("fc", "Alt orta", "text", "Sayfa {sayfa} / {toplam}"), ("fr", "Alt sağ", "text", ""),
                  ("size", "Yazı boyutu", "float", 9, (5, 40, 1)), ("color", "Renk", "color", "#333333"),
                  ("margin", "Kenar boşluğu (pt)", "float", 24, (4, 144, 2)), ("start", "İlk sayfa numarası", "int", 1, (1, 100000)),
                  page_scope_field(self.doc.page_count, self.view.current_page()), ("range", "Özel aralık", "text", "", "örn. 2-")]

        def run(src, out, v):
            editing.add_header_footer(src, out, {"left": v["hl"], "center": v["hc"], "right": v["hr"]},
                                      {"left": v["fl"], "center": v["fc"], "right": v["fr"]}, v["size"], v["color"], v["margin"])
        d = FormDialog(self, "Üstbilgi ve altbilgi", fields, subtitle=tips, preview=self._preview_page(run))
        if d.exec():
            v = d.values()
            self.apply("Üstbilgi/altbilgi eklendi", editing.add_header_footer, {"left": v["hl"], "center": v["hc"], "right": v["hr"]},
                       {"left": v["fl"], "center": v["fc"], "right": v["fr"]}, v["size"], v["color"], v["margin"],
                       resolve_pages(v, self.doc.page_count, self.view.current_page()), v["start"])

    def op_bates(self):
        v = FormDialog.ask(self, "Bates numaralandırma", [("prefix", "Önek", "text", "MSL-"), ("suffix", "Sonek", "text", ""),
                                                         ("start", "Başlangıç", "int", 1, (0, 10 ** 9)), ("digits", "Basamak", "int", 6, (1, 12)),
                                                         ("pos", "Konum", "combo", "right", [("left", "Alt sol"), ("center", "Alt orta"), ("right", "Alt sağ")]),
                                                         ("info", "", "info", "Birden çok belgeye ardışık numara vermek için Otomasyon > Görevler'i kullanın.")])
        if v:
            def op(src, out):
                d = os.path.join(self.workdir, "bates")
                outs, _ = editing.bates_number([src], d, v["prefix"], v["suffix"], v["start"], v["digits"], v["pos"])
                shutil.move(outs[0], out)
            self.apply("Bates numaraları eklendi", op)

    def _stamp_mode(self, kind):
        self._stamp_kind = (kind, None)
        self.set_tool("stamp")
        self.status_msg.setText(f"“{kind}” damgasını yerleştirmek için sayfaya tıklayın")

    def _custom_stamp(self):
        t, ok = QInputDialog.getText(self, "Özel damga", "Damga metni:")
        if ok and t:
            self._stamp_mode(t.upper())

    def _image_stamp(self):
        f = open_files(self, "Damga / imza görseli", IMG_FILTER, multi=False)
        if f:
            self._stamp_kind = ("", f[0])
            self.set_tool("stamp")
            self.status_msg.setText("Görseli yerleştirmek için sayfaya tıklayın")

    def op_remove_marks(self, mark):
        out = self._next_path()
        _, n = editing.remove_marks(self.path, out, mark)
        if n:
            self.push_version(out, f"{n} öge kaldırıldı")
            toast(self, f"{n} öge kaldırıldı", "ok")
        else:
            toast(self, "Bu uygulamayla eklenmiş öge bulunamadı", "warn")

    def op_metadata(self):
        md = editing.get_metadata(self.path)
        size = os.path.getsize(self.path)
        info = (f"Sayfa: {self.doc.page_count} · Boyut: {size / 1024:.0f} KB · Sayfa ölçüsü: "
                f"{self.doc.page_size(0)[0] / 72 * 25.4:.0f}×{self.doc.page_size(0)[1] / 72 * 25.4:.0f} mm · "
                f"Metin katmanı: {'var' if self.doc.has_text_layer() else 'yok'}")
        v = FormDialog.ask(self, "Belge özellikleri", [("info", "", "info", info), ("title", "Başlık", "text", md.get("Title", "")),
                                                      ("author", "Yazar", "text", md.get("Author", "")), ("subject", "Konu", "text", md.get("Subject", "")),
                                                      ("keywords", "Anahtar kelimeler", "text", md.get("Keywords", "")),
                                                      ("creator", "Oluşturan uygulama", "text", md.get("Creator", ""))], ok_text="Kaydet")
        if v:
            self.apply("Meta veriler güncellendi", editing.set_metadata, **v)

    def op_compress(self):
        v = FormDialog.ask(self, "PDF'i sıkıştır", [("level", "Seviye", "combo", "medium", [("low", "Düşük – kayıpsız (yalnızca yapı)"),
                                                                                         ("medium", "Orta – 150 dpi, iyi kalite"),
                                                                                         ("high", "Yüksek – 110 dpi, en küçük boyut (MRC)")])])
        if v:
            before = os.path.getsize(self.path)

            def done(res):
                out, b, a = res
                self.push_version(out, "Sıkıştırıldı")
                toast(self, f"{b / 1024:.0f} KB → {a / 1024:.0f} KB (%{100 - a * 100 // max(b, 1)} küçüldü)", "ok")
            out = self._next_path()
            run_task(self, "Sıkıştırılıyor…", editing.compress, self.path, out, v["level"], on_done=done)

    # ------------------------------------------------------------ koruma
    def op_protect(self):
        v = FormDialog.ask(self, "Parola ile koru", [
            ("user", "Açma parolası", "password", "", "boş bırakılabilir"), ("user2", "Açma parolası (tekrar)", "password", ""),
            ("owner", "İzin parolası", "password", "", "izinleri değiştirmek için"),
            ("method", "Şifreleme", "combo", "AES-256", [(k, k) for k in security.ENCRYPTIONS]),
            ("print", "Yazdırmaya izin ver", "check", True), ("copy", "Metin/görsel kopyalamaya izin ver", "check", True),
            ("modify", "Değiştirmeye izin ver", "check", True), ("annot", "Yorum eklemeye izin ver", "check", True),
            ("forms", "Form doldurmaya izin ver", "check", True)])
        if not v:
            return
        if v["user"] != v["user2"]:
            QMessageBox.warning(self, "Parola", "Açma parolaları eşleşmiyor.")
            return
        if not v["user"] and not v["owner"]:
            QMessageBox.warning(self, "Parola", "En az bir parola girin.")
            return
        out = save_file(self, "Korumalı PDF'i kaydet", os.path.splitext(os.path.basename(self.orig_path))[0] + "_korumali.pdf")
        if out:
            security.encrypt(self.path, out, v["user"], v["owner"], v["method"], v["print"], v["modify"], v["copy"], v["annot"], v["forms"])
            toast(self, "Korumalı belge kaydedildi", "ok")

    def op_unprotect(self):
        if not self.orig_password:
            toast(self, "Bu belge parola korumalı açılmadı", "info")
            return
        self.orig_password = None
        self.saved_vidx = -1
        self.titleChanged.emit()
        toast(self, "Parola kaldırıldı — kaydettiğinizde parolasız olacak", "ok")

    def op_redact_search(self):
        v = FormDialog.ask(self, "Ara ve karart", [
            ("terms", "Aranacak sözcükler (her satıra bir)", "multiline", ""),
            ("tc", "TC Kimlik numaraları", "check", True), ("iban", "IBAN numaraları", "check", True),
            ("mail", "E-posta adresleri", "check", False), ("tel", "Telefon numaraları", "check", False),
            ("card", "Kredi kartı numaraları", "check", False), ("date", "Tarihler", "check", False),
            ("regex", "Özel düzenli ifade", "text", "")], subtitle="Bulunan içerik kalıcı olarak silinir ve siyah kutuyla kapatılır.", ok_text="Bul ve karart")
        if not v:
            return
        pats = [n for k, n in (("tc", "TC Kimlik No"), ("iban", "IBAN"), ("mail", "E-posta"), ("tel", "Telefon"),
                               ("card", "Kredi Kartı"), ("date", "Tarih")) if v[k]]
        if v["regex"]:
            pats.append(v["regex"])
        terms = [t.strip() for t in v["terms"].splitlines() if t.strip()]
        regions = security.find_text_rects(self.path, terms, [security.PATTERNS.get(p, p) for p in pats])
        n = sum(len(x) for x in regions.values())
        if not n:
            toast(self, "Eşleşme bulunamadı", "info")
            return
        for pgi, rects in regions.items():
            for r in rects:
                self.view.tool = "redact"
                self.on_rect(pgi, r)
        self.view.tool = "select"
        self.show_banner("mdi6.square", f"{n} eşleşme karartma için işaretlendi. Kontrol edip uygulayın.",
                         [("Karartmayı uygula", self.apply_redactions), ("Temizle", self.clear_redactions)])

    def op_clean(self):
        v = FormDialog.ask(self, "Gizli verileri temizle", [("metadata", "Meta veriler (yazar, başlık, XMP)", "check", True),
                                                          ("attachments", "Ekli dosyalar", "check", True), ("javascript", "JavaScript ve otomatik eylemler", "check", True),
                                                          ("comments", "Yorumlar ve işaretlemeler", "check", True), ("hidden_layers", "Gizli katmanlar", "check", True),
                                                          ("forms", "Form alanları", "check", False), ("bookmarks", "Yer imleri", "check", False),
                                                          ("links", "Bağlantılar", "check", False)], ok_text="Temizle")
        if v:
            out = self._next_path()
            _, removed = security.remove_hidden_data(self.path, out, **v)
            self.push_version(out, "Gizli veriler temizlendi")
            toast(self, "Temizlendi: " + (", ".join(removed) or "temizlenecek veri yoktu"), "ok")

    def op_create_cert(self):
        v = FormDialog.ask(self, "Dijital kimlik oluştur", [("name", "Ad Soyad", "text", Settings.get("author") or ""),
                                                          ("org", "Kurum", "text", ""), ("email", "E-posta", "text", ""),
                                                          ("pw", "Parola", "password", ""), ("years", "Geçerlilik (yıl)", "int", 3, (1, 20)),
                                                          ("out", "Kaydedilecek dosya", "savefile", os.path.join(os.path.expanduser("~"), "imza.pfx"), "PKCS#12 (*.pfx *.p12)")],
                           subtitle="Kendinden imzalı sertifika belgelerin değiştirilmediğini kanıtlar; resmi geçerlilik için "
                                    "nitelikli elektronik sertifika (e-imza) kullanın.", ok_text="Oluştur")
        if v and v["name"] and v["pw"] and v["out"]:
            signmod.create_self_signed(v["out"], v["name"], v["pw"], v["email"], v["org"], years=v["years"])
            Settings.set("last_p12", v["out"])
            toast(self, "Dijital kimlik oluşturuldu", "ok")

    def op_sign(self, page, rect):
        last = Settings.get("last_p12", "")
        v = FormDialog.ask(self, "Belgeyi dijital olarak imzala", [
            ("source", "İmza kaynağı", "combo", "pfx", [("pfx", "Sertifika dosyası (.pfx/.p12)"), ("card", "Akıllı kart / e-imza (PKCS#11)")]),
            ("p12", "Sertifika dosyası", "file", last, "PKCS#12 (*.pfx *.p12)"), ("pw", "Parola / PIN", "password", ""),
            ("lib", "PKCS#11 sürücüsü", "file", r"C:\Windows\System32\akisp11.dll", "DLL (*.dll)"),
            ("reason", "İmza nedeni", "text", "Onaylıyorum"), ("location", "Yer", "text", ""),
            ("image", "İmza görseli (isteğe bağlı)", "file", "", IMG_FILTER),
            ("tsa", "Zaman damgası", "combo", None, [(v, k) for k, v in signmod.TSA_SERVERS.items()]),
            ("ltv", "Uzun süreli doğrulama (LTV) bilgisi göm", "check", False),
            ("certify", "Belgeyi onayla (sertifikalı imza)", "check", False)], ok_text="İmzala ve kaydet")
        if not v:
            self.set_tool("select")
            return
        out = save_file(self, "İmzalı belgeyi kaydet", os.path.splitext(os.path.basename(self.orig_path))[0] + "_imzali.pdf")
        if not out:
            return
        box = tuple(rect) if rect else None  # pyHanko kutusu sayfa koordinatındadır
        kw = dict(field_name=f"Imza{len(signmod.signature_fields(self.path)) + 1}", page=page or 0, box=box,
                  reason=v["reason"], location=v["location"], name=self.author(), tsa_url=v["tsa"], embed_validation=v["ltv"],
                  certify=v["certify"], image=v["image"] or None)
        if v["source"] == "pfx":
            Settings.set("last_p12", v["p12"])
            fn, args = signmod.sign_pdf, (self.path, out, v["p12"], v["pw"])
        else:
            fn, args = signmod.sign_pdf_pkcs11, (self.path, out, v["lib"], v["pw"])

        def done(_):
            toast(self, "Belge imzalandı", "ok")
            self.main.open_document(out)
        run_task(self, "İmzalanıyor…", fn, *args, on_done=done, **kw)
        self.set_tool("select")

    # ------------------------------------------------------------ formlar
    def _add_field(self, page, rect, kind):
        names = {f["name"] for f in forms.list_fields(self.path)}
        k = 1
        base = {"text": "Metin", "checkbox": "Onay", "radio": "Secenek", "combo": "Liste", "list": "Liste", "button": "Dugme", "signature": "Imza"}[kind]
        while f"{base}{k}" in names:
            k += 1
        fields = [("name", "Alan adı", "text", f"{base}{k}"), ("tooltip", "Araç ipucu", "text", "")]
        if kind in ("radio", "combo", "list"):
            fields.append(("options", "Seçenekler (her satıra bir)", "multiline", "Seçenek 1\nSeçenek 2"))
        if kind == "text":
            fields += [("value", "Varsayılan değer", "text", ""), ("multiline", "Çok satırlı", "check", False),
                       ("password", "Parola alanı", "check", False), ("maxlen", "En fazla karakter (0=sınırsız)", "int", 0, (0, 10000)),
                       ("size", "Yazı boyutu (0=otomatik)", "float", 0, (0, 72, 1))]
        if kind == "button":
            fields += [("value", "Düğme etiketi", "text", "Gönder"),
                       ("action", "Eylem", "combo", "reset", [("reset", "Formu sıfırla"), ("print", "Yazdır"), ("url", "Web adresi aç"),
                                                             ("submit", "Formu gönder (URL)"), ("js", "JavaScript")]),
                       ("avalue", "Adres / komut", "text", "")]
        if kind == "checkbox":
            fields.append(("value", "Varsayılan olarak işaretli", "check", False))
        fields += [("required", "Zorunlu", "check", False), ("readonly", "Salt okunur", "check", False)]
        v = FormDialog.ask(self, "Form alanı ekle", fields)
        if not v:
            return
        kw = dict(required=v["required"], readonly=v["readonly"], tooltip=v["tooltip"])
        if kind in ("radio", "combo", "list"):
            kw["options"] = [o.strip() for o in v["options"].splitlines() if o.strip()]
        if kind == "text":
            kw.update(value=v["value"], multiline=v["multiline"], password=v["password"], max_len=v["maxlen"] or None, font_size=v["size"])
        if kind == "button":
            kw.update(value=v["value"], action={"type": v["action"], "value": v["avalue"]})
        if kind == "checkbox":
            kw["value"] = "Evet" if v["value"] else ""
        self.apply("Form alanı eklendi", forms.add_field, page, kind, v["name"], rect, **kw)

    def toggle_form_fill(self):
        self.form_mode = not self.form_mode
        self.b_fill.setChecked(self.form_mode)
        if self.form_mode:
            self.form_values = {}
            self._build_form_proxies()
            self.show_banner("mdi6.form-textbox", "Form doldurma modu: alanlara doğrudan yazın.",
                             [("Formu kaydet", self.commit_form), ("Temizle", self._clear_form_values)])
        else:
            self._clear_form_proxies()
            self.banner.hide()

    def _clear_form_proxies(self):
        for p, _, _ in self._form_proxies:
            try:
                w = p.widget()
                if w is not None:
                    w.blockSignals(True)
                    w.clearFocus()
                if p.scene():
                    self.view.scene().removeItem(p)
                p.deleteLater()
            except RuntimeError:
                pass
        self._form_proxies = []

    def _build_form_proxies(self):
        self._clear_form_proxies()
        try:
            flds = forms.list_fields(self.path)
        except Exception:
            flds = []
        style = (f"background: #EAF1FF; color: #111; border: 1px solid {theme.C['primary']}; border-radius: 2px; padding: 0 3px;")
        from PySide6.QtWidgets import QListWidget as _LW
        for f in flds:
            if f["page"] is None or f["type"] in ("button", "signature"):
                continue
            val = self.form_values.get(f["name"], f["value"])
            h_pt = f["rect"][3] - f["rect"][1]
            if f["type"] == "text" and h_pt > 30:
                w = QPlainTextEdit(val if isinstance(val, str) else "")
                w.textChanged.connect(lambda w=w, n=f["name"]: self._set_form_value(n, w.toPlainText()))
            elif f["type"] == "text":
                w = QLineEdit(val if isinstance(val, str) else "")
                w.textChanged.connect(lambda t, n=f["name"]: self._set_form_value(n, t))
            elif f["type"] == "checkbox":
                w = QCheckBox()
                w.setChecked(val not in ("", "Off", None, False))
                w.toggled.connect(lambda c, n=f["name"]: self._set_form_value(n, c))
            elif f["type"] == "combo":
                w = QComboBox()
                w.addItems(f["options"])
                if isinstance(val, str) and val in f["options"]:
                    w.setCurrentText(val)
                w.currentTextChanged.connect(lambda t, n=f["name"]: self._set_form_value(n, t))
            elif f["type"] == "list":
                w = _LW()
                w.addItems(f["options"])
                for k in range(w.count()):
                    if w.item(k).text() == val:
                        w.setCurrentRow(k)
                w.currentTextChanged.connect(lambda t, n=f["name"]: self._set_form_value(n, t))
            elif f["type"] == "radio":
                w = QComboBox()
                w.addItems(["—"] + f["options"])
                if val in f["options"]:
                    w.setCurrentText(val)
                w.currentTextChanged.connect(lambda t, n=f["name"]: self._set_form_value(n, "" if t == "—" else t))
            else:
                continue
            if f.get("tooltip"):
                w.setToolTip(f["tooltip"])
            if f["readonly"]:
                w.setEnabled(False)
            st = style if f["type"] != "checkbox" else "QCheckBox { background: #EAF1FF; }"
            if f["required"] and f["type"] != "checkbox":
                st = st.replace(theme.C["primary"], theme.C["danger"])
            w.setStyleSheet(st)
            proxy = self.view.scene().addWidget(w)
            proxy.setZValue(20)
            self._form_proxies.append((proxy, f["page"], f["rect"]))
        self._position_form_proxies()

    def _position_form_proxies(self):
        for proxy, page, rect in self._form_proxies:
            if page >= len(self.view.items_):
                continue
            r = self.view.pdf_rect_to_scene(page, rect)
            proxy.setGeometry(QRectF(r.x(), r.y(), max(r.width(), 14), max(r.height(), 14)))

    def _set_form_value(self, name, value):
        self.form_values[name] = value
        if not self.dirty or self.status_msg.text() != "Form değiştirildi":
            self.status_msg.setText("Form değiştirildi")

    def _clear_form_values(self):
        self.form_values = {f["name"]: ("" if f["type"] != "checkbox" else False) for f in forms.list_fields(self.path)}
        self._build_form_proxies()

    def commit_form(self):
        if not self.form_values:
            toast(self, "Değişiklik yok", "info")
            return
        vals = dict(self.form_values)
        self.form_values = {}
        missing = []
        self.apply("Form verileri kaydedildi", forms.fill, vals)
        missing = forms.validate_required(self.path)
        if missing:
            toast(self, "Doldurulmamış zorunlu alanlar: " + ", ".join(missing[:5]), "warn")

    def op_import_form(self):
        f = open_files(self, "Form verisi", "Form verisi (*.fdf *.json)", multi=False)
        if not f:
            return
        if f[0].lower().endswith(".json"):
            import json
            data = json.load(open(f[0], encoding="utf-8"))
            self.apply("Form verileri içe aktarıldı", forms.fill, data)
        else:
            self.apply("Form verileri içe aktarıldı", lambda src, out: forms.import_fdf(src, f[0], out))

    def op_export_form(self):
        out = save_file(self, "Form verilerini dışa aktar", os.path.splitext(os.path.basename(self.orig_path))[0] + ".fdf",
                        "FDF (*.fdf);;JSON (*.json)")
        if out:
            (forms.export_json if out.lower().endswith(".json") else forms.export_fdf)(self.path, out)
            toast(self, "Form verileri dışa aktarıldı", "ok")

    def op_flatten_form(self):
        if confirm(self, "Formu düzleştir", "Form alanları sabit içeriğe dönüştürülecek ve artık düzenlenemeyecek. Devam edilsin mi?"):
            self.apply("Form düzleştirildi", forms.fill, {}, True)

    # ------------------------------------------------------------ dönüştürme
    def op_export(self, fmt):
        ext = fmt
        out = save_file(self, f"{convert.EXPORT_FORMATS[fmt]} olarak kaydet",
                        os.path.splitext(os.path.basename(self.orig_path))[0] + "." + ext,
                        f"{convert.EXPORT_FORMATS[fmt]}")
        if not out:
            return
        mode = Settings.get("export_mode")

        def done(_):
            toast(self, f"Kaydedildi: {os.path.basename(out)}", "ok")
            if confirm(self, "Dönüştürme tamamlandı", f"{os.path.basename(out)} oluşturuldu.\nŞimdi açılsın mı?"):
                open_path(out)
        run_task(self, "Dönüştürülüyor…", convert.convert, [self.path], out, fmt, Settings.get("ocr_lang"), mode=mode, on_done=done)

    def op_pdfa(self):
        v = FormDialog.ask(self, "PDF/A'ya dönüştür", [("part", "Uyumluluk", "combo", 2, [(1, "PDF/A-1b"), (2, "PDF/A-2b (önerilen)"), (3, "PDF/A-3b (eklerle)")]),
                                                     ("ocr", "Taranmış sayfalara OCR uygula", "check", True)])
        if not v:
            return
        out = save_file(self, "PDF/A kaydet", os.path.splitext(os.path.basename(self.orig_path))[0] + "_PDFA.pdf")
        if not out:
            return

        def work():
            src = self.path
            if v["ocr"]:
                tmp = out + ".ocr.pdf"
                ocrmod.ocr_pdf(src, tmp, Settings.get("ocr_lang"))
                src = tmp
            r = convert.make_pdfa(src, out, v["part"])
            if v["ocr"]:
                os.remove(src)
            return r

        def done(res):
            _, warns = res
            msg = "PDF/A belgesi oluşturuldu." + ("\n\nUyarılar:\n• " + "\n• ".join(warns) if warns else "")
            QMessageBox.information(self, "PDF/A", msg)
        run_task(self, "PDF/A oluşturuluyor…", work, on_done=done)

    def op_pdfua(self):
        out = save_file(self, "Erişilebilir PDF kaydet", os.path.splitext(os.path.basename(self.orig_path))[0] + "_erisilebilir.pdf")
        if out:
            run_task(self, "Etiketli PDF oluşturuluyor…", convert.make_pdfua, self.path, out, None, "tr-TR", Settings.get("ocr_lang"),
                     on_done=lambda _: toast(self, "Erişilebilir PDF oluşturuldu", "ok"))

    def op_ocr(self, pages=None):
        if pages is not None and not isinstance(pages, list):
            pages = None
        v = FormDialog.ask(self, "OCR: metin katmanı ekle", [("lang", "Dil", "lang", Settings.get("ocr_lang")),
                                                           ("force", "Metni olan sayfaları da yeniden tanı", "check", False),
                                                           ("pre", "Görüntüyü iyileştir (eğiklik, arka plan) — sayfa görüntüsü değişir", "check", False)],
                           subtitle="Sayfa görüntüsü korunur, altına seçilebilir/aranabilir görünmez metin eklenir.", ok_text="Tanı")
        if not v:
            return
        from ..core.preprocess import PreprocessOptions
        opts = PreprocessOptions(**Settings.get("preprocess")) if v["pre"] else None
        self.apply_async("Metin tanınıyor…", "OCR tamamlandı: belge artık aranabilir", ocrmod.ocr_pdf, v["lang"], Settings.get("dpi"),
                         pages, not v["force"], opts, "replace" if v["pre"] else "under")
        self.banner.hide()

    def open_in_ocr_editor(self):
        self.main.open_ocr_editor([self.path], name=os.path.basename(self.orig_path))

    def op_compare(self):
        self.main.open_compare(self.orig_path if not self.dirty else self.path)

    # ------------------------------------------------------------ düzenleyici görünümü
    def show_organizer(self):
        from .organize import Organizer
        if self.center_stack.count() > 1:
            w = self.center_stack.widget(1)
            self.center_stack.removeWidget(w)
            w.deleteLater()
        org = Organizer(self)
        self.center_stack.addWidget(org)
        self.center_stack.setCurrentIndex(1)

    def close_organizer(self):
        self.center_stack.setCurrentIndex(0)

    # ------------------------------------------------------------ yazdırma
    def print_doc(self):
        from PySide6.QtPrintSupport import QPrinter, QPrintDialog
        v = FormDialog.ask(self, "Yazdırma seçenekleri", [("annots", "Yorumları ve işaretlemeleri yazdır", "check", True),
                                                        ("fit", "Kağıda sığdır", "check", True)], ok_text="Devam")
        if not v:
            return
        printer = QPrinter(QPrinter.HighResolution)
        printer.setDocName(os.path.basename(self.orig_path))
        dlg = QPrintDialog(printer, self)
        dlg.setMinMax(1, self.doc.page_count)
        if dlg.exec() != QDialog.Accepted:
            return
        first, last = printer.fromPage() or 1, printer.toPage() or self.doc.page_count
        painter = QPainter(printer)
        dpi = min(printer.resolution(), 300)
        from .pageview import pil_to_qpixmap
        for i in range(first - 1, last):
            if i > first - 1:
                printer.newPage()
            page = self.doc._doc[i]
            img = page.render(scale=dpi / 72, draw_annots=v["annots"], may_draw_forms=True).to_pil()
            page.close()
            pm = pil_to_qpixmap(img)
            rect = painter.viewport()
            size = pm.size()
            if v["fit"]:
                size.scale(rect.size(), Qt.KeepAspectRatio)
            else:
                f = printer.resolution() / dpi
                size = size * f
            painter.drawPixmap(int((rect.width() - size.width()) / 2), int((rect.height() - size.height()) / 2), size.width(), size.height(), pm)
        painter.end()
        toast(self, "Yazdırma gönderildi", "ok")
