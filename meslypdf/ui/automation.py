"""Otomasyon ekranı: Hot Folder işleri ve özel otomatik görevler."""
from __future__ import annotations

import copy
import os

from PySide6.QtCore import Qt, QTimer, Signal, QObject
from PySide6.QtWidgets import (QWidget, QVBoxLayout, QHBoxLayout, QTabWidget, QTableWidget, QTableWidgetItem, QHeaderView,
                               QAbstractItemView, QPlainTextEdit, QListWidget, QListWidgetItem, QLineEdit, QMenu, QToolButton,
                               QSplitter, QMessageBox, QLabel, QFormLayout, QComboBox, QFrame)

from ..core import hotfolder as hf, tasks as tk
from . import theme
from .common import run_task, toast, Settings, button, label, open_files, pick_dir, confirm, ALL_SUPPORTED, open_path
from .dialogs import FormDialog

SCHEDULES = [("watch", "Sürekli izle (yeni dosya gelince)"), ("interval", "Belirli aralıklarla"), ("daily", "Her gün belirli saatte"),
             ("once", "Bir kez, belirli zamanda")]
SOURCES = [("folder", "Klasör (yerel / ağ)"), ("email", "E-posta (IMAP)"), ("ftp", "FTP sunucusu")]
AFTER = [("move", "'İşlenenler' klasörüne taşı"), ("keep", "Yerinde bırak"), ("delete", "Sil")]


class _Bridge(QObject):
    msg = Signal(str)


