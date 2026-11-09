"""Hot Folder: izlenen klasör / e-posta (IMAP) / FTP kaynaklarından gelen belgeleri
zamanlamaya göre otomatik işler. Görev motorunu (tasks.py) kullanır."""
from __future__ import annotations

import datetime as _dt
import fnmatch
import ftplib
import imaplib
import email
import json
import os
import shutil
import tempfile
import threading
import time
from dataclasses import dataclass, field, asdict

from .paths import app_data_dir
from .tasks import Task, run_task

SUPPORTED = ["*.pdf", "*.png", "*.jpg", "*.jpeg", "*.tif", "*.tiff", "*.bmp", "*.docx", "*.doc", "*.xlsx",
             "*.xls", "*.pptx", "*.odt", "*.rtf", "*.txt", "*.html"]


@dataclass
class HotFolderJob:
    name: str
    source_type: str = "folder"          # folder, email, ftp
    source: str = ""                     # klasör yolu / IMAP sunucusu / FTP sunucusu
    username: str = ""
    password: str = ""
    port: int = 0
    remote_dir: str = "/"                # FTP klasörü veya IMAP posta kutusu
    patterns: list = field(default_factory=lambda: list(SUPPORTED))
    recursive: bool = False
    schedule: str = "watch"              # watch (sürekli izle), interval, daily, once
    interval_min: int = 10
    time_of_day: str = "02:00"
    run_at: str = ""                     # once için: 'YYYY-MM-DD HH:MM'
    task: dict = field(default_factory=dict)
    output_dir: str = ""
    after: str = "move"                  # keep, move, delete
    processed_dir: str = ""
    combine: bool = False                # tüm gelen dosyaları tek belgeye birleştir
    enabled: bool = True
    last_run: str = ""
    last_result: str = ""

    def task_obj(self) -> Task:
        return Task.from_json(self.task) if self.task else Task(self.name, [{"type": "ocr", "params": {"lang": "tur+eng"}},
                                                                            {"type": "save", "params": {}}])


def jobs_file() -> str:
    return os.path.join(app_data_dir(), "hotfolder.json")


def log_file() -> str:
    return os.path.join(app_data_dir(), "hotfolder.log")


def load_jobs() -> list[HotFolderJob]:
    p = jobs_file()
    if not os.path.exists(p):
        return []
    try:
        return [HotFolderJob(**{k: v for k, v in d.items() if k in HotFolderJob.__dataclass_fields__})
                for d in json.load(open(p, encoding="utf-8"))]
    except Exception:
        return []


def save_jobs(jobs: list[HotFolderJob]):
    with open(jobs_file(), "w", encoding="utf-8") as f:
        json.dump([asdict(j) for j in jobs], f, ensure_ascii=False, indent=2)


def write_log(msg: str):
    line = f"{_dt.datetime.now():%Y-%m-%d %H:%M:%S}  {msg}"
    try:
        with open(log_file(), "a", encoding="utf-8") as f:
            f.write(line + "\n")
    except Exception:
        pass
    return line


def _matches(name: str, patterns: list[str]) -> bool:
    return any(fnmatch.fnmatch(name.lower(), p.lower()) for p in patterns)


def _stable(path: str, wait: float = 1.0) -> bool:
    try:
        s1 = os.path.getsize(path)
        time.sleep(wait)
        return s1 == os.path.getsize(path) and s1 > 0
    except OSError:
        return False


def collect_folder(job: HotFolderJob) -> list[str]:
    if not os.path.isdir(job.source):
        raise FileNotFoundError(f"Klasör bulunamadı: {job.source}")
    files = []
    skip = {os.path.abspath(p) for p in (job.output_dir, job.processed_dir,
                                         os.path.join(job.source, "İşlenenler"), os.path.join(job.source, "Çıktı")) if p}
    for root, dirs, names in os.walk(job.source):
        dirs[:] = [d for d in dirs if os.path.abspath(os.path.join(root, d)) not in skip]
        for n in sorted(names):
            if _matches(n, job.patterns) and not n.startswith("~$"):
                files.append(os.path.join(root, n))
        if not job.recursive:
            break
    return [f for f in files if _stable(f, 0.3)]


