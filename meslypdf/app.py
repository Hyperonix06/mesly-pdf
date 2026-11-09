"""Mesly PDF masaüstü uygulaması giriş noktası.

Kullanım:
  MeslyPDF.exe [dosyalar...]          Dosyaları açar
  MeslyPDF.exe --ocr dosya            Aranabilir PDF oluşturur (dosya_ocr.pdf)
  MeslyPDF.exe --to docx dosya        Word/Excel/... biçimine dönüştürür
  MeslyPDF.exe --compress dosya       Sıkıştırır
  MeslyPDF.exe --merge dosyalar       Birleştirir
  MeslyPDF.exe --compare a [b]        Karşılaştırma ekranını açar
  MeslyPDF.exe --tray                 Tepside başlar (Hot Folder ile)
  MeslyPDF.exe --cli ...              Komut satırı arayüzü (meslypdf --help)
"""
from __future__ import annotations

import os
import sys

SERVER = "MeslyPDF-" + (os.environ.get("USERNAME") or os.environ.get("USER") or "kullanici")


def _send_to_running(args) -> bool:
    from PySide6.QtNetwork import QLocalSocket
    s = QLocalSocket()
    s.connectToServer(SERVER)
    if not s.waitForConnected(400):
        return False
    s.write(("\x1f".join(args)).encode("utf-8"))
    s.waitForBytesWritten(1000)
    s.disconnectFromServer()
    return True


def _quick_action(win, args):
    """Sağ tık menüsünden gelen tek seferlik işlemler."""
    from .core import convert, editing, ocr
    from .ui.common import run_task, toast, Settings, open_path
    from PySide6.QtWidgets import QMessageBox
    if not args:
        return False
    a0 = args[0]
    files = [os.path.abspath(f) for f in args[1:] if os.path.exists(f)]
    if a0 == "--to" and len(args) >= 3:
        fmt = args[1]
        files = [os.path.abspath(f) for f in args[2:] if os.path.exists(f)]
        for f in files:
            out = os.path.splitext(f)[0] + "." + fmt
            k = 1
            while os.path.exists(out):
                out = f"{os.path.splitext(f)[0]} ({k}).{fmt}"
                k += 1
            run_task(win, f"{os.path.basename(f)} dönüştürülüyor…", convert.convert, [f], out, fmt, Settings.get("ocr_lang"),
                     on_done=lambda o, out=out: (toast(win, f"Oluşturuldu: {os.path.basename(out)}", "ok"), open_path(out)))
        return True
    if a0 == "--ocr":
        for f in files:
            out = os.path.splitext(f)[0] + "_aranabilir.pdf"
            run_task(win, f"{os.path.basename(f)} tanınıyor…", convert.convert, [f], out, "pdf", Settings.get("ocr_lang"),
                     on_done=lambda o, out=out: win.open_document(out))
        return True
    if a0 == "--compress":
        for f in files:
            out = os.path.splitext(f)[0] + "_kucuk.pdf"
            run_task(win, "Sıkıştırılıyor…", editing.compress, f, out, "medium",
                     on_done=lambda r: QMessageBox.information(win, "Sıkıştırıldı", f"{os.path.basename(r[0])}\n{r[1] / 1024:.0f} KB → {r[2] / 1024:.0f} KB"))
        return True
    if a0 == "--merge":
        if files:
            out = os.path.join(os.path.dirname(files[0]), "birlesik.pdf")
            run_task(win, "Birleştiriliyor…", convert.any_to_pdf, files, out, on_done=lambda _: win.open_document(out))
        return True
    if a0 == "--compare":
        win.open_compare(files[0] if files else None, files[1] if len(files) > 1 else None)
        return True
    return False


def handle_args(win, args):
    args = [a for a in args if a]
    if not args:
        return
    if args[0] == "--tray":
        return
    if args[0].startswith("--") and _quick_action(win, args):
        win.show_normal()
        return
    win.open_many([a for a in args if os.path.exists(a)])
    win.show_normal()


def _utf8_console():
    for stream in (sys.stdout, sys.stderr):
        try:
            if stream is not None:
                stream.reconfigure(encoding="utf-8", errors="replace")
        except Exception:
            pass