class AutomationView(QWidget):
    titleChanged = Signal()
    title = "Otomasyon"

    def __init__(self, main):
        super().__init__()
        self.main = main
        self.bridge = _Bridge()
        self.bridge.msg.connect(self._log)
        self.service = main.hotfolder_service
        self.service.on_event = self.bridge.msg.emit
        v = QVBoxLayout(self)
        v.setContentsMargins(24, 18, 24, 18)
        v.addWidget(label("Otomasyon", "H1"))
        v.addWidget(label("Tekrarlayan belge işlerini otomatikleştirin: klasörleri izleyin, zamanlayın veya kendi iş akışlarınızı oluşturun.", "Muted"))
        tabs = QTabWidget()
        tabs.addTab(self._hot_tab(), theme.icon("mdi6.folder-sync-outline"), "Hot Folder")
        tabs.addTab(self._tasks_tab(), theme.icon("mdi6.playlist-play"), "Otomatik görevler")
        v.addWidget(tabs, 1)
        self.reload_jobs()
        self._update_status()
        self._timer = QTimer(self, interval=3000, timeout=self.reload_jobs)
        self._timer.start()

    def close_tab(self):
        return True

    # ------------------------------------------------------------ Hot Folder
    def _hot_tab(self):
        w = QWidget()
        v = QVBoxLayout(w)
        v.setContentsMargins(14, 14, 14, 14)
        top = QHBoxLayout()
        self.status_lbl = QLabel()
        top.addWidget(self.status_lbl)
        top.addStretch()
        self.btn_service = button("Hizmeti başlat", "mdi6.play", obj="Primary", slot=self.toggle_service)
        top.addWidget(self.btn_service)
        v.addLayout(top)
        hb = QHBoxLayout()
        hb.addWidget(button("Yeni iş", "mdi6.plus", slot=self.new_job))
        hb.addWidget(button("Düzenle", "mdi6.pencil-outline", slot=self.edit_job))
        hb.addWidget(button("Şimdi çalıştır", "mdi6.play-circle-outline", slot=self.run_job_now))
        hb.addWidget(button("Sil", "mdi6.delete-outline", slot=self.delete_job))
        hb.addStretch()
        hb.addWidget(button("Çıktı klasörünü aç", "mdi6.folder-open-outline", slot=self.open_output))
        v.addLayout(hb)
        self.table = QTableWidget(0, 6)
        self.table.setHorizontalHeaderLabels(["Etkin", "Ad", "Kaynak", "Zamanlama", "Son çalışma", "Sonuç"])
        self.table.horizontalHeader().setSectionResizeMode(2, QHeaderView.Stretch)
        self.table.horizontalHeader().setSectionResizeMode(1, QHeaderView.ResizeToContents)
        self.table.verticalHeader().setVisible(False)
        self.table.setSelectionBehavior(QAbstractItemView.SelectRows)
        self.table.setEditTriggers(QAbstractItemView.NoEditTriggers)
        self.table.setAlternatingRowColors(True)
        self.table.doubleClicked.connect(lambda *_: self.edit_job())
        self.table.itemChanged.connect(self._enabled_changed)
        v.addWidget(self.table, 2)
        v.addWidget(label("Günlük", "H3"))
        self.log = QPlainTextEdit()
        self.log.setReadOnly(True)
        self.log.setMaximumBlockCount(2000)
        try:
            with open(hf.log_file(), encoding="utf-8") as f:
                self.log.setPlainText("".join(f.readlines()[-200:]))
        except Exception:
            pass
        v.addWidget(self.log, 1)
        return w

    def _log(self, msg):
        self.log.appendPlainText(msg)
        self.reload_jobs()

    def _update_status(self):
        on = self.service.running
        col = theme.C["success"] if on else theme.C["text3"]
        self.status_lbl.setText(f"<span style='color:{col}; font-size:16px'>●</span>  Hot Folder hizmeti: <b>{'çalışıyor' if on else 'durduruldu'}</b>")
        self.btn_service.setText("Hizmeti durdur" if on else "Hizmeti başlat")
        self.btn_service.setIcon(theme.icon("mdi6.stop" if on else "mdi6.play", "#FFFFFF"))
        self.main.update_tray()

    def toggle_service(self):
        if self.service.running:
            self.service.stop()
        else:
            self.service.start()
        self._update_status()

    def reload_jobs(self):
        jobs = hf.load_jobs()
        sel = self.table.currentRow()
        self.table.blockSignals(True)
        self.table.setRowCount(len(jobs))
        sch = dict(SCHEDULES)
        for r, j in enumerate(jobs):
            en = QTableWidgetItem()
            en.setFlags(Qt.ItemIsUserCheckable | Qt.ItemIsEnabled | Qt.ItemIsSelectable)
            en.setCheckState(Qt.Checked if j.enabled else Qt.Unchecked)
            self.table.setItem(r, 0, en)
            src = j.source if j.source_type == "folder" else f"{j.source_type.upper()}: {j.username}@{j.source}"
            s = sch[j.schedule] if j.schedule == "watch" else (f"{j.interval_min} dakikada bir" if j.schedule == "interval" else
                                                                (f"Her gün {j.time_of_day}" if j.schedule == "daily" else f"{j.run_at}"))
            for c, val in enumerate([j.name, src, s, j.last_run, j.last_result], 1):
                self.table.setItem(r, c, QTableWidgetItem(val))
        self.table.blockSignals(False)
        if 0 <= sel < len(jobs):
            self.table.selectRow(sel)
        if hasattr(self, "btn_service"):
            on = self.service.running
            if (self.btn_service.text() == "Hizmeti durdur") != on:
                self._update_status()

    def _enabled_changed(self, item):
        if item.column() != 0:
            return
        jobs = hf.load_jobs()
        jobs[item.row()].enabled = item.checkState() == Qt.Checked
        hf.save_jobs(jobs)
        self.service.reload()

    def _job_dialog(self, job: hf.HotFolderJob | None):
        job = job or hf.HotFolderJob(name=f"İş {len(hf.load_jobs()) + 1}")
        tasks = tk.load_tasks()
        task_opts = [(t.name, t.name) for t in tasks]
        cur_task = job.task.get("name") if job.task else (tasks[0].name if tasks else "")
        fields = [
            ("name", "İş adı", "text", job.name),
            ("source_type", "Kaynak türü", "combo", job.source_type, SOURCES),
            ("source", "Klasör / sunucu", "text", job.source, "C:\\Taramalar  veya  imap.gmail.com"),
            ("browse", "", "info", "Klasör için yol yazın veya aşağıdaki 'Klasör seç' alanını kullanın."),
            ("folder_pick", "Klasör seç", "dir", job.source if job.source_type == "folder" else ""),
            ("username", "Kullanıcı adı", "text", job.username), ("password", "Parola", "password", job.password),
            ("port", "Port (0=varsayılan)", "int", job.port, (0, 65535)), ("remote_dir", "Posta kutusu / FTP klasörü", "text", job.remote_dir),
            ("patterns", "Dosya desenleri", "text", " ".join(job.patterns)), ("recursive", "Alt klasörleri de tara", "check", job.recursive),
            ("schedule", "Zamanlama", "combo", job.schedule, SCHEDULES), ("interval_min", "Aralık (dakika)", "int", job.interval_min, (1, 10080)),
            ("time_of_day", "Saat (SS:DD)", "text", job.time_of_day), ("run_at", "Tarih-saat (YYYY-AA-GG SS:DD)", "text", job.run_at),
            ("task", "Uygulanacak görev", "combo", cur_task, task_opts),
            ("combine", "Gelen tüm dosyaları tek belgede birleştir", "check", job.combine),
            ("output_dir", "Çıktı klasörü", "dir", job.output_dir), ("after", "İşlenen dosyalar", "combo", job.after, AFTER)]
        d = FormDialog(self, "Hot Folder işi", fields, ok_text="Kaydet", width=560)
        if not d.exec():
            return None
        v = d.values()
        if v["folder_pick"] and v["source_type"] == "folder":
            v["source"] = v["folder_pick"]
        if not v["name"] or not v["source"]:
            QMessageBox.warning(self, "Eksik bilgi", "İş adı ve kaynak zorunludur.")
            return None
        t = next((t for t in tasks if t.name == v["task"]), tasks[0])
        job.name, job.source_type, job.source = v["name"], v["source_type"], v["source"]
        job.username, job.password, job.port, job.remote_dir = v["username"], v["password"], v["port"], v["remote_dir"] or "/"
        job.patterns = v["patterns"].split() or list(hf.SUPPORTED)
        job.recursive, job.schedule, job.interval_min = v["recursive"], v["schedule"], v["interval_min"]
        job.time_of_day, job.run_at = v["time_of_day"], v["run_at"]
        job.task = t.to_json()
        job.combine, job.output_dir, job.after = v["combine"], v["output_dir"], v["after"]
        return job

    def new_job(self):
        j = self._job_dialog(None)
        if j:
            jobs = hf.load_jobs()
            names = {x.name for x in jobs}
            while j.name in names:
                j.name += " (2)"
            jobs.append(j)
            hf.save_jobs(jobs)
            self.service.reload()
            self.reload_jobs()
            if not self.service.running and confirm(self, "Hot Folder", "İş kaydedildi. Hot Folder hizmeti şimdi başlatılsın mı?"):
                self.toggle_service()

    def _selected(self):
        r = self.table.currentRow()
        jobs = hf.load_jobs()
        return (r, jobs) if 0 <= r < len(jobs) else (None, jobs)

    def edit_job(self):
        r, jobs = self._selected()
        if r is None:
            return
        j = self._job_dialog(copy.deepcopy(jobs[r]))
        if j:
            jobs[r] = j
            hf.save_jobs(jobs)
            self.service.reload()
            self.reload_jobs()

    def delete_job(self):
        r, jobs = self._selected()
        if r is not None and confirm(self, "Sil", f"“{jobs[r].name}” işi silinsin mi?"):
            del jobs[r]
            hf.save_jobs(jobs)
            self.service.reload()
            self.reload_jobs()

    def run_job_now(self):
        r, jobs = self._selected()
        if r is None:
            return
        name = jobs[r].name
        run_task(self, f"“{name}” çalıştırılıyor…", self.service.run_now, name,
                 on_done=lambda _: (self.reload_jobs(), toast(self, f"“{name}” tamamlandı", "ok")))

    def open_output(self):
        r, jobs = self._selected()
        if r is not None:
            j = jobs[r]
            d = j.output_dir or os.path.join(j.source, "Çıktı")
            if os.path.isdir(d):
                open_path(d)

    # ------------------------------------------------------------ görevler
    def _tasks_tab(self):
        w = QWidget()
        h = QHBoxLayout(w)
        h.setContentsMargins(14, 14, 14, 14)
        left = QVBoxLayout()
        left.addWidget(label("Görevler", "H3"))
        self.task_list = QListWidget()
        self.task_list.currentRowChanged.connect(self._show_task)
        left.addWidget(self.task_list, 1)
        lb = QHBoxLayout()
        lb.addWidget(button("Yeni", "mdi6.plus", slot=self.new_task))
        lb.addWidget(button("Çoğalt", "mdi6.content-copy", slot=self.dup_task))
        lb.addWidget(button("Sil", "mdi6.delete-outline", slot=self.del_task))
        left.addLayout(lb)
        h.addLayout(left, 1)
        right = QFrame()
        right.setObjectName("Card")
        rv = QVBoxLayout(right)
        rv.setContentsMargins(16, 14, 16, 14)
        form = QFormLayout()
        self.t_name = QLineEdit()
        self.t_desc = QLineEdit()
        self.t_out = QLineEdit()
        self.t_out.setPlaceholderText("Boş = girdi dosyasının klasörü")
        self.t_naming = QLineEdit()
        self.t_naming.setToolTip("{ad} {tarih} {saat} {sayac}")
        form.addRow("Ad", self.t_name)
        form.addRow("Açıklama", self.t_desc)
        form.addRow("Çıktı klasörü", self.t_out)
        form.addRow("Dosya adı şablonu", self.t_naming)
        rv.addLayout(form)
        rv.addWidget(label("Adımlar (sırayla uygulanır)", "H3"))
        self.steps = QListWidget()
        self.steps.itemDoubleClicked.connect(lambda *_: self.edit_step())
        rv.addWidget(self.steps, 1)
        sb = QHBoxLayout()
        add = QToolButton()
        add.setText("Adım ekle")
        add.setIcon(theme.icon("mdi6.plus"))
        add.setToolButtonStyle(Qt.ToolButtonTextBesideIcon)
        add.setPopupMode(QToolButton.InstantPopup)
        m = QMenu(add)
        for k, (t, _) in tk.STEP_TYPES.items():
            m.addAction(t, lambda k=k: self.add_step(k))
        add.setMenu(m)
        sb.addWidget(add)
        sb.addWidget(button("Ayarlar", "mdi6.tune", slot=self.edit_step))
        sb.addWidget(button("", "mdi6.arrow-up", slot=lambda: self.move_step(-1)))
        sb.addWidget(button("", "mdi6.arrow-down", slot=lambda: self.move_step(1)))
        sb.addWidget(button("Kaldır", "mdi6.close", slot=self.remove_step))
        sb.addStretch()
        rv.addLayout(sb)
        bb = QHBoxLayout()
        bb.addStretch()
        bb.addWidget(button("Kaydet", "mdi6.content-save-outline", slot=self.save_task))
        bb.addWidget(button("Dosyalarda çalıştır…", "mdi6.play", obj="Primary", slot=self.run_task_on_files))
        rv.addLayout(bb)
        h.addWidget(right, 2)
        self.tasks = tk.load_tasks()
        self._fill_tasks()
        return w

    def _fill_tasks(self, select=0):
        self.task_list.clear()
        for t in self.tasks:
            self.task_list.addItem(QListWidgetItem(theme.icon("mdi6.playlist-play"), t.name))
        if self.tasks:
            self.task_list.setCurrentRow(min(select, len(self.tasks) - 1))

    def _cur_task(self):
        r = self.task_list.currentRow()
        return self.tasks[r] if 0 <= r < len(self.tasks) else None

    def _show_task(self, r):
        t = self._cur_task()
        if not t:
            return
        self.t_name.setText(t.name)
        self.t_desc.setText(t.description)
        self.t_out.setText(t.output_dir)
        self.t_naming.setText(t.naming)
        self._fill_steps(t)

    def _fill_steps(self, t):
        self.steps.clear()
        for k, s in enumerate(t.steps, 1):
            label_ = tk.STEP_TYPES.get(s["type"], (s["type"],))[0]
            prm = ", ".join(f"{a}={b}" for a, b in s.get("params", {}).items() if b not in ("", [], None) and a not in ("password", "user_pw", "owner_pw"))
            self.steps.addItem(f"{k}. {label_}" + (f"   ({prm})" if prm else ""))

    def add_step(self, kind):
        t = self._cur_task()
        if t:
            t.steps.append({"type": kind, "params": copy.deepcopy(tk.STEP_TYPES[kind][1])})
            self._fill_steps(t)
            self.steps.setCurrentRow(len(t.steps) - 1)

    def edit_step(self):
        t = self._cur_task()
        r = self.steps.currentRow()
        if not t or r < 0:
            return
        s = t.steps[r]
        fields = []
        for k, v in s.get("params", {}).items():
            if k == "lang":
                fields.append((k, "Dil", "lang", v))
            elif k == "level":
                fields.append((k, "Seviye", "combo", v, [("low", "Düşük"), ("medium", "Orta"), ("high", "Yüksek")]))
            elif k == "format":
                from ..core.convert import EXPORT_FORMATS
                fields.append((k, "Biçim", "combo", v, list(EXPORT_FORMATS.items())))
            elif k == "method":
                fields.append((k, "Şifreleme", "combo", v, [(x, x) for x in ("AES-256", "AES-128", "RC4-128")]))
            elif k == "p12":
                fields.append((k, "Sertifika", "file", v, "PKCS#12 (*.pfx *.p12)"))
            elif "pw" in k or k == "password":
                fields.append((k, k, "password", v))
            elif isinstance(v, bool):
                fields.append((k, k, "check", v))
            elif isinstance(v, int):
                fields.append((k, k, "int", v, (0, 10 ** 9)))
            elif isinstance(v, float):
                fields.append((k, k, "float", v, (0, 1, 0.05)))
            elif isinstance(v, list):
                fields.append((k, k + " (virgülle)", "text", ", ".join(v)))
            else:
                fields.append((k, k, "text", v))
        if not fields:
            toast(self, "Bu adımın ayarı yok", "info")
            return
        vals = FormDialog.ask(self, tk.STEP_TYPES[s["type"]][0], fields, ok_text="Tamam")
        if vals:
            for k, v in vals.items():
                if isinstance(s["params"].get(k), list):
                    v = [x.strip() for x in v.split(",") if x.strip()]
                s["params"][k] = v
            self._fill_steps(t)

    def move_step(self, d):
        t = self._cur_task()
        r = self.steps.currentRow()
        if t and 0 <= r and 0 <= r + d < len(t.steps):
            t.steps[r], t.steps[r + d] = t.steps[r + d], t.steps[r]
            self._fill_steps(t)
            self.steps.setCurrentRow(r + d)

    def remove_step(self):
        t = self._cur_task()
        r = self.steps.currentRow()
        if t and r >= 0:
            del t.steps[r]
            self._fill_steps(t)

    def save_task(self):
        t = self._cur_task()
        if not t:
            return
        t.name, t.description = self.t_name.text().strip() or t.name, self.t_desc.text()
        t.output_dir, t.naming = self.t_out.text().strip(), self.t_naming.text().strip() or "{ad}"
        tk.save_tasks(self.tasks)
        self._fill_tasks(self.task_list.currentRow())
        toast(self, "Görev kaydedildi", "ok")

    def new_task(self):
        self.tasks.append(tk.Task(f"Yeni görev {len(self.tasks) + 1}", [{"type": "ocr", "params": {"lang": Settings.get("ocr_lang")}},
                                                                        {"type": "save", "params": {}}]))
        tk.save_tasks(self.tasks)
        self._fill_tasks(len(self.tasks) - 1)

    def dup_task(self):
        t = self._cur_task()
        if t:
            n = copy.deepcopy(t)
            n.name += " (kopya)"
            self.tasks.append(n)
            tk.save_tasks(self.tasks)
            self._fill_tasks(len(self.tasks) - 1)

    def del_task(self):
        t = self._cur_task()
        if t and confirm(self, "Sil", f"“{t.name}” silinsin mi?"):
            self.tasks.remove(t)
            tk.save_tasks(self.tasks)
            self._fill_tasks()

    def run_task_on_files(self):
        self.save_task()
        t = self._cur_task()
        fs = open_files(self, "Görevin uygulanacağı dosyalar", ALL_SUPPORTED)
        if not fs:
            return
        out = t.output_dir or os.path.dirname(fs[0])

        def done(outs):
            toast(self, f"{len(outs)} çıktı oluşturuldu", "ok")
            if outs and confirm(self, "Görev tamamlandı", f"{len(outs)} dosya oluşturuldu:\n" + "\n".join(os.path.basename(o) for o in outs[:8]) + "\n\nKlasör açılsın mı?"):
                open_path(os.path.dirname(outs[0]))
        run_task(self, f"“{t.name}” çalıştırılıyor…", tk.run_task, t, fs, out, lambda m: None, on_done=done)