def collect_email(job: HotFolderJob, dest: str) -> list[str]:
    """IMAP'ten okunmamış e-postaların eklerini indirir ve okundu işaretler."""
    port = job.port or 993
    M = imaplib.IMAP4_SSL(job.source, port)
    M.login(job.username, job.password)
    M.select(job.remote_dir if job.remote_dir not in ("", "/") else "INBOX")
    typ, data = M.search(None, "UNSEEN")
    files = []
    for num in data[0].split():
        typ, msgdata = M.fetch(num, "(RFC822)")
        msg = email.message_from_bytes(msgdata[0][1])
        for part in msg.walk():
            fn = part.get_filename()
            if not fn:
                continue
            fn = str(email.header.make_header(email.header.decode_header(fn)))
            if not _matches(fn, job.patterns):
                continue
            p = os.path.join(dest, fn)
            with open(p, "wb") as f:
                f.write(part.get_payload(decode=True))
            files.append(p)
        M.store(num, "+FLAGS", "\\Seen")
    M.logout()
    return files


def collect_ftp(job: HotFolderJob, dest: str) -> list[str]:
    ftp = ftplib.FTP()
    ftp.connect(job.source, job.port or 21, timeout=30)
    ftp.login(job.username or "anonymous", job.password or "")
    ftp.cwd(job.remote_dir or "/")
    files = []
    for name in ftp.nlst():
        if not _matches(name, job.patterns):
            continue
        p = os.path.join(dest, os.path.basename(name))
        with open(p, "wb") as f:
            ftp.retrbinary(f"RETR {name}", f.write)
        files.append(p)
        if job.after == "delete":
            try:
                ftp.delete(name)
            except ftplib.error_perm:
                pass
    ftp.quit()
    return files


def run_job(job: HotFolderJob, log=write_log) -> list[str]:
    """İşi bir kez çalıştırır: kaynaktaki dosyaları toplar, görevi uygular, kaynağı temizler."""
    tmp = tempfile.mkdtemp(prefix="mesly_hf_")
    try:
        if job.source_type == "folder":
            files = collect_folder(job)
        elif job.source_type == "email":
            files = collect_email(job, tmp)
        elif job.source_type == "ftp":
            files = collect_ftp(job, tmp)
        else:
            raise ValueError(job.source_type)
        if not files:
            job.last_run = _dt.datetime.now().strftime("%d.%m.%Y %H:%M")
            job.last_result = "İşlenecek dosya yok"
            return []
        out_dir = job.output_dir or os.path.join(job.source if job.source_type == "folder" else app_data_dir(), "Çıktı")
        task = job.task_obj()
        log(f"[{job.name}] {len(files)} dosya işleniyor")
        produced = []
        groups = [files] if job.combine else [[f] for f in files]
        if job.combine and not any(s["type"] == "merge" for s in task.steps):
            task.steps = [{"type": "merge", "params": {}}] + task.steps
        errors = 0
        for g in groups:
            try:
                produced += run_task(task, g, out_dir, log=lambda m: log(f"[{job.name}] {m}"))
                if job.source_type == "folder":
                    for f in g:
                        if job.after == "delete":
                            os.remove(f)
                        elif job.after == "move":
                            pd = job.processed_dir or os.path.join(job.source, "İşlenenler")
                            os.makedirs(pd, exist_ok=True)
                            shutil.move(f, _unique(os.path.join(pd, os.path.basename(f))))
            except Exception as e:
                errors += 1
                log(f"[{job.name}] HATA ({', '.join(os.path.basename(x) for x in g)}): {e}")
        job.last_run = _dt.datetime.now().strftime("%d.%m.%Y %H:%M")
        job.last_result = f"{len(produced)} çıktı" + (f", {errors} hata" if errors else "")
        log(f"[{job.name}] Bitti: {job.last_result}")
        return produced
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


def _unique(path):
    if not os.path.exists(path):
        return path
    b, e = os.path.splitext(path)
    k = 1
    while os.path.exists(f"{b} ({k}){e}"):
        k += 1
    return f"{b} ({k}){e}"


