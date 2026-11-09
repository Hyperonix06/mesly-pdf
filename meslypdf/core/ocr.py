"""OCR motoru (Tesseract 5 LSTM) + düzen analizi: paragraflar, tablolar, resimler, barkodlar.
Aranabilir PDF üretimi, kullanıcı sözlüğü, düşük güvenli kelime doğrulama ve düzeltme hafızası."""
from __future__ import annotations

import io
import json
import os
import shutil
import sys
from dataclasses import dataclass, field, asdict
from typing import Callable

import cv2
import numpy as np
import pytesseract
from PIL import Image

from .preprocess import PreprocessOptions, preprocess, to_cv

# ------------------------------------------------------------------ Tesseract konumu
LANG_NAMES = {
    "tur": "Türkçe", "eng": "İngilizce", "deu": "Almanca", "fra": "Fransızca", "spa": "İspanyolca",
    "ita": "İtalyanca", "rus": "Rusça", "ara": "Arapça", "fas": "Farsça", "aze": "Azerbaycanca",
    "kaz": "Kazakça", "uzb": "Özbekçe", "kur": "Kürtçe", "ell": "Yunanca", "bul": "Bulgarca",
    "nld": "Felemenkçe", "por": "Portekizce", "pol": "Lehçe", "chi_sim": "Çince (Basit)",
    "chi_tra": "Çince (Geleneksel)", "jpn": "Japonca", "kor": "Korece", "heb": "İbranice",
    "hin": "Hintçe", "ukr": "Ukraynaca", "ron": "Romence", "swe": "İsveççe", "ces": "Çekçe",
    "hun": "Macarca", "osd": "Yön algılama", "ind": "Endonezce", "vie": "Vietnamca", "tha": "Tayca",
    "deu_latf": "Almanca (Gotik/Fraktur)", "ota": "Osmanlıca", "lat": "Latince", "frk": "Fraktur",
}
TESSDATA_URL = "https://github.com/tesseract-ocr/tessdata_fast/raw/main/{lang}.traineddata"


def app_data_dir() -> str:
    base = os.environ.get("APPDATA") or os.path.join(os.path.expanduser("~"), ".config")
    d = os.path.join(base, "MeslyPDF")
    os.makedirs(d, exist_ok=True)
    return d


def _configure_tesseract():
    """Windows'ta uygulamayla gelen tesseract.exe'yi bulur."""
    candidates = []
    base = getattr(sys, "_MEIPASS", None)
    if base:
        candidates.append(os.path.join(base, "tesseract", "tesseract.exe"))
    exe_dir = os.path.dirname(sys.executable)
    candidates += [os.path.join(exe_dir, "tesseract", "tesseract.exe"),
                   r"C:\Program Files\Tesseract-OCR\tesseract.exe",
                   r"C:\Program Files (x86)\Tesseract-OCR\tesseract.exe"]
    for c in candidates:
        if os.path.exists(c):
            pytesseract.pytesseract.tesseract_cmd = c
            td = os.path.join(os.path.dirname(c), "tessdata")
            user_td = os.path.join(app_data_dir(), "tessdata")
            # kullanıcı dil paketleri varsa onları da kullanabilmek için birleşik klasör
            if os.path.isdir(user_td) and os.listdir(user_td):
                for f in os.listdir(td):
                    dst = os.path.join(user_td, f)
                    if not os.path.exists(dst):
                        try:
                            shutil.copy(os.path.join(td, f), dst)
                        except Exception:
                            pass
                os.environ["TESSDATA_PREFIX"] = user_td
            else:
                os.environ.setdefault("TESSDATA_PREFIX", td)
            return
    if not shutil.which("tesseract"):
        return


_configure_tesseract()


def available_languages() -> list[str]:
    try:
        return sorted(l for l in pytesseract.get_languages(config="") if l not in ("osd", "equ"))
    except Exception:
        return []


def tesseract_ok() -> bool:
    try:
        pytesseract.get_tesseract_version()
        return True
    except Exception:
        return False


def download_language(lang: str, progress: Callable[[int], None] | None = None) -> str:
    """Ek OCR dil paketini GitHub tessdata_fast deposundan indirir."""
    import urllib.request
    td = os.environ.get("TESSDATA_PREFIX") or os.path.join(app_data_dir(), "tessdata")
    os.makedirs(td, exist_ok=True)
    dst = os.path.join(td, f"{lang}.traineddata")
    with urllib.request.urlopen(TESSDATA_URL.format(lang=lang), timeout=60) as r, open(dst + ".part", "wb") as f:
        total = int(r.headers.get("Content-Length", 0))
        done = 0
        while True:
            chunk = r.read(65536)
            if not chunk:
                break
            f.write(chunk)
            done += len(chunk)
            if progress and total:
                progress(int(done * 100 / total))
    os.replace(dst + ".part", dst)
    return dst


