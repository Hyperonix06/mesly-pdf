"""Mesly PDF komut satırı arayüzü.

Örnekler:
  meslypdf ocr tarama.pdf -o aranabilir.pdf --lang tur+eng
  meslypdf convert belge.pdf -o belge.docx
  meslypdf convert *.jpg -o kitap.pdf --format pdf
  meslypdf merge a.pdf b.pdf -o birlesik.pdf
  meslypdf split kitap.pdf -o parcalar --every 10
  meslypdf compress buyuk.pdf -o kucuk.pdf --level high
  meslypdf protect gizli.pdf -o korumali.pdf --user-pw 1234
  meslypdf redact sozlesme.pdf -o temiz.pdf --pattern "TC Kimlik No" --term "Ahmet"
  meslypdf compare eski.pdf yeni.docx --xlsx farklar.xlsx
  meslypdf sign belge.pdf -o imzali.pdf --p12 imza.pfx --password ****
  meslypdf verify imzali.pdf
  meslypdf pdfa belge.pdf -o arsiv.pdf
  meslypdf task "Arşivle (PDF/A)" klasor/ -o cikti/
  meslypdf hotfolder run
"""
from __future__ import annotations

import argparse
import json
import os
import sys


def _progress(i, n):
    if n:
        bar = int(30 * i / n)
        sys.stderr.write(f"\r[{'#' * bar}{'.' * (30 - bar)}] {i}/{n}")
        if i >= n:
            sys.stderr.write("\n")
        sys.stderr.flush()