class HotFolderService:
    """Arka planda tüm etkin işleri zamanlar. Uygulama içinde veya `meslypdf hotfolder` ile çalışır."""

    def __init__(self, on_event=None):
        self.on_event = on_event or (lambda msg: None)
        self._stop = threading.Event()
        self._thread: threading.Thread | None = None
        self._lock = threading.Lock()
        self._observer = None
        self._dirty: set[str] = set()
        self._last: dict[str, float] = {}

    def log(self, msg):
        line = write_log(msg)
        self.on_event(line)

    def start(self):
        if self._thread and self._thread.is_alive():
            return
        self._stop.clear()
        self._setup_watchers()
        self._thread = threading.Thread(target=self._loop, daemon=True, name="HotFolder")
        self._thread.start()
        self.log("Hot Folder hizmeti başlatıldı")

    def stop(self):
        self._stop.set()
        if self._observer:
            try:
                self._observer.stop()
                self._observer.join(timeout=3)
            except Exception:
                pass
            self._observer = None
        self.log("Hot Folder hizmeti durduruldu")

    @property
    def running(self):
        return bool(self._thread and self._thread.is_alive())

    def reload(self):
        if self.running:
            if self._observer:
                self._observer.stop()
            self._setup_watchers()

    def _setup_watchers(self):
        try:
            from watchdog.observers import Observer
            from watchdog.events import FileSystemEventHandler
        except Exception:
            return
        svc = self

        class H(FileSystemEventHandler):
            def __init__(self, name):
                self.name = name

            def on_created(self, e):
                if not e.is_directory:
                    svc._dirty.add(self.name)

            on_moved = on_created
            on_modified = on_created

        self._observer = Observer()
        for j in load_jobs():
            if j.enabled and j.source_type == "folder" and j.schedule == "watch" and os.path.isdir(j.source):
                self._observer.schedule(H(j.name), j.source, recursive=j.recursive)
                self._dirty.add(j.name)  # başlangıçta mevcut dosyaları da işle
        self._observer.start()

    def _due(self, j: HotFolderJob, now: _dt.datetime) -> bool:
        last = self._last.get(j.name, 0)
        if j.schedule == "watch":
            if j.source_type != "folder":
                return time.time() - last > max(1, j.interval_min) * 60
            return j.name in self._dirty
        if j.schedule == "interval":
            return time.time() - last >= max(1, j.interval_min) * 60
        if j.schedule == "daily":
            hh, mm = (int(x) for x in j.time_of_day.split(":"))
            target = now.replace(hour=hh, minute=mm, second=0, microsecond=0)
            return now >= target and last < target.timestamp()
        if j.schedule == "once":
            try:
                target = _dt.datetime.strptime(j.run_at, "%Y-%m-%d %H:%M")
            except ValueError:
                return False
            return now >= target and not j.last_run
        return False

    def run_now(self, name: str):
        jobs = load_jobs()
        for j in jobs:
            if j.name == name:
                with self._lock:
                    run_job(j, self.log)
                    self._last[j.name] = time.time()
                save_jobs(jobs)
                return

    def _loop(self):
        while not self._stop.is_set():
            now = _dt.datetime.now()
            jobs = load_jobs()
            changed = False
            for j in jobs:
                if not j.enabled:
                    continue
                try:
                    if self._due(j, now):
                        self._dirty.discard(j.name)
                        time.sleep(1.0)  # dosya kopyalaması bitsin
                        with self._lock:
                            run_job(j, self.log)
                        self._last[j.name] = time.time()
                        changed = True
                except Exception as e:
                    self.log(f"[{j.name}] HATA: {e}")
                    self._last[j.name] = time.time()
            if changed:
                # yalnızca durum alanlarını güncelle (kullanıcının eşzamanlı düzenlemesini ezmemek için)
                fresh = load_jobs()
                st = {j.name: (j.last_run, j.last_result) for j in jobs}
                for f in fresh:
                    if f.name in st:
                        f.last_run, f.last_result = st[f.name]
                save_jobs(fresh)
            self._stop.wait(2.0)