# ------------------------------------------------------------------ veri modeli
@dataclass
class Word:
    text: str
    box: tuple  # piksel x0,y0,x1,y1
    conf: float = 100.0


@dataclass
class Line:
    words: list
    box: tuple

    @property
    def text(self):
        return " ".join(w.text for w in self.words)


@dataclass
class Block:
    kind: str                    # text, table, picture, barcode
    box: tuple
    lines: list = field(default_factory=list)
    cells: list = field(default_factory=list)   # tablo: [[{'text','box','rowspan','colspan'}]]
    value: str = ""              # barkod içeriği
    font_px: float = 0.0
    heading: bool = False

    @property
    def text(self):
        if self.kind == "table":
            return "\n".join("\t".join(c["text"] for c in row) for row in self.cells)
        if self.kind == "barcode":
            return self.value
        return "\n".join(l.text for l in self.lines)


@dataclass
class PageResult:
    width: int
    height: int
    dpi: int
    blocks: list = field(default_factory=list)
    lang: str = ""
    rotated: int = 0
    skew: float = 0.0
    image: Image.Image | None = None

    @property
    def text(self):
        return "\n\n".join(b.text for b in self.blocks if b.text)

    def words(self):
        for b in self.blocks:
            if b.kind == "table":
                for row in b.cells:
                    for c in row:
                        for w in c.get("words", []):
                            yield w
            for l in b.lines:
                for w in l.words:
                    yield w

    def low_confidence(self, threshold: float = 60) -> list[Word]:
        return [w for w in self.words() if w.conf < threshold]

    def to_dict(self):
        d = {"width": self.width, "height": self.height, "dpi": self.dpi, "lang": self.lang,
             "rotated": self.rotated, "skew": self.skew, "blocks": []}
        for b in self.blocks:
            bd = {"kind": b.kind, "box": list(b.box), "value": b.value, "font_px": b.font_px, "heading": b.heading,
                  "lines": [{"box": list(l.box), "words": [asdict(w) for w in l.words]} for l in b.lines],
                  "cells": [[{k: v for k, v in c.items() if k != "words"} | {"words": [asdict(w) for w in c.get("words", [])]}
                             for c in row] for row in b.cells]}
            d["blocks"].append(bd)
        return d

    @staticmethod
    def from_dict(d) -> "PageResult":
        pr = PageResult(d["width"], d["height"], d["dpi"], lang=d.get("lang", ""), rotated=d.get("rotated", 0),
                        skew=d.get("skew", 0))
        for bd in d["blocks"]:
            lines = [Line([Word(w["text"], tuple(w["box"]), w["conf"]) for w in l["words"]], tuple(l["box"]))
                     for l in bd["lines"]]
            cells = [[dict(c, words=[Word(w["text"], tuple(w["box"]), w["conf"]) for w in c.get("words", [])])
                      for c in row] for row in bd["cells"]]
            pr.blocks.append(Block(bd["kind"], tuple(bd["box"]), lines, cells, bd.get("value", ""),
                                   bd.get("font_px", 0), bd.get("heading", False)))
        return pr


# ------------------------------------------------------------------ kullanıcı sözlüğü / düzeltme hafızası
def user_dict_path() -> str:
    return os.path.join(app_data_dir(), "kullanici_sozlugu.txt")


def corrections_path() -> str:
    return os.path.join(app_data_dir(), "duzeltme_hafizasi.json")


def add_user_words(words: list[str]):
    p = user_dict_path()
    existing = set(open(p, encoding="utf-8").read().split()) if os.path.exists(p) else set()
    existing.update(w.strip() for w in words if w.strip())
    with open(p, "w", encoding="utf-8") as f:
        f.write("\n".join(sorted(existing)))


def learn_correction(wrong: str, right: str):
    p = corrections_path()
    data = json.load(open(p, encoding="utf-8")) if os.path.exists(p) else {}
    data[wrong] = right
    json.dump(data, open(p, "w", encoding="utf-8"), ensure_ascii=False, indent=1)


def _corrections() -> dict:
    p = corrections_path()
    try:
        return json.load(open(p, encoding="utf-8")) if os.path.exists(p) else {}
    except Exception:
        return {}


