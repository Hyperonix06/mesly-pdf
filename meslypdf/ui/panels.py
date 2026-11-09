"""Belge düzenleyicinin yan panelleri: küçük resimler, yer imleri, arama, yorumlar, ekler, imzalar, form alanları."""
from __future__ import annotations

import os

from PySide6.QtCore import Qt, QSize, QTimer
from PySide6.QtGui import QIcon, QColor
from PySide6.QtWidgets import (QWidget, QVBoxLayout, QHBoxLayout, QListWidget, QListWidgetItem, QTreeWidget,
                               QTreeWidgetItem, QLineEdit, QCheckBox, QPushButton, QLabel, QComboBox, QMenu,
                               QInputDialog, QFileDialog, QMessageBox, QToolButton, QAbstractItemView)

from ..core import annotations as ann, editing, sign as signmod, forms
from . import theme
from .common import button, label, toast, Settings, open_path


def _hdr(title, *buttons):
    w = QWidget()
    h = QHBoxLayout(w)
    h.setContentsMargins(10, 8, 6, 4)
    h.addWidget(label(title, "H3"))
    h.addStretch()
    for b in buttons:
        h.addWidget(b)
    return w


def _tb(icon_name, tip, slot):
    b = QToolButton()
    b.setIcon(theme.icon(icon_name))
    b.setToolTip(tip)
    b.setAutoRaise(True)
    b.clicked.connect(slot)
    b.setCursor(Qt.PointingHandCursor)
    return b


class ThumbnailPanel(QWidget):
    def __init__(self, tab):
        super().__init__()
        self.tab = tab
        v = QVBoxLayout(self)
        v.setContentsMargins(0, 0, 0, 0)
        v.addWidget(_hdr("Sayfalar", _tb("mdi6.view-grid-outline", "Sayfaları düzenle", tab.show_organizer)))
        self.list = QListWidget()
        self.list.setViewMode(QListWidget.IconMode)
        self.list.setFlow(QListWidget.TopToBottom)
        self.list.setWrapping(False)
        self.list.setIconSize(QSize(120, 160))
        self.list.setSpacing(6)
        self.list.setResizeMode(QListWidget.Adjust)
        self.list.setMovement(QListWidget.Static)
        self.list.setStyleSheet("QListWidget { border: none; background: transparent; }")
        self.list.currentRowChanged.connect(lambda r: r >= 0 and self.tab.view.go_to(r))
        self.list.setContextMenuPolicy(Qt.CustomContextMenu)
        self.list.customContextMenuRequested.connect(self._menu)
        v.addWidget(self.list)
        self._todo = []
        self._timer = QTimer(self, interval=15, timeout=self._render_next)

    def reload(self):
        self.list.blockSignals(True)
        self.list.clear()
        doc = self.tab.doc
        ph = QIcon()
        for i in range(doc.page_count):
            it = QListWidgetItem(ph, str(i + 1))
            it.setTextAlignment(Qt.AlignHCenter)
            it.setSizeHint(QSize(140, 190))
            self.list.addItem(it)
        self.list.blockSignals(False)
        self._todo = list(range(doc.page_count))
        self._timer.start()

    def _render_next(self):
        if not self._todo:
            self._timer.stop()
            return
        i = self._todo.pop(0)
        try:
            from .pageview import pil_to_qpixmap
            w, h = self.tab.doc.page_size(i)
            img = self.tab.doc.render(i, scale=150 / max(w, h))
            self.list.item(i).setIcon(QIcon(pil_to_qpixmap(img)))
        except Exception:
            pass

    def set_current(self, i):
        self.list.blockSignals(True)
        self.list.setCurrentRow(i)
        self.list.blockSignals(False)

    def _menu(self, pos):
        it = self.list.itemAt(pos)
        if not it:
            return
        i = self.list.row(it)
        m = QMenu(self)
        m.addAction(theme.icon("mdi6.rotate-right"), "Sağa döndür", lambda: self.tab.op_rotate([i], 90))
        m.addAction(theme.icon("mdi6.rotate-left"), "Sola döndür", lambda: self.tab.op_rotate([i], -90))
        m.addAction(theme.icon("mdi6.delete-outline"), "Sil", lambda: self.tab.op_delete([i]))
        m.addAction(theme.icon("mdi6.file-export-outline"), "Ayrı PDF olarak çıkar", lambda: self.tab.op_extract([i]))
        m.addAction(theme.icon("mdi6.file-plus-outline"), "Sonrasına boş sayfa ekle", lambda: self.tab.op_insert_blank(i + 1))
        m.addAction(theme.icon("mdi6.image-outline"), "Görüntü olarak kaydet…", lambda: self.tab.op_save_page_image(i))
        m.exec(self.list.mapToGlobal(pos))


