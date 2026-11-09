"""Windows Gezgini entegrasyonu: sağ tık menüsü, 'Birlikte aç', 'Gönder' menüsü, oturum açılışında Hot Folder.
Yönetici izni gerektirmez (HKCU altına yazar). Kurulum programı da aynı kayıtları oluşturur."""
from __future__ import annotations

import os
import sys

APP_ID = "MeslyPDF"
VERBS = [
    ("MeslyPDF.Open", "Mesly PDF ile aç", ""),
    ("MeslyPDF.Ocr", "Mesly PDF: Aranabilir PDF yap", "--ocr"),
    ("MeslyPDF.Word", "Mesly PDF: Word'e dönüştür", "--to docx"),
    ("MeslyPDF.Excel", "Mesly PDF: Excel'e dönüştür", "--to xlsx"),
    ("MeslyPDF.Compress", "Mesly PDF: Sıkıştır", "--compress"),
    ("MeslyPDF.Compare", "Mesly PDF: Karşılaştır", "--compare"),
]
EXTS = [".pdf", ".png", ".jpg", ".jpeg", ".tif", ".tiff", ".bmp", ".docx", ".doc", ".xlsx", ".pptx"]


def exe_command() -> str:
    if getattr(sys, "frozen", False):
        return f'"{sys.executable}"'
    pyw = sys.executable.replace("python.exe", "pythonw.exe")
    return f'"{pyw}" -m meslypdf.app'


def is_windows() -> bool:
    return sys.platform == "win32"


def register(context_menu=True, open_with=True, send_to=True) -> list[str]:
    if not is_windows():
        raise OSError("Yalnızca Windows'ta kullanılabilir")
    import winreg
    done = []
    cmd = exe_command()
    icon = sys.executable if getattr(sys, "frozen", False) else ""
    base = r"Software\Classes"
    # ProgID
    with winreg.CreateKey(winreg.HKEY_CURRENT_USER, rf"{base}\{APP_ID}.Document") as k:
        winreg.SetValue(k, "", winreg.REG_SZ, "PDF belgesi (Mesly PDF)")
        if icon:
            winreg.SetValue(k, "DefaultIcon", winreg.REG_SZ, f"{icon},0")
        winreg.SetValue(k, r"shell\open\command", winreg.REG_SZ, f'{cmd} "%1"')
    if open_with:
        for ext in EXTS:
            with winreg.CreateKey(winreg.HKEY_CURRENT_USER, rf"{base}\{ext}\OpenWithProgids") as k:
                winreg.SetValueEx(k, f"{APP_ID}.Document", 0, winreg.REG_NONE, b"")
        done.append("Birlikte aç")
    if context_menu:
        for ext in EXTS:
            for verb, text, arg in VERBS:
                if verb == "MeslyPDF.Ocr" and ext in (".docx", ".doc", ".xlsx", ".pptx"):
                    continue
                kp = rf"{base}\SystemFileAssociations\{ext}\shell\{verb}"
                with winreg.CreateKey(winreg.HKEY_CURRENT_USER, kp) as k:
                    winreg.SetValueEx(k, "MUIVerb", 0, winreg.REG_SZ, text)
                    if icon:
                        winreg.SetValueEx(k, "Icon", 0, winreg.REG_SZ, icon)
                    if verb == "MeslyPDF.Compare":
                        winreg.SetValueEx(k, "MultiSelectModel", 0, winreg.REG_SZ, "Player")
                with winreg.CreateKey(winreg.HKEY_CURRENT_USER, kp + r"\command") as k:
                    winreg.SetValue(k, "", winreg.REG_SZ, f'{cmd} {arg} "%1"'.replace("  ", " "))
        done.append("Sağ tık menüsü")
    if send_to:
        try:
            sendto = os.path.join(os.environ["APPDATA"], r"Microsoft\Windows\SendTo", "Mesly PDF (birleştir).lnk")
            _shortcut(sendto, "--merge")
            done.append("Gönder menüsü")
        except Exception:
            pass
    _notify_shell()
    return done


def unregister():
    if not is_windows():
        return
    import winreg

    def delete_tree(root, path):
        try:
            with winreg.OpenKey(root, path, 0, winreg.KEY_ALL_ACCESS) as k:
                while True:
                    try:
                        sub = winreg.EnumKey(k, 0)
                    except OSError:
                        break
                    delete_tree(root, path + "\\" + sub)
            winreg.DeleteKey(root, path)
        except OSError:
            pass
    base = r"Software\Classes"
    delete_tree(winreg.HKEY_CURRENT_USER, rf"{base}\{APP_ID}.Document")
    for ext in EXTS:
        for verb, _, _ in VERBS:
            delete_tree(winreg.HKEY_CURRENT_USER, rf"{base}\SystemFileAssociations\{ext}\shell\{verb}")
        try:
            with winreg.OpenKey(winreg.HKEY_CURRENT_USER, rf"{base}\{ext}\OpenWithProgids", 0, winreg.KEY_ALL_ACCESS) as k:
                winreg.DeleteValue(k, f"{APP_ID}.Document")
        except OSError:
            pass
    try:
        os.remove(os.path.join(os.environ["APPDATA"], r"Microsoft\Windows\SendTo", "Mesly PDF (birleştir).lnk"))
    except OSError:
        pass
    set_autostart(False)
    _notify_shell()


def set_autostart(enabled: bool):
    """Oturum açılışında Hot Folder hizmetini tepsi simgesiyle başlatır."""
    if not is_windows():
        return
    import winreg
    with winreg.CreateKey(winreg.HKEY_CURRENT_USER, r"Software\Microsoft\Windows\CurrentVersion\Run") as k:
        if enabled:
            winreg.SetValueEx(k, APP_ID + "HotFolder", 0, winreg.REG_SZ, f"{exe_command()} --tray")
        else:
            try:
                winreg.DeleteValue(k, APP_ID + "HotFolder")
            except OSError:
                pass


def _shortcut(path, args):
    import subprocess
    target = sys.executable
    a = args if getattr(sys, "frozen", False) else f"-m meslypdf.app {args}"
    ps = (f"$s=(New-Object -ComObject WScript.Shell).CreateShortcut('{path}');"
          f"$s.TargetPath='{target}';$s.Arguments='{a}';$s.Save()")
    subprocess.run(["powershell", "-NoProfile", "-Command", ps], check=True, capture_output=True,
                   creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))


def _notify_shell():
    try:
        import ctypes
        ctypes.windll.shell32.SHChangeNotify(0x08000000, 0, None, None)
    except Exception:
        pass
