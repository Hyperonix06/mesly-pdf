# -*- mode: python ; coding: utf-8 -*-
# PyInstaller tanımı: iki yürütülebilir dosya (MeslyPDF.exe arayüz, meslypdf-cli.exe komut satırı)
# Derleme:  pyinstaller packaging/meslypdf.spec --noconfirm
import os
import sys
from PyInstaller.utils.hooks import collect_all, collect_data_files, collect_dynamic_libs, collect_submodules

ROOT = os.path.abspath(os.path.join(SPECPATH, ".."))
datas = [(os.path.join(ROOT, "meslypdf", "assets"), os.path.join("meslypdf", "assets"))]
binaries = []
hidden = collect_submodules("meslypdf") + ["pyhanko.sign.pkcs11", "pkcs11", "fontTools.subset", "fontTools.ttLib",
                                           "pyhanko.pdf_utils.font.opentype", "uharfbuzz"]
if sys.platform == "win32":
    hidden += ["win32com.client", "pythoncom", "pywintypes"]
for pkg in ("pypdfium2", "pypdfium2_raw", "pikepdf", "qtawesome", "pyhanko", "pyhanko_certvalidator", "zxingcpp",
            "docx", "pptx", "odf", "reportlab"):
    try:
        d, b, h = collect_all(pkg)
        datas += d
        binaries += b
        hidden += h
    except Exception:
        pass

a = Analysis([os.path.join(ROOT, "run_app.py")], pathex=[ROOT], binaries=binaries, datas=datas,
             hiddenimports=hidden, hookspath=[], runtime_hooks=[],
             excludes=["tkinter", "matplotlib", "IPython", "pytest", "PyQt5", "PyQt6", "PySide2", "scipy", "pandas",
                       "sympy", "numba", "xsdata", "pyhanko.generated.etsi",
                       "PySide6.QtWebEngineCore", "PySide6.QtWebEngineWidgets", "PySide6.Qt3DCore", "PySide6.QtQuick",
                       "PySide6.QtQuick3D", "PySide6.QtQml", "PySide6.QtMultimedia", "PySide6.QtPdf", "PySide6.QtPdfWidgets",
                       "PySide6.QtDataVisualization", "PySide6.QtCharts", "PySide6.QtBluetooth", "PySide6.QtSql",
                       "PySide6.QtDesigner", "PySide6.QtHelp", "PySide6.QtTest", "PySide6.QtLocation", "PySide6.QtPositioning"],
             noarchive=False)
pyz = PYZ(a.pure)

common = dict(debug=False, bootloader_ignore_signals=False, strip=False, upx=False,
              icon=os.path.join(ROOT, "meslypdf", "assets", "icon.ico"),
              version=os.path.join(SPECPATH, "version_info.txt"))
gui = EXE(pyz, a.scripts, [], exclude_binaries=True, name="MeslyPDF", console=False, **common)
cli = EXE(pyz, a.scripts, [], exclude_binaries=True, name="meslypdf-cli", console=True, **common)
coll = COLLECT(gui, cli, a.binaries, a.datas, strip=False, upx=False, name="MeslyPDF")
