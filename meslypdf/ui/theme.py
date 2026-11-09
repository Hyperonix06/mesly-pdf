"""Görsel tema: renk paleti, Qt stil sayfası (açık/koyu), simgeler."""
from __future__ import annotations

import sys

from PySide6.QtGui import QColor, QFont, QPalette, QIcon
from PySide6.QtWidgets import QApplication

LIGHT = {
    "bg": "#F4F6FA", "surface": "#FFFFFF", "surface2": "#F8F9FC", "border": "#E1E5EE", "border_strong": "#C9D0DD",
    "text": "#1B2333", "text2": "#5B6577", "text3": "#8A93A5", "primary": "#2457D6", "primary_hover": "#1D49B8",
    "primary_soft": "#E8EEFC", "accent": "#D93025", "success": "#1E8E3E", "warning": "#E8A200", "danger": "#C62828",
    "canvas": "#E6E9F0", "sidebar": "#FFFFFF", "nav": "#14213D", "nav_text": "#C8D1E6", "nav_active": "#2457D6",
    "selection": "#CFDCFB",
}
DARK = {
    "bg": "#16191F", "surface": "#1E222A", "surface2": "#242934", "border": "#2E3440", "border_strong": "#3B4252",
    "text": "#E6E9EF", "text2": "#A7B0C0", "text3": "#7C8597", "primary": "#5B8CFF", "primary_hover": "#7AA2FF",
    "primary_soft": "#26324D", "accent": "#FF6B5E", "success": "#4CC26A", "warning": "#F2B33D", "danger": "#FF5C5C",
    "canvas": "#101317", "sidebar": "#1E222A", "nav": "#0E1117", "nav_text": "#9AA4B8", "nav_active": "#5B8CFF",
    "selection": "#2D3E66",
}

C = dict(LIGHT)  # etkin palet


def font_family() -> str:
    return "Segoe UI" if sys.platform == "win32" else "DejaVu Sans"


def icon(name: str, color: str | None = None, size: int | None = None) -> QIcon:
    import qtawesome as qta
    try:
        return qta.icon(name, color=color or C["text2"], color_active=color or C["primary"],
                        color_disabled=C["text3"])
    except Exception:
        return QIcon()


_ICON_DIR = None


def _icon_files(c: dict) -> dict:
    """QSS'te kullanılacak küçük ok/onay görsellerini üretir."""
    import os
    import tempfile
    global _ICON_DIR
    if _ICON_DIR is None:
        _ICON_DIR = tempfile.mkdtemp(prefix="mesly_qss_")
    out = {}
    try:
        import qtawesome as qta
        for key, name, col in (("up", "mdi6.chevron-up", c["text2"]), ("down", "mdi6.chevron-down", c["text2"]),
                               ("check", "mdi6.check-bold", "#FFFFFF")):
            path = os.path.join(_ICON_DIR, f"{key}_{col.strip('#')}.png").replace("\\", "/")
            if not os.path.exists(path):
                qta.icon(name, color=col).pixmap(32, 32).save(path)
            out[key] = path.replace("\\", "/")
    except Exception:
        pass
    return out


def qss(c: dict) -> str:
    ic = _icon_files(c)
    extra = ""
    if ic:
        extra = f"""
QAbstractSpinBox {{ padding-right: 22px; }}
QAbstractSpinBox::up-button {{ subcontrol-origin: border; subcontrol-position: top right; width: 20px; border: none;
    border-left: 1px solid {c['border']}; border-top-right-radius: 7px; background: {c['surface2']}; }}
QAbstractSpinBox::down-button {{ subcontrol-origin: border; subcontrol-position: bottom right; width: 20px; border: none;
    border-left: 1px solid {c['border']}; border-bottom-right-radius: 7px; background: {c['surface2']}; }}
QAbstractSpinBox::up-button:hover, QAbstractSpinBox::down-button:hover {{ background: {c['primary_soft']}; }}
QAbstractSpinBox::up-arrow {{ image: url({ic['up']}); width: 12px; height: 12px; }}
QAbstractSpinBox::down-arrow {{ image: url({ic['down']}); width: 12px; height: 12px; }}
QComboBox::down-arrow {{ image: url({ic['down']}); width: 12px; height: 12px; }}
QCheckBox::indicator {{ width: 16px; height: 16px; border: 1px solid {c['border_strong']}; border-radius: 4px; background: {c['surface']}; }}
QCheckBox::indicator:hover {{ border-color: {c['primary']}; }}
QCheckBox::indicator:checked {{ background: {c['primary']}; border-color: {c['primary']}; image: url({ic['check']}); }}
QCheckBox::indicator:disabled {{ background: {c['surface2']}; border-color: {c['border']}; }}
"""
    return _base_qss(c) + extra