class BookmarkPanel(QWidget):
    def __init__(self, tab):
        super().__init__()
        self.tab = tab
        v = QVBoxLayout(self)
        v.setContentsMargins(0, 0, 0, 0)
        v.addWidget(_hdr("Yer imleri", _tb("mdi6.bookmark-plus-outline", "Geçerli sayfaya yer imi ekle", self.add),
                         _tb("mdi6.auto-fix", "Başlıklardan otomatik oluştur", self.auto),
                         _tb("mdi6.pencil-outline", "Düzenle (tablo)", self.edit_table)))
        self.tree = QTreeWidget()
        self.tree.setHeaderHidden(True)
        self.tree.setStyleSheet("QTreeWidget { border: none; }")
        self.tree.itemClicked.connect(lambda it, c: it.data(0, Qt.UserRole) is not None and self.tab.view.go_to(it.data(0, Qt.UserRole)))
        self.tree.setContextMenuPolicy(Qt.CustomContextMenu)
        self.tree.customContextMenuRequested.connect(self._menu)
        v.addWidget(self.tree)
        self.empty = label("Bu belgede yer imi yok.\nBaşlıklardan otomatik oluşturmak için ✨ düğmesini kullanın.", "Muted", True)
        self.empty.setContentsMargins(12, 8, 12, 8)
        v.addWidget(self.empty)
        self.items = []

    def reload(self):
        self.tree.clear()
        try:
            self.items = editing.get_bookmarks(self.tab.path)
        except Exception:
            self.items = []
        stack = []
        for b in self.items:
            node = QTreeWidgetItem([b["title"]])
            node.setData(0, Qt.UserRole, b["page"])
            node.setIcon(0, theme.icon("mdi6.bookmark-outline"))
            while stack and stack[-1][0] >= b["level"]:
                stack.pop()
            (stack[-1][1].addChild(node) if stack else self.tree.addTopLevelItem(node))
            stack.append((b["level"], node))
        self.tree.expandAll()
        self.empty.setVisible(not self.items)
        self.tree.setVisible(bool(self.items))

    def add(self):
        t, ok = QInputDialog.getText(self, "Yer imi ekle", "Başlık:", text=f"Sayfa {self.tab.view.current_page() + 1}")
        if ok and t:
            items = self.items + [{"title": t, "page": self.tab.view.current_page(), "level": 0}]
            items.sort(key=lambda x: x["page"] if x["page"] is not None else 0) if not any(x["level"] for x in items) else None
            self.tab.apply("Yer imi eklendi", editing.set_bookmarks, items)

    def auto(self):
        self.tab.apply("Yer imleri başlıklardan oluşturuldu", editing.auto_bookmarks_from_headings)

    def edit_table(self):
        from .dialogs import TableDialog
        d = TableDialog(self, "Yer imlerini düzenle", ["Başlık", "Sayfa", "Düzey (0,1,2…)"],
                        [[b["title"], (b["page"] or 0) + 1, b["level"]] for b in self.items])
        if d.exec():
            items = []
            for r in d.rows():
                if r[0].strip():
                    try:
                        items.append({"title": r[0], "page": max(0, int(r[1] or 1) - 1), "level": int(r[2] or 0)})
                    except ValueError:
                        pass
            self.tab.apply("Yer imleri güncellendi", editing.set_bookmarks, items)

    def _menu(self, pos):
        it = self.tree.itemAt(pos)
        if not it:
            return
        idx = self._flat_index(it)
        m = QMenu(self)
        m.addAction("Yeniden adlandır", lambda: self._rename(idx))
        m.addAction("Sil", lambda: self._delete(idx))
        m.exec(self.tree.mapToGlobal(pos))

    def _flat_index(self, it):
        allit = []

        def walk(n):
            allit.append(n)
            for k in range(n.childCount()):
                walk(n.child(k))
        for k in range(self.tree.topLevelItemCount()):
            walk(self.tree.topLevelItem(k))
        return allit.index(it)

    def _rename(self, idx):
        t, ok = QInputDialog.getText(self, "Yeniden adlandır", "Başlık:", text=self.items[idx]["title"])
        if ok and t:
            items = [dict(b) for b in self.items]
            items[idx]["title"] = t
            self.tab.apply("Yer imi yeniden adlandırıldı", editing.set_bookmarks, items)

    def _delete(self, idx):
        items = [dict(b) for k, b in enumerate(self.items) if k != idx]
        self.tab.apply("Yer imi silindi", editing.set_bookmarks, items)