def main(argv=None):
    from .core import tasks as tk
    p = argparse.ArgumentParser(prog="meslypdf", description="Mesly PDF komut satırı", epilog=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="cmd", required=True)

    s = sub.add_parser("info", help="Belge bilgisi")
    s.add_argument("file")

    s = sub.add_parser("ocr", help="Aranabilir PDF oluştur")
    s.add_argument("files", nargs="+")
    s.add_argument("-o", "--output", required=True)
    s.add_argument("--lang", default="tur+eng")
    s.add_argument("--dpi", type=int, default=300)
    s.add_argument("--force", action="store_true", help="Metin katmanı olan sayfaları da tanı")

    s = sub.add_parser("convert", help="Dönüştür (docx, xlsx, pptx, odt, rtf, txt, csv, html, epub, fb2, pdf, pdfa, pdfua)")
    s.add_argument("files", nargs="+")
    s.add_argument("-o", "--output", required=True, help="Çıktı dosyası veya --batch ile klasör")
    s.add_argument("--format")
    s.add_argument("--lang", default="tur+eng")
    s.add_argument("--plain", action="store_true", help="Biçimsiz metin (Word)")
    s.add_argument("--batch", action="store_true", help="Her dosyayı ayrı dönüştür")

    s = sub.add_parser("merge", help="Birleştir")
    s.add_argument("files", nargs="+")
    s.add_argument("-o", "--output", required=True)

    s = sub.add_parser("split", help="Böl")
    s.add_argument("file")
    s.add_argument("-o", "--output", required=True)
    g = s.add_mutually_exclusive_group()
    g.add_argument("--every", type=int)
    g.add_argument("--ranges")
    g.add_argument("--bookmarks", action="store_true")
    g.add_argument("--size-mb", type=float)

    s = sub.add_parser("compress", help="Sıkıştır")
    s.add_argument("file")
    s.add_argument("-o", "--output", required=True)
    s.add_argument("--level", choices=["low", "medium", "high"], default="medium")

    s = sub.add_parser("protect", help="Parola ile koru")
    s.add_argument("file")
    s.add_argument("-o", "--output", required=True)
    s.add_argument("--user-pw", default="")
    s.add_argument("--owner-pw", default="")
    s.add_argument("--method", default="AES-256", choices=["RC4-40", "RC4-128", "AES-128", "AES-256"])
    s.add_argument("--no-print", action="store_true")
    s.add_argument("--no-copy", action="store_true")
    s.add_argument("--no-modify", action="store_true")

    s = sub.add_parser("unprotect", help="Parolayı kaldır")
    s.add_argument("file")
    s.add_argument("-o", "--output", required=True)
    s.add_argument("--password", required=True)

    s = sub.add_parser("redact", help="Karart")
    s.add_argument("file")
    s.add_argument("-o", "--output", required=True)
    s.add_argument("--term", action="append", default=[])
    s.add_argument("--pattern", action="append", default=[], help="TC Kimlik No, IBAN, E-posta, Telefon, Kredi Kartı veya regex")

    s = sub.add_parser("clean", help="Gizli verileri temizle")
    s.add_argument("file")
    s.add_argument("-o", "--output", required=True)

    s = sub.add_parser("watermark", help="Filigran ekle")
    s.add_argument("file")
    s.add_argument("-o", "--output", required=True)
    s.add_argument("--text", default="GİZLİ")
    s.add_argument("--opacity", type=float, default=0.25)

    s = sub.add_parser("compare", help="İki belgeyi karşılaştır")
    s.add_argument("old")
    s.add_argument("new")
    s.add_argument("--xlsx")
    s.add_argument("--docx")
    s.add_argument("--pdf-dir")
    s.add_argument("--lang", default="tur+eng")
    s.add_argument("--ignore-case", action="store_true")
    s.add_argument("--ignore-punct", action="store_true")

    s = sub.add_parser("sign", help="Dijital imza")
    s.add_argument("file")
    s.add_argument("-o", "--output", required=True)
    s.add_argument("--p12", required=True)
    s.add_argument("--password", required=True)
    s.add_argument("--reason", default="")
    s.add_argument("--location", default="")
    s.add_argument("--invisible", action="store_true")
    s.add_argument("--tsa")

    s = sub.add_parser("verify", help="İmzaları doğrula")
    s.add_argument("file")

    s = sub.add_parser("pdfa", help="PDF/A'ya dönüştür")
    s.add_argument("file")
    s.add_argument("-o", "--output", required=True)
    s.add_argument("--part", type=int, default=2, choices=[1, 2, 3])

    s = sub.add_parser("task", help="Kayıtlı otomatik görevi çalıştır")
    s.add_argument("name", nargs="?")
    s.add_argument("inputs", nargs="*")
    s.add_argument("-o", "--output")
    s.add_argument("--list", action="store_true")

    s = sub.add_parser("hotfolder", help="Hot Folder")
    s.add_argument("action", choices=["run", "list", "once"])
    s.add_argument("--job")

    s = sub.add_parser("languages", help="OCR dillerini listele / indir")
    s.add_argument("--download")

    a = p.parse_args(argv)

    if a.cmd == "info":
        from .core.document import PdfDocument
        from .core.security import encryption_info
        with PdfDocument(a.file) as d:
            print(json.dumps({"sayfa": d.page_count, "metin_katmani": d.has_text_layer(), "meta": d.metadata(),
                              "sifreleme": encryption_info(a.file)}, ensure_ascii=False, indent=2))
    elif a.cmd == "ocr":
        from .core import ocr, convert
        if len(a.files) == 1 and a.files[0].lower().endswith(".pdf"):
            ocr.ocr_pdf(a.files[0], a.output, a.lang, a.dpi, skip_text_pages=not a.force, progress=_progress)
        else:
            convert.convert(a.files, a.output, "pdf", lang=a.lang, progress=_progress)
        print(a.output)
    elif a.cmd == "convert":
        from .core import convert
        fmt = (a.format or os.path.splitext(a.output)[1].lstrip(".")).lower()
        if a.batch:
            for src, out in convert.batch_convert(a.files, a.output, fmt, lang=a.lang, progress=_progress,
                                                  mode="plain" if a.plain else "formatted"):
                print(f"{src} -> {out}")
        else:
            convert.convert(a.files, a.output, fmt, lang=a.lang, progress=_progress, mode="plain" if a.plain else "formatted")
            print(a.output)
    elif a.cmd == "merge":
        from .core import convert
        convert.any_to_pdf(a.files, a.output)
        print(a.output)
    elif a.cmd == "split":
        from .core import pages
        if a.ranges:
            outs = pages.split(a.file, a.output, "ranges", a.ranges)
        elif a.bookmarks:
            outs = pages.split(a.file, a.output, "bookmarks")
        elif a.size_mb:
            outs = pages.split(a.file, a.output, "size", a.size_mb)
        else:
            outs = pages.split(a.file, a.output, "every", a.every or 1)
        print("\n".join(outs))
    elif a.cmd == "compress":
        from .core import editing
        out, b, c = editing.compress(a.file, a.output, a.level)
        print(f"{out}: {b / 1024:.0f} KB -> {c / 1024:.0f} KB")
    elif a.cmd == "protect":
        from .core import security
        security.encrypt(a.file, a.output, a.user_pw, a.owner_pw, a.method, allow_print=not a.no_print,
                         allow_copy=not a.no_copy, allow_modify=not a.no_modify)
        print(a.output)
    elif a.cmd == "unprotect":
        from .core import security
        security.decrypt(a.file, a.output, a.password)
        print(a.output)
    elif a.cmd == "redact":
        from .core import security
        out, n = security.redact_text(a.file, a.output, a.term, a.pattern)
        print(f"{out}: {n} bölge karartıldı")
    elif a.cmd == "clean":
        from .core import security
        out, removed = security.remove_hidden_data(a.file, a.output)
        print(f"{out}: " + (", ".join(removed) or "temizlenecek veri yok"))
    elif a.cmd == "watermark":
        from .core import editing
        editing.add_watermark(a.file, a.output, a.text, opacity=a.opacity)
        print(a.output)
    elif a.cmd == "compare":
        from .core import compare
        r = compare.compare(a.old, a.new, a.lang, a.ignore_case, a.ignore_punct, progress=_progress)
        print(compare.export_report_text(r))
        if a.xlsx:
            compare.export_xlsx(r, a.xlsx)
        if a.docx:
            compare.export_docx_track_changes(r, a.docx)
        if a.pdf_dir:
            os.makedirs(a.pdf_dir, exist_ok=True)
            compare.export_annotated_pdfs(r, os.path.join(a.pdf_dir, "eski_isaretli.pdf"),
                                          os.path.join(a.pdf_dir, "yeni_isaretli.pdf"))
    elif a.cmd == "sign":
        from .core import sign
        sign.sign_pdf(a.file, a.output, a.p12, a.password, reason=a.reason, location=a.location,
                      box=None if a.invisible else (360, 40, 560, 110), tsa_url=a.tsa)
        print(a.output)
    elif a.cmd == "verify":
        from .core import sign
        res = sign.verify(a.file)
        if not res:
            print("Belgede imza yok")
        for r in res:
            print(f"{r['field']}: {r['signer']} · {r['signing_time']} · {r['summary']}")
        return 0 if all(r["intact"] for r in res) else 2
    elif a.cmd == "pdfa":
        from .core import convert
        out, warns = convert.make_pdfa(a.file, a.output, a.part)
        print(out)
        for w in warns:
            print("Uyarı:", w)
    elif a.cmd == "task":
        tasks = tk.load_tasks()
        if a.list or not a.name:
            for t in tasks:
                print(f"- {t.name}: " + " → ".join(tk.STEP_TYPES.get(s['type'], (s['type'],))[0] for s in t.steps))
            return 0
        t = next((t for t in tasks if t.name == a.name), None)
        if not t:
            print(f"Görev bulunamadı: {a.name}", file=sys.stderr)
            return 1
        outs = tk.run_task(t, tk.expand_inputs(a.inputs), a.output, progress=_progress)
        print("\n".join(outs))
    elif a.cmd == "hotfolder":
        from .core import hotfolder as hf
        if a.action == "list":
            for j in hf.load_jobs():
                print(f"{'✓' if j.enabled else '✗'} {j.name} · {j.source_type}:{j.source} · {j.schedule} · son: {j.last_run} {j.last_result}")
        elif a.action == "once":
            jobs = hf.load_jobs()
            for j in jobs:
                if j.enabled and (not a.job or j.name == a.job):
                    hf.run_job(j, lambda m: print(hf.write_log(m)))
            hf.save_jobs(jobs)
        else:
            import time
            svc = hf.HotFolderService(on_event=print)
            svc.start()
            try:
                while True:
                    time.sleep(1)
            except KeyboardInterrupt:
                svc.stop()
    elif a.cmd == "languages":
        from .core import ocr
        if a.download:
            print(ocr.download_language(a.download, lambda p: _progress(p, 100)))
        for l in ocr.available_languages():
            print(f"{l}\t{ocr.LANG_NAMES.get(l, l)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
