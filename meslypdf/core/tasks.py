"""Otomatik görevler: adımlardan oluşan, kaydedilip tekrar çalıştırılabilen iş akışları.
Hot Folder da aynı motoru kullanır."""
from __future__ import annotations

import datetime as _dt
import glob
import json
import os
import shutil
import tempfile
from dataclasses import dataclass, field, asdict

from .ocr import app_data_dir
from .preprocess import PreprocessOptions

# Adım türleri: ad -> (Türkçe etiket, varsayılan parametreler)
STEP_TYPES = {
    "ocr": ("OCR ile aranabilir yap", {"lang": "tur+eng"}),
    "preprocess": ("Görüntü ön işleme (eğiklik, yön, arka plan)", {"deskew": True, "auto_orient": True, "whiten_background": True}),
    "merge": ("Tüm dosyaları tek PDF'te birleştir", {}),
    "split_blank": ("Boş sayfalardan belgelere ayır", {"remove_blank": True}),
    "remove_blank": ("Boş sayfaları sil", {}),
    "compress": ("Sıkıştır", {"level": "medium"}),
    "watermark": ("Filigran ekle", {"text": "GİZLİ", "opacity": 0.2}),
    "header_footer": ("Üstbilgi/altbilgi ekle", {"footer_center": "Sayfa {sayfa} / {toplam}"}),
    "bates": ("Bates numarası ekle", {"prefix": "", "start": 1, "digits": 6}),
    "stamp": ("Damga ekle", {"text": "ONAYLANDI"}),
    "encrypt": ("Parola ile koru", {"user_pw": "", "owner_pw": "", "method": "AES-256"}),
    "redact": ("Hassas verileri karart", {"patterns": ["TC Kimlik No", "IBAN"], "terms": []}),
    "remove_hidden": ("Gizli verileri temizle", {}),
    "metadata": ("Meta verileri ayarla", {"title": "", "author": ""}),
    "pdfa": ("PDF/A'ya dönüştür", {"part": 2}),
    "sign": ("Dijital imza", {"p12": "", "password": "", "reason": ""}),
    "export": ("Farklı formatta kaydet", {"format": "docx"}),
    "save": ("PDF olarak kaydet", {}),
}


@dataclass
class Task:
    name: str
    steps: list = field(default_factory=list)  # [{'type':..., 'params': {...}}]
    output_dir: str = ""
    naming: str = "{ad}"                       # {ad} {tarih} {saat} {sayac}
    description: str = ""

    def to_json(self):
        return asdict(self)

    @staticmethod
    def from_json(d):
        return Task(**{k: v for k, v in d.items() if k in Task.__dataclass_fields__})


def tasks_file() -> str:
    return os.path.join(app_data_dir(), "gorevler.json")


def load_tasks() -> list[Task]:
    p = tasks_file()
    if not os.path.exists(p):
        return default_tasks()
    try:
        return [Task.from_json(d) for d in json.load(open(p, encoding="utf-8"))]
    except Exception:
        return default_tasks()


def save_tasks(tasks: list[Task]):
    with open(tasks_file(), "w", encoding="utf-8") as f:
        json.dump([t.to_json() for t in tasks], f, ensure_ascii=False, indent=2)


def default_tasks() -> list[Task]:
    return [
        Task("Taramaları aranabilir PDF'e çevir", [{"type": "preprocess", "params": STEP_TYPES["preprocess"][1]},
                                                   {"type": "ocr", "params": {"lang": "tur+eng"}},
                                                   {"type": "compress", "params": {"level": "medium"}},
                                                   {"type": "save", "params": {}}],
             description="Görüntüleri ve taranmış PDF'leri düzeltip OCR uygular"),
        Task("Arşivle (PDF/A)", [{"type": "ocr", "params": {"lang": "tur+eng"}}, {"type": "pdfa", "params": {"part": 2}},
                                 {"type": "save", "params": {}}]),
        Task("Paylaşmadan önce temizle", [{"type": "remove_hidden", "params": {}},
                                          {"type": "redact", "params": {"patterns": ["TC Kimlik No", "IBAN", "E-posta", "Telefon"], "terms": []}},
                                          {"type": "compress", "params": {"level": "medium"}}, {"type": "save", "params": {}}]),
        Task("Word'e dönüştür", [{"type": "export", "params": {"format": "docx"}}]),
    ]