class SearchPanel(QWidget):
    def __init__(self, tab):
        super().__init__()
        self.tab = tab
        v = QVBoxLayout(self)
        v.setContentsMargins(10, 8, 10, 8)
        v.addWidget(label("Ara", "H3"))
        self.edit = QLineEdit()
        self.edit.setPlaceholderText("Belgede ara…  (Enter)")
        self.edit.setClearButtonEnabled(True)
        self.edit.addAction(theme.icon("mdi6.magnify"), QLineEdit.LeadingPosition)
        self.edit.returnPressed.connect(self.search)
        v.addWidget(self.edit)
        h = QHBoxLayout()
        self.case = QCheckBox("Büyük/küçük harf")
        self.whole = QCheckBox("Tam kelime")
        h.addWidget(self.case)
        h.addWidget(self.whole)
        v.addLayout(h)
        self.info = label("", "Muted")
        v.addWidget(self.info)
        self.list = QListWidget()
        self.list.setWordWrap(True)
        self.list.itemClicked.connect(self._go)
        v.addWidget(self.list, 1)
        h2 = QHBoxLayout()
        self.b_hl = button("Tümünü vurgula", "mdi6.marker", slot=self.highlight_all)
        self.b_red = button("Tümünü karart", "mdi6.square", slot=self.redact_all)
        self.b_rep = button("Değiştir…", "mdi6.find-replace", slot=self.replace_all)
        for b in (self.b_hl, self.b_red, self.b_rep):
            b.setEnabled(False)
            h2.addWidget(b)
        v.addLayout(h2)
        self.hits = []

    def focus(self, text=None):
        if text:
            self.edit.setText(text)
        self.edit.setFocus()
        self.edit.selectAll()

    def search(self):
        q = self.edit.text().strip()
        self.list.clear()
        self.hits = self.tab.doc.search(q, self.case.isChecked(), self.whole.isChecked()) if q else []
        texts = {}
        for k, h in enumerate(self.hits):
            if h.page not in texts:
                texts[h.page] = self.tab.doc.page_text(h.page).replace("\r", " ").replace("\n", " ")
            t = texts[h.page]
            pos = t.lower().find(h.text.lower())
            snippet = t[max(0, pos - 30): pos + len(h.text) + 40] if pos >= 0 else h.text
            it = QListWidgetItem(f"Sayfa {h.page + 1}\n…{snippet.strip()}…")
            it.setData(Qt.UserRole, k)
            self.list.addItem(it)
        self.info.setText(f"{len(self.hits)} sonuç" if q else "")
        self.tab.view.set_search_hits(self.hits)
        for b in (self.b_hl, self.b_red, self.b_rep):
            b.setEnabled(bool(self.hits))
        if self.hits:
            self._go(self.list.item(0))

    def _go(self, it):
        k = it.data(Qt.UserRole)
        h = self.hits[k]
        self.tab.view.set_search_hits(self.hits, k)
        self.tab.view.go_to(h.page, h.rects[0][3] if h.rects else None)

    def highlight_all(self):
        by_page = {}
        for h in self.hits:
            by_page.setdefault(h.page, []).extend(h.rects)

        def op(src, out):
            import shutil
            shutil.copyfile(src, out)
            for pg, rects in by_page.items():
                ann.add_markup(out, out, pg, rects, "Highlight", "#FFEB3B", Settings.get("author") or "")
            return out
        self.tab.apply(f"{len(self.hits)} sonuç vurgulandı", op)

    def redact_all(self):
        from ..core.security import redact_regions
        regions = {}
        for h in self.hits:
            regions.setdefault(h.page, []).extend(h.rects)
        if QMessageBox.question(self, "Karart", f"{len(self.hits)} sonuç kalıcı olarak karartılacak. Devam edilsin mi?") != QMessageBox.Yes:
            return
        self.tab.apply_async("Karartılıyor…", f"{len(self.hits)} sonuç karartıldı", redact_regions, regions)

    def replace_all(self):
        new, ok = QInputDialog.getText(self, "Bul ve değiştir", f"“{self.edit.text()}” yerine:")
        if not ok:
            return
        old = self.edit.text()
        targets = []
        for pg in sorted({h.page for h in self.hits}):
            for ln in editing.text_lines(self.tab.path, pg):
                if (old in ln["text"]) if self.case.isChecked() else (old.lower() in ln["text"].lower()):
                    targets.append((pg, ln))

        def op(src, out):
            import re
            import shutil
            shutil.copyfile(src, out)
            flags = 0 if self.case.isChecked() else re.I
            for pg, ln in targets:
                txt = re.sub(re.escape(old), new.replace("\\", "\\\\"), ln["text"], flags=flags)
                editing.edit_text_block(out, out, pg, ln["bounds"], txt, ln["size"], ln["baseline"], bold=ln["bold"])
            return out
        self.tab.apply(f"{len(targets)} satırda değiştirildi", op)