def selftest(args) -> int:
    """Kurulum sonrası doğrulama: arayüzü açar, örnek belge üretip düzenleyicide açar, OCR ve dönüştürmeyi dener."""
    import tempfile
    import traceback
    _utf8_console()
    from PySide6.QtWidgets import QApplication
    app = QApplication.instance() or QApplication(sys.argv)
    report = []
    try:
        from .ui import theme
        from .ui.common import Settings
        Settings.load()
        theme.apply(app)
        from .ui.main_window import MainWindow
        from .core import ocr, convert, pages
        from reportlab.pdfgen import canvas
        from .core.fonts import font_name
        d = tempfile.mkdtemp(prefix="mesly_selftest_")
        pdf = os.path.join(d, "ornek.pdf")
        c = canvas.Canvas(pdf)
        c.setFont(font_name(), 14)
        c.drawString(72, 760, "Mesly PDF öz test: çğıöşü ÇĞİÖŞÜ")
        c.save()
        win = MainWindow()
        win.show()
        tab = win.open_document(pdf)
        app.processEvents()
        report.append(("Arayüz ve PDF düzenleyici", tab is not None and tab.doc.page_count == 1))
        report.append(("OCR motoru", ocr.tesseract_ok()))
        langs = ocr.available_languages()
        report.append(("Türkçe OCR dili", "tur" in langs))
        img = tab.doc.render_dpi(0, 200)
        ip = os.path.join(d, "goruntu.png")
        img.save(ip)
        r = ocr.recognize_image(img, "tur+eng" if "tur" in langs else "eng", 200, detect_layout=False)
        report.append(("OCR tanıma", "test" in r.text.lower()))
        out = os.path.join(d, "ornek.docx")
        convert.convert([pdf], out, "docx")
        report.append(("Word dönüştürme", os.path.getsize(out) > 0))
        win.close()
    except Exception:
        traceback.print_exc()
        report.append(("Beklenmeyen hata", False))
    ok = all(v for _, v in report)
    lines = [f"[{'OK' if v else 'HATA'}] {k}" for k, v in report]
    text = "\n".join(lines) + f"\nSONUÇ: {'BAŞARILI' if ok else 'BAŞARISIZ'}"
    print(text)
    if args and args[0]:
        with open(args[0], "w", encoding="utf-8") as f:
            f.write(text)
    return 0 if ok else 1


def main():
    args = sys.argv[1:]
    if args and args[0] == "--cli":
        from .cli import main as cli_main
        sys.exit(cli_main(args[1:]))
    if args and args[0] == "--selftest":
        sys.exit(selftest(args[1:]))
    os.environ.setdefault("QT_ENABLE_HIGHDPI_SCALING", "1")
    from PySide6.QtCore import Qt, QCoreApplication
    from PySide6.QtWidgets import QApplication
    from PySide6.QtNetwork import QLocalServer
    QApplication.setHighDpiScaleFactorRoundingPolicy(Qt.HighDpiScaleFactorRoundingPolicy.PassThrough)
    app = QApplication(sys.argv)
    app.setApplicationName("Mesly PDF")
    app.setOrganizationName("Mesly")
    app.setQuitOnLastWindowClosed(False)
    if _send_to_running([os.path.abspath(a) if os.path.exists(a) else a for a in args]):
        return 0
    if sys.platform == "win32":
        try:
            import ctypes
            ctypes.windll.shell32.SetCurrentProcessExplicitAppUserModelID("Mesly.MeslyPDF")
        except Exception:
            pass
    from .ui import theme
    from .ui.common import Settings
    Settings.load()
    theme.apply(app, Settings.get("dark"))
    from .ui.main_window import MainWindow
    win = MainWindow()
    server = QLocalServer()
    QLocalServer.removeServer(SERVER)
    server.listen(SERVER)

    def incoming():
        sock = server.nextPendingConnection()
        if sock.waitForReadyRead(1000):
            data = bytes(sock.readAll()).decode("utf-8")
            handle_args(win, data.split("\x1f") if data else [])
            win.show_normal()
    server.newConnection.connect(incoming)
    app.lastWindowClosed.connect(lambda: None if win.hotfolder_service.running else app.quit())
    if args and args[0] == "--tray":
        win.hotfolder_service.start()
        win.update_tray()
    else:
        win.show()
        handle_args(win, args)
    rc = app.exec()
    if win.hotfolder_service.running:
        win.hotfolder_service.stop()
    return rc


if __name__ == "__main__":
    sys.exit(main())