# ------------------------------------------------------------------ analiz
def _tess_config(psm: int = 3) -> str:
    cfg = f"--oem 1 --psm {psm} -c preserve_interword_spaces=1"
    if os.path.exists(user_dict_path()):
        cfg += f' --user-words "{user_dict_path()}"'
    return cfg


def detect_language(img: Image.Image, candidates: list[str] | None = None) -> str:
    """Birkaç dili deneyip en yüksek ortalama güveni veren dili seçer."""
    langs = available_languages()
    candidates = [c for c in (candidates or ["tur", "eng", "deu", "fra", "rus", "ara", "spa", "ita"]) if c in langs]
    if not candidates:
        return "eng"
    sample = img.copy()
    sample.thumbnail((1600, 1600))
    w, h = sample.size
    sample = sample.crop((0, int(h * 0.1), w, int(h * 0.6)))
    best, score = candidates[0], -1
    for lg in candidates:
        try:
            d = pytesseract.image_to_data(sample, lang=lg, config="--oem 1 --psm 3", output_type=pytesseract.Output.DICT)
            confs = [float(c) for c, t in zip(d["conf"], d["text"]) if t.strip() and float(c) >= 0]
            s = (sum(confs) / len(confs)) if confs else 0
            s += min(len(confs), 50) * 0.05
            if s > score:
                best, score = lg, s
        except Exception:
            continue
    return best


