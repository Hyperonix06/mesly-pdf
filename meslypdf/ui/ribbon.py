"""Şerit (ribbon) araç çubuğu: sekmeler, gruplar, büyük simgeli düğmeler."""
from __future__ import annotations

from PySide6.QtCore import Qt, QSize, Signal
from PySide6.QtWidgets import (QWidget, QHBoxLayout, QVBoxLayout, QToolButton, QTabBar, QStackedWidget, QFrame,
                               QLabel, QMenu, QButtonGroup, QScrollArea, QSizePolicy)

from . import theme


class _HScroll(QScrollArea):
    """Dar pencerede şeridi fare tekerleğiyle yatay kaydırır (çubuk göstermeden)."""

    def wheelEvent(self, e):
        bar = self.horizontalScrollBar()
        if bar.maximum() > 0:
            bar.setValue(bar.value() - e.angleDelta().y())
            e.accept()
        else:
            super().wheelEvent(e)


class RibbonPage(QWidget):
    def __init__(self):
        super().__init__()
        self.lay = QHBoxLayout(self)
        self.lay.setContentsMargins(8, 4, 8, 4)
        self.lay.setSpacing(2)
        self.lay.addStretch()
        self._group_lay = None

    def group(self, title: str):
        if self.lay.count() > 1:
            sep = QFrame()
            sep.setObjectName("RibbonSep")
            self.lay.insertWidget(self.lay.count() - 1, sep)
        box = QWidget()
        v = QVBoxLayout(box)
        v.setContentsMargins(2, 0, 2, 0)
        v.setSpacing(0)
        row = QHBoxLayout()
        row.setSpacing(1)
        v.addLayout(row)
        t = QLabel(title)
        t.setObjectName("RibbonGroup")
        t.setAlignment(Qt.AlignHCenter)
        v.addWidget(t)
        self.lay.insertWidget(self.lay.count() - 1, box)
        self._group_lay = row
        return self

    def add(self, text: str, icon_name: str, slot=None, tip: str = "", checkable=False, menu: list | None = None,
            color: str | None = None) -> QToolButton:
        b = QToolButton()
        b.setObjectName("RibbonButton")
        b.setText(text)
        b.setIcon(theme.icon(icon_name, color))
        b.setIconSize(QSize(22, 22))
        b.setToolButtonStyle(Qt.ToolButtonTextUnderIcon)
        fw = b.fontMetrics().horizontalAdvance(text) + 14
        b.setMinimumWidth(max(52, fw))
        b.setToolTip(tip or text)
        b.setCheckable(checkable)
        b.setCursor(Qt.PointingHandCursor)
        if menu:
            m = QMenu(b)
            for item in menu:
                if item is None:
                    m.addSeparator()
                    continue
                label, ic, fn = item
                a = m.addAction(theme.icon(ic) if ic else theme.icon("mdi6.circle-small"), label)
                a.triggered.connect(fn)
            b.setMenu(m)
            b.setPopupMode(QToolButton.InstantPopup)
        elif slot:
            b.clicked.connect(slot)
        self._group_lay.addWidget(b)
        return b

    def add_widget(self, w):
        self._group_lay.addWidget(w)
        return w


class Ribbon(QWidget):
    def __init__(self):
        super().__init__()
        self.setObjectName("Ribbon")
        self.setAttribute(Qt.WA_StyledBackground, True)
        v = QVBoxLayout(self)
        v.setContentsMargins(0, 0, 0, 0)
        v.setSpacing(0)
        self.tabs = QTabBar()
        self.tabs.setObjectName("RibbonTabs")
        self.tabs.setDrawBase(False)
        self.tabs.setExpanding(False)
        self.stack = QStackedWidget()
        self.stack.setFixedHeight(82)
        th = QHBoxLayout()
        th.setContentsMargins(8, 0, 8, 0)
        th.addWidget(self.tabs)
        th.addStretch()
        self.right = QHBoxLayout()
        th.addLayout(self.right)
        v.addLayout(th)
        v.addWidget(self.stack)
        self.tabs.currentChanged.connect(self.stack.setCurrentIndex)
        self.tool_group = QButtonGroup(self)
        self.tool_group.setExclusive(True)

    def page(self, title: str) -> RibbonPage:
        p = RibbonPage()
        sc = _HScroll()
        sc.setWidget(p)
        sc.setWidgetResizable(True)
        sc.setHorizontalScrollBarPolicy(Qt.ScrollBarAlwaysOff)
        sc.setVerticalScrollBarPolicy(Qt.ScrollBarAlwaysOff)
        sc.setFrameShape(QFrame.NoFrame)
        self.stack.addWidget(sc)
        self.tabs.addTab(title)
        return p
