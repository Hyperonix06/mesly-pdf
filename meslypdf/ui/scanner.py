"""Tarayıcıdan görüntü alma: Windows WIA (yerleşik) ve TWAIN (varsa). Tarayıcı yoksa dosyadan/kameradan içe aktarma."""
from __future__ import annotations

import os
import sys
import tempfile

from PySide6.QtWidgets import QMessageBox, QInputDialog

from .common import open_files, IMG_FILTER, Settings

WIA_FORMAT_PNG = "{B96B3CAF-0728-11D3-9D7B-0000F81EF32E}"


def wia_available() -> bool:
    if sys.platform != "win32":
        return False
    try:
        import win32com.client  # noqa
        return True
    except Exception:
        return False


def twain_available() -> bool:
    try:
        import twain  # noqa
        return True
    except Exception:
        return False


def _scan_wia(parent, color_mode: int, dpi: int) -> list[str]:
    import win32com.client
    import pythoncom
    pythoncom.CoInitialize()
    dlg = win32com.client.Dispatch("WIA.CommonDialog")
    out = []
    dev = None
    tmpd = tempfile.mkdtemp(prefix="mesly_scan_")
    while True:
        try:
            if dev is None:
                dev = dlg.ShowSelectDevice(1, False, False)  # 1 = tarayıcı
            if dev is None:
                break
            item = dev.Items[1]
            for pid, val in ((6146, color_mode), (6147, dpi), (6148, dpi)):  # renk niyeti, yatay/dikey dpi
                try:
                    for p in item.Properties:
                        if p.PropertyID == pid:
                            p.Value = val
                except Exception:
                    pass
            img = dlg.ShowTransfer(item, WIA_FORMAT_PNG, True)
        except Exception as e:
            if out:
                break
            QMessageBox.warning(parent, "Tarama", f"Tarama başlatılamadı:\n{e}")
            return []
        p = os.path.join(tmpd, f"tarama_{len(out) + 1:03d}.png")
        img.SaveFile(p)
        out.append(p)
        if QMessageBox.question(parent, "Tarama", f"{len(out)} sayfa tarandı. Bir sayfa daha taransın mı?") != QMessageBox.Yes:
            break
    return out


def _scan_twain(parent, dpi: int) -> list[str]:
    import twain
    sm = twain.SourceManager(0)
    src = sm.open_source()
    if not src:
        return []
    src.set_capability(twain.ICAP_XRESOLUTION, twain.TWTY_FIX32, float(dpi))
    src.set_capability(twain.ICAP_YRESOLUTION, twain.TWTY_FIX32, float(dpi))
    tmpd = tempfile.mkdtemp(prefix="mesly_scan_")
    out = []
    src.request_acquire(0, 0)
    more = 1
    while more:
        handle, more = src.xfer_image_natively()
        p = os.path.join(tmpd, f"tarama_{len(out) + 1:03d}.bmp")
        twain.dib_to_bm_file(handle, p)
        twain.global_handle_free(handle)
        out.append(p)
    src.destroy()
    sm.destroy()
    return out


def acquire_images(parent) -> list[str]:
    """Kullanıcıya uygun yöntemi sunar ve taranan görüntü dosyalarının yollarını döndürür."""
    options = []
    if wia_available():
        options.append("Tarayıcı (Windows WIA)")
    if twain_available():
        options.append("Tarayıcı (TWAIN)")
    options.append("Görüntü dosyalarından / telefon fotoğraflarından")
    if len(options) == 1:
        choice = options[0]
    else:
        choice, ok = QInputDialog.getItem(parent, "Görüntü al", "Kaynak:", options, 0, False)
        if not ok:
            return []
    dpi = int(Settings.get("dpi") or 300)
    if choice.startswith("Tarayıcı (Windows"):
        mode, ok = QInputDialog.getItem(parent, "Tarama", "Renk modu:", ["Renkli", "Gri tonlamalı", "Siyah-beyaz"], 1, False)
        if not ok:
            return []
        return _scan_wia(parent, {"Renkli": 1, "Gri tonlamalı": 2, "Siyah-beyaz": 4}[mode], dpi)
    if choice.startswith("Tarayıcı (TWAIN"):
        return _scan_twain(parent, dpi)
    return open_files(parent, "Görüntüleri seç", IMG_FILTER)