def detect_tables(img: Image.Image) -> list[dict]:
    """Çizgili tabloları bulur: [{'box':(x0,y0,x1,y1), 'rows':[y...], 'cols':[x...]}]"""
    g = cv2.cvtColor(to_cv(img), cv2.COLOR_BGR2GRAY)
    th = cv2.adaptiveThreshold(~g, 255, cv2.ADAPTIVE_THRESH_MEAN_C, cv2.THRESH_BINARY, 15, -2)
    h, w = th.shape
    hk = cv2.getStructuringElement(cv2.MORPH_RECT, (max(20, w // 30), 1))
    vk = cv2.getStructuringElement(cv2.MORPH_RECT, (1, max(20, h // 50)))
    horiz = cv2.dilate(cv2.erode(th, hk), hk)
    vert = cv2.dilate(cv2.erode(th, vk), vk)
    grid = cv2.add(horiz, vert)
    cnts, _ = cv2.findContours(grid, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    tables = []
    for c in cnts:
        x, y, cw, ch = cv2.boundingRect(c)
        if cw < w * 0.15 or ch < 30:
            continue
        hs = horiz[y:y + ch, x:x + cw]
        vs = vert[y:y + ch, x:x + cw]
        rows = _line_positions(hs.sum(axis=1), y)
        cols = _line_positions(vs.sum(axis=0), x)
        if len(rows) >= 2 and len(cols) >= 2 and (len(rows) >= 3 or len(cols) >= 3):
            tables.append({"box": (x, y, x + cw, y + ch), "rows": rows, "cols": cols})
    return tables


def _line_positions(profile, offset):
    profile = np.asarray(profile, dtype=np.float64)
    if profile.max() <= 0:
        return []
    mask = profile > profile.max() * 0.35
    pos, start = [], None
    for i, m in enumerate(mask):
        if m and start is None:
            start = i
        elif not m and start is not None:
            pos.append(offset + (start + i - 1) // 2)
            start = None
    if start is not None:
        pos.append(offset + (start + len(mask) - 1) // 2)
    return pos


def detect_pictures(img: Image.Image, text_boxes: list[tuple], table_boxes: list[tuple]) -> list[tuple]:
    g = cv2.cvtColor(to_cv(img), cv2.COLOR_BGR2GRAY)
    h, w = g.shape
    hsv = cv2.cvtColor(to_cv(img), cv2.COLOR_BGR2HSV)
    nonwhite = ((g < 200) | (hsv[:, :, 1] > 60)).astype(np.uint8) * 255
    for (x0, y0, x1, y1) in list(text_boxes) + list(table_boxes):
        cv2.rectangle(nonwhite, (int(x0) - 3, int(y0) - 3), (int(x1) + 3, int(y1) + 3), 0, -1)
    nonwhite = cv2.morphologyEx(nonwhite, cv2.MORPH_CLOSE, np.ones((25, 25), np.uint8))
    cnts, _ = cv2.findContours(nonwhite, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    pics = []
    for c in cnts:
        x, y, cw, ch = cv2.boundingRect(c)
        if cw * ch > w * h * 0.012 and cw > 60 and ch > 60:
            region = g[y:y + ch, x:x + cw]
            if float(np.std(region)) > 18:  # düz çizgi/gürültü değil
                pics.append((x, y, x + cw, y + ch))
    return pics


def detect_barcodes(img: Image.Image) -> list[dict]:
    try:
        import zxingcpp
        res = []
        for r in zxingcpp.read_barcodes(img):
            p = r.position
            xs = [p.top_left.x, p.top_right.x, p.bottom_left.x, p.bottom_right.x]
            ys = [p.top_left.y, p.top_right.y, p.bottom_left.y, p.bottom_right.y]
            res.append({"value": r.text, "format": str(r.format).split(".")[-1], "box": (min(xs), min(ys), max(xs), max(ys))})
        return res
    except Exception:
        return []


def _inside(box, outer, tol=2):
    cx, cy = (box[0] + box[2]) / 2, (box[1] + box[3]) / 2
    return outer[0] - tol <= cx <= outer[2] + tol and outer[1] - tol <= cy <= outer[3] + tol


_TR_BASE = str.maketrans("çğıöşüÇĞİÖŞÜâîûÂÎÛ", "cgiosuCGIOSUaiuAIU")
_TR_CHARS = set("çğıöşüÇĞİÖŞÜ")


def _base(t: str) -> str:
    return t.translate(_TR_BASE).replace("I", "i").lower()


def _raw_words(img: Image.Image, lang: str, psm: int) -> list[dict]:
    d = pytesseract.image_to_data(img, lang=lang, config=_tess_config(psm), output_type=pytesseract.Output.DICT)
    out = []
    for i in range(len(d["text"])):
        t = d["text"][i].strip()
        if not t:
            continue
        conf = float(d["conf"][i])
        if conf < 0:
            continue
        x, y, w, h = d["left"][i], d["top"][i], d["width"][i], d["height"][i]
        out.append({"text": t, "box": (x, y, x + w, y + h), "conf": conf, "block": d["block_num"][i],
                    "par": d["par_num"][i], "line": d["line_num"][i]})
    return out


def _iou(a, b) -> float:
    ix = max(0, min(a[2], b[2]) - max(a[0], b[0]))
    iy = max(0, min(a[3], b[3]) - max(a[1], b[1]))
    inter = ix * iy
    u = (a[2] - a[0]) * (a[3] - a[1]) + (b[2] - b[0]) * (b[3] - b[1]) - inter
    return inter / u if u else 0.0


def _ocr_words(img: Image.Image, lang: str, psm: int = 3) -> list[dict]:
    """Kelimeleri tanır. Türkçe başka dillerle birlikte seçildiğinde, karma modelin Türkçe harfleri
    (Ç Ğ İ Ö Ş Ü ı) kaybettiği kelimeler yalnızca-Türkçe geçişin sonucuyla düzeltilir."""
    words = _raw_words(img, lang, psm)
    langs = lang.split("+")
    if "tur" in langs and len(langs) > 1:
        tr = _raw_words(img, "tur", psm)
        for w in words:
            best, score = None, 0.0
            for t in tr:
                sc = _iou(w["box"], t["box"])
                if sc > score:
                    best, score = t, sc
            if best and score > 0.5 and best["text"] != w["text"] and _TR_CHARS & set(best["text"]) \
                    and _base(best["text"]) == _base(w["text"]):
                w["text"] = best["text"]
                w["conf"] = max(w["conf"], best["conf"])
    corr = _corrections()
    if corr:
        for w in words:
            w["text"] = corr.get(w["text"], w["text"])
    return words


def _build_text_blocks(words: list[dict]) -> list[Block]:
    groups: dict[tuple, dict[int, list]] = {}
    for w in words:
        groups.setdefault((w["block"], w["par"]), {}).setdefault(w["line"], []).append(w)
    blocks = []
    for key in groups:
        lines = []
        for ln in sorted(groups[key]):
            ws = sorted(groups[key][ln], key=lambda z: z["box"][0])
            wobjs = [Word(z["text"], z["box"], z["conf"]) for z in ws]
            box = (min(z["box"][0] for z in ws), min(z["box"][1] for z in ws),
                   max(z["box"][2] for z in ws), max(z["box"][3] for z in ws))
            lines.append(Line(wobjs, box))
        if not lines:
            continue
        box = (min(l.box[0] for l in lines), min(l.box[1] for l in lines),
               max(l.box[2] for l in lines), max(l.box[3] for l in lines))
        hs = sorted(l.box[3] - l.box[1] for l in lines)
        blocks.append(Block("text", box, lines, font_px=hs[len(hs) // 2]))
    if blocks:
        # gövde yazı boyutu: karakter sayısına göre ağırlıklı ortanca (başlık sayısından etkilenmez)
        pairs = sorted((b.font_px, sum(len(l.text) for l in b.lines)) for b in blocks)
        total = sum(n for _, n in pairs)
        acc, med = 0, pairs[0][0]
        for px, n in pairs:
            acc += n
            if acc >= total / 2:
                med = px
                break
        for b in blocks:
            b.heading = b.font_px > med * 1.3 and len(b.lines) <= 3
    return blocks


def recognize_image(img: Image.Image, lang: str = "tur+eng", dpi: int = 300, detect_layout: bool = True,
                    opts: PreprocessOptions | None = None, areas: list[dict] | None = None,
                    already_preprocessed: bool = False) -> PageResult:
    """Tek görüntüyü tanır. areas verilirse (OCR düzenleyicide elle çizilen alanlar)
    yalnızca o alanlar belirtilen türlerle tanınır: [{'kind':'text|table|picture|barcode','box':(...)}]"""
    info = {"rotated": 0, "skew": 0.0}
    if not already_preprocessed and opts is not None:
        imgs, info = preprocess(img, opts, dpi)
        img = imgs[0]
    if lang == "auto":
        lang = detect_language(img)
    W, H = img.size
    res = PageResult(W, H, dpi, lang=lang, rotated=info.get("rotated", 0), skew=info.get("skew", 0), image=img)

    if areas:
        for a in areas:
            box = tuple(int(v) for v in a["box"])
            crop = img.crop(box)
            kind = a["kind"]
            if kind == "picture":
                res.blocks.append(Block("picture", box))
            elif kind == "barcode":
                bc = detect_barcodes(crop)
                res.blocks.append(Block("barcode", box, value=bc[0]["value"] if bc else ""))
            elif kind == "table":
                tbs = detect_tables(crop)
                tb = tbs[0] if tbs else _uniform_table(crop, a.get("rows", 0), a.get("cols", 0))
                words = _ocr_words(crop, lang, 6)
                blk = _make_table(tb, words)
                _shift_block(blk, box[0], box[1])
                res.blocks.append(blk)
            else:
                words = _ocr_words(crop, lang, 6)
                for b in _build_text_blocks(words):
                    _shift_block(b, box[0], box[1])
                    res.blocks.append(b)
        return res

    tables = detect_tables(img) if detect_layout else []
    barcodes = detect_barcodes(img) if detect_layout else []
    words = _ocr_words(img, lang, 3)
    table_blocks = []
    for tb in tables:
        inside = [w for w in words if _inside(w["box"], tb["box"])]
        words = [w for w in words if not _inside(w["box"], tb["box"])]
        # Tablo hücrelerini ayrı ayrı tanımak daha doğru
        cell_words = []
        rows, cols = tb["rows"], tb["cols"]
        for r in range(len(rows) - 1):
            for c in range(len(cols) - 1):
                x0, y0, x1, y1 = cols[c] + 3, rows[r] + 3, cols[c + 1] - 3, rows[r + 1] - 3
                if x1 - x0 < 8 or y1 - y0 < 8:
                    continue
                crop = img.crop((x0, y0, x1, y1))
                for w in _ocr_words(crop, lang, 6):
                    b = w["box"]
                    w["box"] = (b[0] + x0, b[1] + y0, b[2] + x0, b[3] + y0)
                    cell_words.append(w)
        table_blocks.append(_make_table(tb, cell_words or inside))
    for bc in barcodes:
        words = [w for w in words if not _inside(w["box"], bc["box"])]
    text_blocks = _build_text_blocks(words)
    pics = detect_pictures(img, [b.box for b in text_blocks] + [bc["box"] for bc in barcodes],
                           [t["box"] for t in tables]) if detect_layout else []
    blocks = text_blocks + table_blocks + [Block("picture", p) for p in pics] + \
        [Block("barcode", bc["box"], value=bc["value"]) for bc in barcodes]
    res.blocks = order_blocks(blocks, W)
    return res


def _uniform_table(img, rows, cols):
    w, h = img.size
    rows = rows or 2
    cols = cols or 2
    return {"box": (0, 0, w, h), "rows": [int(h * i / rows) for i in range(rows + 1)],
            "cols": [int(w * i / cols) for i in range(cols + 1)]}


def _make_table(tb, words) -> Block:
    rows, cols = tb["rows"], tb["cols"]
    cells = []
    for r in range(len(rows) - 1):
        row = []
        for c in range(len(cols) - 1):
            box = (cols[c], rows[r], cols[c + 1], rows[r + 1])
            ws = sorted([w for w in words if _inside(w["box"], box, 0)], key=lambda z: (z["box"][1] // 10, z["box"][0]))
            row.append({"text": " ".join(w["text"] for w in ws), "box": box, "rowspan": 1, "colspan": 1,
                        "words": [Word(w["text"], w["box"], w["conf"]) for w in ws]})
        cells.append(row)
    return Block("table", tb["box"], cells=cells)


def _shift_block(b: Block, dx, dy):
    sh = lambda bx: (bx[0] + dx, bx[1] + dy, bx[2] + dx, bx[3] + dy)
    b.box = sh(b.box)
    for l in b.lines:
        l.box = sh(l.box)
        for w in l.words:
            w.box = sh(w.box)
    for row in b.cells:
        for c in row:
            c["box"] = sh(c["box"])
            for w in c.get("words", []):
                w.box = sh(w.box)


def order_blocks(blocks: list[Block], page_width: int) -> list[Block]:
    """Okuma sırası: sütunları algılayıp önce soldaki sütun yukarıdan aşağı."""
    if not blocks:
        return blocks
    mid = page_width / 2
    full, left, right = [], [], []
    for b in blocks:
        bw = b.box[2] - b.box[0]
        if bw > page_width * 0.55 or (b.box[0] < mid < b.box[2]):
            full.append(b)
        elif b.box[2] <= mid + page_width * 0.02:
            left.append(b)
        else:
            right.append(b)
    two_col = len(left) >= 2 and len(right) >= 2
    if not two_col:
        return sorted(blocks, key=lambda b: (b.box[1], b.box[0]))
    # tam genişlikli bloklar bölücü görevi görür
    out = []
    bands = sorted(full, key=lambda b: b.box[1])
    prev_y = -1
    for f in bands + [None]:
        y_lim = f.box[1] if f else 10 ** 9
        seg_l = sorted([b for b in left if prev_y <= b.box[1] < y_lim], key=lambda b: b.box[1])
        seg_r = sorted([b for b in right if prev_y <= b.box[1] < y_lim], key=lambda b: b.box[1])
        out += seg_l + seg_r
        if f:
            out.append(f)
            prev_y = f.box[1]
    return out


# ------------------------------------------------------------------ aranabilir PDF
def _text_layer_canvas(c, result: PageResult, page_w: float, page_h: float, visible: bool = False,
                       tagged: bool = False):
    """Görünmez (render mode 3) metin katmanını kelime kelime, kutuya sığacak şekilde yazar."""
    from .fonts import font_name
    fn = font_name()
    sx, sy = page_w / result.width, page_h / result.height
    mcid = 0
    lines = []
    for b in result.blocks:
        if b.kind == "table":
            for row in b.cells:
                for cell in row:
                    if cell.get("words"):
                        lines.append(cell["words"])
        else:
            for l in b.lines:
                lines.append(l.words)
    for words in lines:
        if tagged:
            c._code.append(f"/P <</MCID {mcid}>> BDC")
        for w in words:
            x0, y0, x1, y1 = w.box
            hpt = (y1 - y0) * sy
            wpt = (x1 - x0) * sx
            if hpt <= 0 or wpt <= 0 or not w.text:
                continue
            size = max(1.0, hpt * 0.95)
            t = c.beginText()
            t.setTextRenderMode(0 if visible else 3)
            t.setFont(fn, size)
            natural = c.stringWidth(w.text + " ", fn, size)
            t.setHorizScale(max(1, min(1000, 100.0 * (wpt + size * 0.25) / natural)) if natural else 100)
            t.setTextOrigin(x0 * sx, page_h - y1 * sy + hpt * 0.18)
            t.textOut(w.text + " ")
            c.drawText(t)
        if tagged:
            c._code.append("EMC")
            mcid += 1
    return mcid


def image_to_searchable_pdf_page(img: Image.Image, page_w: float, page_h: float, lang: str = "tur+eng",
                                 result: PageResult | None = None, jpeg_quality: int = 80) -> bytes:
    from reportlab.pdfgen import canvas
    from reportlab.lib.utils import ImageReader
    result = result or recognize_image(img, lang, detect_layout=False)
    buf = io.BytesIO()
    c = canvas.Canvas(buf, pagesize=(page_w, page_h))
    jb = io.BytesIO()
    img.convert("RGB").save(jb, "JPEG", quality=jpeg_quality)
    jb.seek(0)
    c.drawImage(ImageReader(jb), 0, 0, page_w, page_h)
    _text_layer_canvas(c, result, page_w, page_h)
    c.showPage()
    c.save()
    return buf.getvalue()


def ocr_pdf(path: str, out: str, lang: str = "tur+eng", dpi: int = 300, pages: list[int] | None = None,
            skip_text_pages: bool = True, opts: PreprocessOptions | None = None, mode: str = "under",
            progress: Callable[[int, int], None] | None = None, cancel: Callable[[], bool] | None = None,
            password: str | None = None, results_out: dict | None = None) -> str:
    """PDF'e görünmez metin katmanı ekleyerek aranabilir yapar.
    mode: 'under' (orijinal görüntü korunur, metin altta), 'replace' (ön işlenmiş görüntü ile değiştir)."""
    import pikepdf
    from .document import PdfDocument
    from .editing import overlay
    opts = opts if opts is not None else PreprocessOptions(auto_orient=False, deskew=False, whiten_background=False)
    results: dict[int, PageResult] = {}
    with PdfDocument(path, password) as doc:
        idx = list(range(doc.page_count) if pages is None else pages)
        for n, i in enumerate(idx):
            if cancel and cancel():
                raise InterruptedError("İşlem iptal edildi")
            if progress:
                progress(n, len(idx))
            if skip_text_pages and len(doc.page_text(i).strip()) > 20:
                continue
            img = doc.render_dpi(i, dpi)
            if mode == "replace":
                imgs, info = preprocess(img, opts, dpi)
                img = imgs[0]
            lg = detect_language(img) if lang == "auto" else lang
            results[i] = recognize_image(img, lg, dpi, detect_layout=False)
    if progress:
        progress(len(idx), len(idx))
    if results_out is not None:
        results_out.update(results)
    if not results:
        shutil.copyfile(path, out)
        return out
    if mode == "replace":
        with pikepdf.open(path, password=password or "", allow_overwriting_input=True) as pdf:
            for i, r in results.items():
                p = pdf.pages[i]
                mb = [float(v) for v in p.mediabox]
                rot = int(p.obj.get("/Rotate", 0)) % 360
                w, h = mb[2] - mb[0], mb[3] - mb[1]
                if rot in (90, 270):
                    w, h = h, w
                data = image_to_searchable_pdf_page(r.image, w, h, r.lang, r)
                src = pikepdf.open(io.BytesIO(data))
                pdf.pages.insert(i, src.pages[0])
                del pdf.pages[i + 1]
            pdf.save(out)
        return out

    def draw(c, i, n, w, h):
        _text_layer_canvas(c, results[i], w, h)
    return overlay(path, out, draw, sorted(results), mark="ocr", password=password)


def ocr_images_to_pdf(images: list[str], out: str, lang: str = "tur+eng", opts: PreprocessOptions | None = None,
                      progress=None, cancel=None, page_size: str = "auto") -> str:
    """Görüntülerden (tarama/fotoğraf) aranabilir PDF."""
    import pikepdf
    opts = opts or PreprocessOptions()
    dst = pikepdf.new()
    total = len(images)
    for n, path in enumerate(images):
        if cancel and cancel():
            raise InterruptedError("İşlem iptal edildi")
        if progress:
            progress(n, total)
        src_img = Image.open(path)
        dpi = int((src_img.info.get("dpi") or (300, 300))[0] or 300)
        frames = []
        try:
            k = 0
            while True:
                src_img.seek(k)
                frames.append(src_img.convert("RGB").copy())
                k += 1
        except EOFError:
            pass
        for fr in frames:
            pages_img, _ = preprocess(fr, opts, dpi)
            for pim in pages_img:
                eff_dpi = dpi * (pim.width / fr.width) if fr.width else dpi
                eff_dpi = max(eff_dpi, 72)
                if page_size == "a4":
                    w, h = (595.28, 841.89) if pim.height >= pim.width else (841.89, 595.28)
                else:
                    w, h = pim.width * 72 / eff_dpi, pim.height * 72 / eff_dpi
                lg = detect_language(pim) if lang == "auto" else lang
                r = recognize_image(pim, lg, int(eff_dpi), detect_layout=False)
                data = image_to_searchable_pdf_page(pim, w, h, lg, r)
                with pikepdf.open(io.BytesIO(data)) as s:
                    dst.pages.extend(s.pages)
    if progress:
        progress(total, total)
    dst.save(out)
    return out


def recognize_pdf(path: str, lang: str = "tur+eng", dpi: int = 300, pages: list[int] | None = None,
                  opts: PreprocessOptions | None = None, use_text_layer: bool = True, progress=None, cancel=None,
                  password: str | None = None) -> list[PageResult]:
    """Belgeyi düzen analiziyle tanır (dönüştürme ve OCR düzenleyici için).
    Metin katmanı olan sayfalarda OCR yerine PDF'in kendi metni kullanılır (daha doğru)."""
    from .document import PdfDocument
    out = []
    with PdfDocument(path, password) as doc:
        idx = list(range(doc.page_count) if pages is None else pages)
        for n, i in enumerate(idx):
            if cancel and cancel():
                raise InterruptedError("İşlem iptal edildi")
            if progress:
                progress(n, len(idx))
            img = doc.render_dpi(i, dpi)
            if use_text_layer and len(doc.page_text(i).strip()) > 20:
                out.append(page_from_text_layer(doc, i, img, dpi))
            else:
                if opts:
                    img = preprocess(img, opts, dpi)[0][0]
                lg = detect_language(img) if lang == "auto" else lang
                out.append(recognize_image(img, lg, dpi))
    if progress:
        progress(len(idx), len(idx))
    return out


def page_from_text_layer(doc, i: int, img: Image.Image, dpi: int) -> PageResult:
    """Dijital PDF sayfasından (OCR'sız) aynı veri modelini üretir."""
    sc = dpi / 72.0
    pw, ph = doc.page_size(i)
    words = []
    for k, (t, (l, b, r, tp)) in enumerate(doc.words(i)):
        box = (l * sc, (ph - tp) * sc, r * sc, (ph - b) * sc)
        words.append({"text": t, "box": box, "conf": 100.0})
    # satırlara grupla: dikey örtüşmeye göre kümele
    words.sort(key=lambda w: ((w["box"][1] + w["box"][3]) / 2, w["box"][0]))
    rows: list[list[dict]] = []
    for w in words:
        y0, y1 = w["box"][1], w["box"][3]
        for row in reversed(rows[-3:]):
            ry0 = min(z["box"][1] for z in row)
            ry1 = max(z["box"][3] for z in row)
            ov = min(y1, ry1) - max(y0, ry0)
            if ov > 0.5 * min(y1 - y0, ry1 - ry0):
                row.append(w)
                break
        else:
            rows.append([w])
    lines: list[list[dict]] = []
    for row in rows:
        row.sort(key=lambda z: z["box"][0])
        seg = [row[0]]
        for w in row[1:]:
            h = max(1, w["box"][3] - w["box"][1])
            if w["box"][0] - seg[-1]["box"][2] > h * 4:  # sütun boşluğu
                lines.append(seg)
                seg = [w]
            else:
                seg.append(w)
        lines.append(seg)
    lines.sort(key=lambda ln: (min(z["box"][1] for z in ln), min(z["box"][0] for z in ln)))
    # paragraflara grupla
    blocks_words = []
    for ln in lines:
        lb = (min(z["box"][0] for z in ln), min(z["box"][1] for z in ln), max(z["box"][2] for z in ln), max(z["box"][3] for z in ln))
        h = lb[3] - lb[1]
        if blocks_words:
            prev = blocks_words[-1]
            pb = prev[-1][1]
            ph_ = pb[3] - pb[1]
            gap = lb[1] - pb[3]
            if abs(h - ph_) < max(2, h * 0.2) and -h * 0.5 < gap < h * 0.9 and abs(lb[0] - prev[0][1][0]) < h * 4:
                prev.append((ln, lb))
                continue
        blocks_words.append([(ln, lb)])
    flat = []
    for bi, bl in enumerate(blocks_words):
        for li, (ln, _) in enumerate(bl):
            for w in ln:
                flat.append(dict(w, block=bi, par=0, line=li))
    tables = detect_tables(img)
    table_blocks = []
    for tb in tables:
        inside = [w for w in flat if _inside(w["box"], tb["box"])]
        flat = [w for w in flat if not _inside(w["box"], tb["box"])]
        table_blocks.append(_make_table(tb, inside))
    text_blocks = _build_text_blocks(flat)
    pics = _pdf_image_boxes(doc, i, sc, ph)
    res = PageResult(img.width, img.height, dpi, lang="pdf", image=img)
    res.blocks = order_blocks(text_blocks + table_blocks + [Block("picture", p) for p in pics], img.width)
    return res


def _pdf_image_boxes(doc, i, sc, ph) -> list[tuple]:
    import pypdfium2.raw as pdfium_c
    boxes = []
    page = doc._doc[i]
    try:
        for o in page.get_objects(filter=[pdfium_c.FPDF_PAGEOBJ_IMAGE], max_depth=3):
            l, b, r, t = o.get_bounds()
            if (r - l) > 20 and (t - b) > 20:
                boxes.append((l * sc, (ph - t) * sc, r * sc, (ph - b) * sc))
    finally:
        del page
    return boxes