class CommentsPanel(QWidget):
    def __init__(self, tab):
        super().__init__()
        self.tab = tab
        v = QVBoxLayout(self)
        v.setContentsMargins(0, 0, 0, 0)
        v.addWidget(_hdr("Yorumlar", _tb("mdi6.export", "Yorumları metin olarak dışa aktar", self.export),
                         _tb("mdi6.layers-triple-outline", "Yorumları sayfaya göm (düzleştir)", self.flatten)))
        from PySide6.QtWidgets import QGridLayout
        f = QGridLayout()
        f.setContentsMargins(10, 0, 10, 4)
        f.setSpacing(4)
        self.f_author = QComboBox()
        self.f_kind = QComboBox()
        self.f_status = QComboBox()
        self.sort = QComboBox()
        for c in (self.f_author, self.f_kind, self.f_status):
            c.setMinimumWidth(60)
        self.sort.addItem("Sırala: Sayfa", "page")
        self.sort.addItem("Sırala: Yazar", "author")
        self.sort.addItem("Sırala: Tarih", "date")
        self.sort.addItem("Sırala: Tür", "kind")
        self.sort.setToolTip("Sıralama")
        for k, c in enumerate((self.f_author, self.f_kind, self.f_status, self.sort)):
            f.addWidget(c, k // 2, k % 2)
            c.currentIndexChanged.connect(self._fill)
        v.addLayout(f)
        self.text = QLineEdit()
        self.text.setPlaceholderText("Yorumlarda ara…")
        self.text.textChanged.connect(self._fill)
        tl = QHBoxLayout()
        tl.setContentsMargins(10, 0, 10, 4)
        tl.addWidget(self.text)
        v.addLayout(tl)
        self.list = QListWidget()
        self.list.setWordWrap(True)
        self.list.setStyleSheet("QListWidget { border: none; } QListWidget::item { border-bottom: 1px solid %s; padding: 8px; }" % theme.C["border"])
        self.list.itemClicked.connect(self._go)
        self.list.setContextMenuPolicy(Qt.CustomContextMenu)
        self.list.customContextMenuRequested.connect(self._menu)
        v.addWidget(self.list, 1)
        self.items = []

    def reload(self):
        try:
            self.items = ann.list_annotations(self.tab.path)
        except Exception:
            self.items = []
        for combo, key, allname, tr in ((self.f_author, "author", "Tüm yazarlar", None),
                                         (self.f_kind, "kind", "Tüm türler", ann.KINDS_TR),
                                         (self.f_status, "status", "Tüm durumlar", ann.STATES)):
            cur = combo.currentData()
            combo.blockSignals(True)
            combo.clear()
            combo.addItem(allname, None)
            vals = sorted({(d.get(key) or ("None" if key == "status" else "")) for d in self.items})
            for val in vals:
                if val == "" and key != "status":
                    continue
                combo.addItem(tr.get(val, val) if tr else val, val)
            i = combo.findData(cur)
            combo.setCurrentIndex(max(0, i))
            combo.blockSignals(False)
        self._fill()

    def _fill(self):
        self.list.clear()
        res = ann.filter_sort(self.items, self.f_author.currentData(), self.f_kind.currentData(),
                              self.f_status.currentData(), self.text.text(), self.sort.currentData() or "page")
        icons = {"Highlight": "mdi6.marker", "Underline": "mdi6.format-underline", "StrikeOut": "mdi6.format-strikethrough",
                 "Text": "mdi6.comment-text-outline", "FreeText": "mdi6.format-text", "Square": "mdi6.square-outline",
                 "Circle": "mdi6.circle-outline", "Ink": "mdi6.draw", "Line": "mdi6.arrow-top-right", "Stamp": "mdi6.stamper"}
        for d in res:
            head = f"{d['kind_tr']} · s.{d['page'] + 1} · {d['author']}  {d['date']}"
            if d.get("status_tr"):
                head += f"  [{d['status_tr']}]"
            body = d["contents"]
            for r in d["replies"]:
                body += f"\n   ↳ {r['author']}: {r['contents']}"
            it = QListWidgetItem(theme.icon(icons.get(d["kind"], "mdi6.comment-outline")), head + ("\n" + body if body else ""))
            it.setData(Qt.UserRole, d)
            self.list.addItem(it)
        if not res:
            it = QListWidgetItem("Yorum yok")
            it.setFlags(Qt.NoItemFlags)
            self.list.addItem(it)

    def _go(self, it):
        d = it.data(Qt.UserRole)
        if d:
            self.tab.view.go_to(d["page"], d["rect"][3])

    def _menu(self, pos):
        it = self.list.itemAt(pos)
        d = it.data(Qt.UserRole) if it else None
        if not d:
            return
        self.tab.annotation_menu(d, self.list.mapToGlobal(pos))

    def export(self):
        from .dialogs import TextDialog
        txt = ann.export_comments_text(self.tab.path)

        def save(t):
            p, _ = QFileDialog.getSaveFileName(self, "Yorumları kaydet", "yorumlar.txt", "Metin (*.txt)")
            if p:
                open(p, "w", encoding="utf-8").write(t)
                toast(self, "Yorumlar kaydedildi", "ok")
        TextDialog(self, "Yorum özeti", txt or "Yorum yok", actions=[("Kaydet…", "mdi6.content-save-outline", save)]).exec()

    def flatten(self):
        if QMessageBox.question(self, "Düzleştir", "Tüm yorumlar sayfa içeriğine kalıcı olarak gömülecek. Devam edilsin mi?") == QMessageBox.Yes:
            self.tab.apply("Yorumlar düzleştirildi", ann.flatten_annotations)


class AttachmentsPanel(QWidget):
    def __init__(self, tab):
        super().__init__()
        self.tab = tab
        v = QVBoxLayout(self)
        v.setContentsMargins(0, 0, 0, 0)
        v.addWidget(_hdr("Ekli dosyalar", _tb("mdi6.paperclip-plus", "Dosya ekle", self.add),
                         _tb("mdi6.download-outline", "Tümünü kaydet", self.save_all)))
        self.list = QListWidget()
        self.list.setStyleSheet("QListWidget { border: none; }")
        self.list.setContextMenuPolicy(Qt.CustomContextMenu)
        self.list.customContextMenuRequested.connect(self._menu)
        self.list.itemDoubleClicked.connect(lambda it: self._open(it.data(Qt.UserRole)))
        v.addWidget(self.list)

    def reload(self):
        self.list.clear()
        try:
            atts = editing.list_attachments(self.tab.path)
        except Exception:
            atts = []
        for a in atts:
            it = QListWidgetItem(theme.icon("mdi6.file-outline"), f"{a['name']}\n{a['size'] / 1024:.1f} KB")
            it.setData(Qt.UserRole, a["name"])
            self.list.addItem(it)
        if not atts:
            it = QListWidgetItem("Ekli dosya yok")
            it.setFlags(Qt.NoItemFlags)
            self.list.addItem(it)

    def add(self):
        fs, _ = QFileDialog.getOpenFileNames(self, "Eklenecek dosyalar")
        for f in fs:
            self.tab.apply(f"{os.path.basename(f)} eklendi", editing.add_attachment, f)

    def save_all(self):
        d = QFileDialog.getExistingDirectory(self, "Kaydedilecek klasör")
        if d:
            outs = editing.extract_attachments(self.tab.path, d)
            toast(self, f"{len(outs)} dosya kaydedildi", "ok")

    def _open(self, name):
        if not name:
            return
        import tempfile
        d = tempfile.mkdtemp()
        for p in editing.extract_attachments(self.tab.path, d):
            if os.path.basename(p) == os.path.basename(name):
                open_path(p)

    def _menu(self, pos):
        it = self.list.itemAt(pos)
        name = it.data(Qt.UserRole) if it else None
        if not name:
            return
        m = QMenu(self)
        m.addAction("Aç", lambda: self._open(name))

        def save():
            p, _ = QFileDialog.getSaveFileName(self, "Kaydet", name)
            if p:
                import tempfile, shutil
                d = tempfile.mkdtemp()
                for x in editing.extract_attachments(self.tab.path, d):
                    if os.path.basename(x) == os.path.basename(name):
                        shutil.copyfile(x, p)
        m.addAction("Farklı kaydet…", save)

        def ren():
            t, ok = QInputDialog.getText(self, "Yeniden adlandır", "Yeni ad:", text=name)
            if ok and t:
                self.tab.apply("Ek yeniden adlandırıldı", editing.rename_attachment, name, t)
        m.addAction("Yeniden adlandır", ren)
        m.addAction("Sil", lambda: self.tab.apply("Ek silindi", editing.remove_attachment, name))
        m.exec(self.list.mapToGlobal(pos))


class SignaturesPanel(QWidget):
    def __init__(self, tab):
        super().__init__()
        self.tab = tab
        v = QVBoxLayout(self)
        v.setContentsMargins(0, 0, 0, 0)
        v.addWidget(_hdr("Dijital imzalar", _tb("mdi6.refresh", "Yeniden doğrula", self.reload),
                         _tb("mdi6.draw-pen", "İmzala", lambda: tab.set_tool("sign"))))
        self.list = QListWidget()
        self.list.setWordWrap(True)
        self.list.setStyleSheet("QListWidget { border: none; } QListWidget::item { padding: 8px; }")
        v.addWidget(self.list)

    def reload(self):
        self.list.clear()
        try:
            res = signmod.verify(self.tab.path)
        except Exception as e:
            res = []
        for r in res:
            ok = r["intact"]
            ic = "mdi6.check-decagram" if (ok and r["trusted"]) else ("mdi6.shield-check-outline" if ok else "mdi6.alert-decagram")
            col = theme.C["success"] if ok else theme.C["danger"]
            txt = (f"{r['signer']}\n{r['signing_time']}\n{r['summary']}\nAlan: {r['field']} · {r['md_algorithm'].upper()}"
                   + (" · zaman damgalı" if r["timestamp"] else ""))
            it = QListWidgetItem(theme.icon(ic, col), txt)
            self.list.addItem(it)
        if not res:
            it = QListWidgetItem("Bu belgede dijital imza yok.")
            it.setFlags(Qt.NoItemFlags)
            self.list.addItem(it)
        return res


class FieldsPanel(QWidget):
    def __init__(self, tab):
        super().__init__()
        self.tab = tab
        v = QVBoxLayout(self)
        v.setContentsMargins(0, 0, 0, 0)
        v.addWidget(_hdr("Form alanları", _tb("mdi6.form-textbox", "Formu doldur", tab.toggle_form_fill),
                         _tb("mdi6.import", "Verileri içe aktar (FDF/JSON)", tab.op_import_form),
                         _tb("mdi6.export", "Verileri dışa aktar", tab.op_export_form)))
        self.list = QListWidget()
        self.list.setStyleSheet("QListWidget { border: none; }")
        self.list.itemClicked.connect(self._go)
        self.list.setContextMenuPolicy(Qt.CustomContextMenu)
        self.list.customContextMenuRequested.connect(self._menu)
        v.addWidget(self.list)
        self.fields = []

    def reload(self):
        self.list.clear()
        try:
            self.fields = forms.list_fields(self.tab.path)
        except Exception:
            self.fields = []
        icons = {"text": "mdi6.form-textbox", "checkbox": "mdi6.checkbox-marked-outline", "radio": "mdi6.radiobox-marked",
                 "combo": "mdi6.form-dropdown", "list": "mdi6.format-list-bulleted", "button": "mdi6.gesture-tap-button",
                 "signature": "mdi6.draw-pen"}
        for f in self.fields:
            val = f["value"] if not isinstance(f["value"], list) else ", ".join(f["value"])
            it = QListWidgetItem(theme.icon(icons.get(f["type"], "mdi6.form-select")),
                                 f"{f['name']}{' *' if f['required'] else ''}\n{f['type_tr']} · s.{(f['page'] or 0) + 1}" + (f" · {val}" if val else ""))
            it.setData(Qt.UserRole, f)
            self.list.addItem(it)
        if not self.fields:
            it = QListWidgetItem("Form alanı yok. 'Formlar' sekmesinden alan ekleyebilirsiniz.")
            it.setFlags(Qt.NoItemFlags)
            self.list.addItem(it)
        return self.fields

    def _go(self, it):
        f = it.data(Qt.UserRole)
        if f:
            self.tab.view.go_to(f["page"] or 0, f["rect"][3])

    def _menu(self, pos):
        it = self.list.itemAt(pos)
        f = it.data(Qt.UserRole) if it else None
        if not f:
            return
        m = QMenu(self)
        m.addAction(theme.icon("mdi6.delete-outline"), "Alanı sil", lambda: self.tab.apply("Alan silindi", forms.remove_field, f["name"]))
        m.exec(self.list.mapToGlobal(pos))