def _base_qss(c: dict) -> str:
    return f"""
* {{ font-family: "{font_family()}"; font-size: 13px; color: {c['text']}; }}
QMainWindow, QDialog {{ background: {c['bg']}; }}
QWidget#Page {{ background: {c['bg']}; }}
QToolTip {{ background: {c['nav']}; color: #FFFFFF; border: none; padding: 6px 8px; border-radius: 6px; }}

/* Navigasyon rayı */
QWidget#NavRail {{ background: {c['nav']}; }}
QToolButton#NavButton {{ background: transparent; color: {c['nav_text']}; border: none; border-radius: 10px;
    padding: 8px 2px 6px 2px; font-size: 11px; margin: 2px 8px; }}
QToolButton#NavButton:hover {{ background: rgba(255,255,255,0.07); color: #FFFFFF; }}
QToolButton#NavButton:checked {{ background: {c['nav_active']}; color: #FFFFFF; }}
QLabel#NavLogo {{ color: #FFFFFF; font-weight: 700; font-size: 15px; }}

/* Kartlar */
QFrame#Card {{ background: {c['surface']}; border: 1px solid {c['border']}; border-radius: 12px; }}
QFrame#Card:hover {{ border-color: {c['primary']}; }}
QPushButton#ActionCard {{ background: {c['surface']}; border: 1px solid {c['border']}; border-radius: 12px;
    text-align: left; padding: 14px 16px; font-size: 13px; }}
QPushButton#ActionCard:hover {{ border: 1px solid {c['primary']}; background: {c['primary_soft']}; }}
QLabel#H1 {{ font-size: 26px; font-weight: 700; }}
QLabel#H2 {{ font-size: 17px; font-weight: 600; }}
QLabel#H3 {{ font-size: 14px; font-weight: 600; }}
QLabel#Muted {{ color: {c['text2']}; }}
QLabel#Small {{ color: {c['text3']}; font-size: 11px; }}
QLabel#Badge {{ background: {c['primary_soft']}; color: {c['primary']}; border-radius: 9px; padding: 2px 8px; font-size: 11px; font-weight: 600; }}

/* Düğmeler */
QPushButton {{ background: {c['surface']}; border: 1px solid {c['border_strong']}; border-radius: 8px; padding: 7px 14px; }}
QPushButton:hover {{ border-color: {c['primary']}; color: {c['primary']}; }}
QPushButton:pressed {{ background: {c['primary_soft']}; }}
QPushButton:disabled {{ color: {c['text3']}; border-color: {c['border']}; }}
QPushButton#Primary {{ background: {c['primary']}; color: #FFFFFF; border: 1px solid {c['primary']}; font-weight: 600; }}
QPushButton#Primary:hover {{ background: {c['primary_hover']}; color: #FFFFFF; }}
QPushButton#Danger {{ background: {c['danger']}; color: #FFFFFF; border: 1px solid {c['danger']}; font-weight: 600; }}
QPushButton#Flat {{ border: none; background: transparent; padding: 6px 8px; }}
QPushButton#Flat:hover {{ background: {c['primary_soft']}; }}

/* Şerit / araç çubuğu */
QWidget#Ribbon {{ background: {c['surface']}; border-bottom: 1px solid {c['border']}; }}
QTabBar#RibbonTabs::tab {{ background: transparent; border: none; padding: 8px 14px; color: {c['text2']}; font-weight: 600; }}
QTabBar#RibbonTabs::tab:selected {{ color: {c['primary']}; border-bottom: 2px solid {c['primary']}; }}
QTabBar#RibbonTabs::tab:hover {{ color: {c['text']}; }}
QToolButton#RibbonButton {{ background: transparent; border: 1px solid transparent; border-radius: 8px; padding: 4px 3px;
    font-size: 11px; color: {c['text']}; }}
QToolButton#RibbonButton:hover {{ background: {c['primary_soft']}; border-color: {c['primary_soft']}; }}
QToolButton#RibbonButton:checked {{ background: {c['primary_soft']}; border: 1px solid {c['primary']}; color: {c['primary']}; }}
QToolButton#RibbonButton::menu-indicator {{ image: none; }}
QFrame#RibbonSep {{ background: {c['border']}; max-width: 1px; min-width: 1px; margin: 6px 4px; }}
QLabel#RibbonGroup {{ color: {c['text3']}; font-size: 10px; }}

/* Belge sekmeleri */
QTabWidget#DocTabs::pane {{ border: none; }}
QTabBar#DocTabsBar::tab {{ background: {c['surface2']}; border: 1px solid {c['border']}; border-bottom: none;
    padding: 7px 12px; margin-right: 2px; border-top-left-radius: 8px; border-top-right-radius: 8px; color: {c['text2']}; min-width: 120px; }}
QTabBar#DocTabsBar::tab:selected {{ background: {c['surface']}; color: {c['text']}; font-weight: 600; }}

/* Kenar çubuğu */
QWidget#Sidebar {{ background: {c['sidebar']}; border-right: 1px solid {c['border']}; }}
QWidget#RightPanel {{ background: {c['sidebar']}; border-left: 1px solid {c['border']}; }}
QToolButton#SideTab {{ border: none; border-radius: 8px; padding: 7px; background: transparent; }}
QToolButton#SideTab:hover {{ background: {c['primary_soft']}; }}
QToolButton#SideTab:checked {{ background: {c['primary_soft']}; }}

QGraphicsView#PageView {{ background: {c['canvas']}; border: none; }}
QScrollArea {{ border: none; background: transparent; }}
QScrollArea > QWidget > QWidget {{ background: transparent; }}

/* Girdiler */
QLineEdit, QSpinBox, QDoubleSpinBox, QComboBox, QDateTimeEdit, QTimeEdit, QPlainTextEdit, QTextEdit {{
    background: {c['surface']}; border: 1px solid {c['border_strong']}; border-radius: 7px; padding: 5px 8px;
    selection-background-color: {c['selection']}; selection-color: {c['text']}; }}
QLineEdit:focus, QSpinBox:focus, QDoubleSpinBox:focus, QComboBox:focus, QPlainTextEdit:focus, QTextEdit:focus {{ border: 1px solid {c['primary']}; }}
QComboBox::drop-down {{ border: none; width: 22px; }}
QComboBox QAbstractItemView {{ background: {c['surface']}; border: 1px solid {c['border']}; selection-background-color: {c['primary_soft']}; selection-color: {c['text']}; }}
QCheckBox, QRadioButton {{ spacing: 8px; }}
QGroupBox {{ border: 1px solid {c['border']}; border-radius: 10px; margin-top: 12px; padding: 10px; background: {c['surface']}; }}
QGroupBox::title {{ subcontrol-origin: margin; left: 12px; padding: 0 4px; color: {c['text2']}; font-weight: 600; }}

/* Listeler / tablolar */
QListWidget, QTreeWidget, QTableWidget, QListView, QTreeView {{ background: {c['surface']}; border: 1px solid {c['border']};
    border-radius: 8px; outline: none; alternate-background-color: {c['surface2']}; }}
QListWidget::item, QTreeWidget::item {{ padding: 5px; border-radius: 6px; }}
QListWidget::item:selected, QTreeWidget::item:selected, QTableWidget::item:selected {{ background: {c['primary_soft']}; color: {c['text']}; }}
QListWidget::item:hover, QTreeWidget::item:hover {{ background: {c['surface2']}; }}
QHeaderView::section {{ background: {c['surface2']}; border: none; border-bottom: 1px solid {c['border']}; padding: 6px; font-weight: 600; color: {c['text2']}; }}

QProgressBar {{ border: none; background: {c['border']}; border-radius: 4px; height: 8px; text-align: center; font-size: 10px; }}
QProgressBar::chunk {{ background: {c['primary']}; border-radius: 4px; }}
QStatusBar {{ background: {c['surface']}; border-top: 1px solid {c['border']}; color: {c['text2']}; }}
QMenu {{ background: {c['surface']}; border: 1px solid {c['border']}; border-radius: 8px; padding: 6px; }}
QMenu::item {{ padding: 6px 24px 6px 10px; border-radius: 6px; }}
QMenu::item:selected {{ background: {c['primary_soft']}; color: {c['text']}; }}
QMenu::separator {{ height: 1px; background: {c['border']}; margin: 5px 8px; }}
QSplitter::handle {{ background: {c['border']}; }}
QSplitter::handle:horizontal {{ width: 1px; }}
QScrollBar:vertical {{ background: transparent; width: 11px; margin: 2px; }}
QScrollBar::handle:vertical {{ background: {c['border_strong']}; border-radius: 4px; min-height: 30px; }}
QScrollBar::handle:vertical:hover {{ background: {c['text3']}; }}
QScrollBar:horizontal {{ background: transparent; height: 11px; margin: 2px; }}
QScrollBar::handle:horizontal {{ background: {c['border_strong']}; border-radius: 4px; min-width: 30px; }}
QScrollBar::add-line, QScrollBar::sub-line {{ width: 0; height: 0; }}
QScrollBar::add-page, QScrollBar::sub-page {{ background: transparent; }}
QSlider::groove:horizontal {{ height: 4px; background: {c['border']}; border-radius: 2px; }}
QSlider::handle:horizontal {{ background: {c['primary']}; width: 14px; margin: -6px 0; border-radius: 7px; }}
QTabWidget::pane {{ border: 1px solid {c['border']}; border-radius: 8px; background: {c['surface']}; top: -1px; }}
QTabBar::tab {{ background: transparent; padding: 7px 14px; color: {c['text2']}; border-bottom: 2px solid transparent; }}
QTabBar::tab:selected {{ color: {c['primary']}; border-bottom: 2px solid {c['primary']}; font-weight: 600; }}
QWidget#DropZone {{ border: 2px dashed {c['border_strong']}; border-radius: 14px; background: {c['surface']}; }}
QWidget#DropZone[hover="true"] {{ border-color: {c['primary']}; background: {c['primary_soft']}; }}
QFrame#Toast {{ background: {c['nav']}; border-radius: 10px; }}
QFrame#Toast QLabel {{ color: #FFFFFF; }}
"""


def apply(app: QApplication, dark: bool = False):
    C.clear()
    C.update(DARK if dark else LIGHT)
    app.setStyle("Fusion")
    pal = QPalette()
    pal.setColor(QPalette.Window, QColor(C["bg"]))
    pal.setColor(QPalette.Base, QColor(C["surface"]))
    pal.setColor(QPalette.AlternateBase, QColor(C["surface2"]))
    pal.setColor(QPalette.Text, QColor(C["text"]))
    pal.setColor(QPalette.WindowText, QColor(C["text"]))
    pal.setColor(QPalette.ButtonText, QColor(C["text"]))
    pal.setColor(QPalette.Button, QColor(C["surface"]))
    pal.setColor(QPalette.Highlight, QColor(C["primary"]))
    pal.setColor(QPalette.HighlightedText, QColor("#FFFFFF"))
    pal.setColor(QPalette.PlaceholderText, QColor(C["text3"]))
    app.setPalette(pal)
    f = QFont(font_family())
    f.setPointSizeF(9.75)
    app.setFont(f)
    app.setStyleSheet(qss(C))