def _out_name(task: Task, base: str, counter: int, ext: str) -> str:
    now = _dt.datetime.now()
    name = (task.naming or "{ad}").replace("{ad}", base).replace("{tarih}", now.strftime("%Y-%m-%d")) \
        .replace("{saat}", now.strftime("%H-%M-%S")).replace("{sayac}", f"{counter:04d}")
    return name + "." + ext


def _unique(path: str) -> str:
    if not os.path.exists(path):
        return path
    b, e = os.path.splitext(path)
    k = 1
    while os.path.exists(f"{b} ({k}){e}"):
        k += 1
    return f"{b} ({k}){e}"


def run_task(task: Task, inputs: list[str], output_dir: str | None = None, log=print, progress=None,
             cancel=None) -> list[str]:
    """Görevi girdilere uygular; üretilen dosyaların listesini döndürür."""
    from . import pages, editing, security, convert, ocr as ocrmod, sign as signmod
    out_dir = output_dir or task.output_dir or os.path.dirname(inputs[0])
    os.makedirs(out_dir, exist_ok=True)
    work = tempfile.mkdtemp(prefix="mesly_")
    produced: list[str] = []
    try:
        prep = PreprocessOptions()
        steps = task.steps
        # 1) Her girdiyi PDF'e getir (görüntüler/Office)
        docs: list[tuple[str, str]] = []  # (taban ad, çalışma pdf)
        has_ocr = any(s["type"] == "ocr" for s in steps)
        for p in steps:
            if p["type"] == "preprocess":
                prm = p.get("params", {})
                prep = PreprocessOptions(**{k: v for k, v in prm.items() if k in PreprocessOptions.__dataclass_fields__})
        ocr_lang = next((s["params"].get("lang", "tur+eng") for s in steps if s["type"] == "ocr"), "tur+eng")
        for n, path in enumerate(inputs):
            base = os.path.splitext(os.path.basename(path))[0]
            dst = os.path.join(work, f"{n:04d}.pdf")
            ext = os.path.splitext(path)[1].lower()
            if ext in convert.IMAGE_EXT and has_ocr:
                ocrmod.ocr_images_to_pdf([path], dst, ocr_lang, prep)
            else:
                convert.any_to_pdf([path], dst)
            docs.append((base, dst))
            log(f"Hazırlandı: {os.path.basename(path)}")
        total = len(steps)
        for si, step in enumerate(steps):
            if cancel and cancel():
                raise InterruptedError("İptal edildi")
            t, prm = step["type"], step.get("params", {})
            if progress:
                progress(si, total)
            log(f"Adım {si + 1}/{total}: {STEP_TYPES.get(t, (t,))[0]}")
            if t == "merge" and len(docs) > 1:
                dst = os.path.join(work, "birlesik.pdf")
                pages.merge([d for _, d in docs], dst)
                docs = [(docs[0][0] + "_birlesik", dst)]
                continue
            if t == "split_blank":
                new_docs = []
                for base, d in docs:
                    blanks = set(pages.detect_blank_pages(d))
                    from .document import PdfDocument
                    with PdfDocument(d) as pd:
                        n = pd.page_count
                    groups, cur = [], []
                    for i in range(n):
                        if i in blanks:
                            if cur:
                                groups.append(cur)
                            cur = []
                            if not prm.get("remove_blank", True):
                                cur.append(i)
                        else:
                            cur.append(i)
                    if cur:
                        groups.append(cur)
                    for gi, g in enumerate(groups, 1):
                        dst = os.path.join(work, f"{base}_{gi:03d}.pdf")
                        pages.extract_pages(d, g, dst)
                        new_docs.append((f"{base}_{gi:03d}", dst))
                docs = new_docs
                log(f"{len(docs)} belgeye ayrıldı")
                continue
            new_docs = []
            for base, d in docs:
                nxt = d + f".{si}.pdf"
                if t == "ocr":
                    ocrmod.ocr_pdf(d, nxt, prm.get("lang", "tur+eng"))
                elif t == "preprocess":
                    nxt = d  # görüntülere hazırlık sırasında uygulandı
                elif t == "remove_blank":
                    bl = pages.detect_blank_pages(d)
                    from .document import PdfDocument
                    with PdfDocument(d) as pd:
                        cnt = pd.page_count
                    if bl and len(bl) < cnt:
                        pages.delete(d, bl, nxt)
                    else:
                        nxt = d
                elif t == "compress":
                    editing.compress(d, nxt, prm.get("level", "medium"))
                elif t == "watermark":
                    editing.add_watermark(d, nxt, prm.get("text", "GİZLİ"), opacity=float(prm.get("opacity", 0.2)))
                elif t == "header_footer":
                    editing.add_header_footer(d, nxt, header={"right": prm.get("header_right", "")},
                                              footer={"center": prm.get("footer_center", "Sayfa {sayfa} / {toplam}")})
                elif t == "bates":
                    outs, nxt_num = editing.bates_number([d], os.path.join(work, f"bates{si}"), prm.get("prefix", ""),
                                                         start=int(prm.get("start", 1)), digits=int(prm.get("digits", 6)))
                    prm["start"] = nxt_num
                    shutil.move(outs[0], nxt)
                elif t == "stamp":
                    editing.add_stamp(d, nxt, prm.get("text", "ONAYLANDI"), pages=[0])
                elif t == "encrypt":
                    security.encrypt(d, nxt, prm.get("user_pw", ""), prm.get("owner_pw", ""), prm.get("method", "AES-256"))
                elif t == "redact":
                    security.redact_text(d, nxt, prm.get("terms", []), prm.get("patterns", []))
                elif t == "remove_hidden":
                    security.remove_hidden_data(d, nxt)
                elif t == "metadata":
                    editing.set_metadata(d, nxt, **{k: v for k, v in prm.items() if v})
                elif t == "pdfa":
                    convert.make_pdfa(d, nxt, int(prm.get("part", 2)))
                elif t == "sign":
                    signmod.sign_pdf(d, nxt, prm["p12"], prm.get("password", ""), reason=prm.get("reason", ""), box=None)
                elif t == "export":
                    fmt = prm.get("format", "docx")
                    ext = {"pdfa": "pdf", "pdfua": "pdf"}.get(fmt, fmt)
                    out = _unique(os.path.join(out_dir, _out_name(task, base, len(produced) + 1, ext)))
                    convert.convert([d], out, fmt, lang=ocr_lang)
                    produced.append(out)
                    log(f"Kaydedildi: {out}")
                    nxt = d
                elif t == "save":
                    out = _unique(os.path.join(out_dir, _out_name(task, base, len(produced) + 1, "pdf")))
                    shutil.copyfile(d, out)
                    produced.append(out)
                    log(f"Kaydedildi: {out}")
                    nxt = d
                new_docs.append((base, nxt))
            docs = new_docs
        if not produced:  # kaydet adımı yoksa sonuçları yine de kaydet
            for base, d in docs:
                out = _unique(os.path.join(out_dir, _out_name(task, base, len(produced) + 1, "pdf")))
                shutil.copyfile(d, out)
                produced.append(out)
        if progress:
            progress(total, total)
        return produced
    finally:
        shutil.rmtree(work, ignore_errors=True)


def expand_inputs(patterns: list[str]) -> list[str]:
    out = []
    for p in patterns:
        if os.path.isdir(p):
            for f in sorted(os.listdir(p)):
                fp = os.path.join(p, f)
                if os.path.isfile(fp):
                    out.append(fp)
        else:
            out += sorted(glob.glob(p)) or [p]
    return out
