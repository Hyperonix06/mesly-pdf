"""Tarama ve fotoğraf ön işleme: yön algılama, eğiklik düzeltme, arka plan beyazlatma,
gürültü giderme, yamuk (perspektif) düzeltme, ikili görüntü, kenar kırpma, iki sayfa bölme."""
from __future__ import annotations

from dataclasses import dataclass

import cv2
import numpy as np
from PIL import Image, ImageEnhance, ImageOps


@dataclass
class PreprocessOptions:
    auto_orient: bool = True
    deskew: bool = True
    whiten_background: bool = True
    denoise: bool = False
    perspective: bool = False      # fotoğraflar için yamuk düzeltme
    binarize: bool = False
    crop_borders: bool = False
    split_pages: bool = False      # kitap taramalarında çift sayfa bölme
    brightness: float = 1.0
    contrast: float = 1.0
    invert: bool = False
    resolution_fix: bool = True    # düşük çözünürlüklü görüntüleri büyüt


def to_cv(img: Image.Image) -> np.ndarray:
    return cv2.cvtColor(np.array(img.convert("RGB")), cv2.COLOR_RGB2BGR)


def to_pil(arr: np.ndarray) -> Image.Image:
    if arr.ndim == 2:
        return Image.fromarray(arr)
    return Image.fromarray(cv2.cvtColor(arr, cv2.COLOR_BGR2RGB))


def detect_orientation(img: Image.Image) -> int:
    """Tesseract OSD ile 0/90/180/270 döndürme açısını bulur (saat yönünde düzeltme)."""
    import pytesseract
    try:
        small = img.copy()
        small.thumbnail((2000, 2000))
        osd = pytesseract.image_to_osd(small, config="--psm 0 -c min_characters_to_try=5",
                                       output_type=pytesseract.Output.DICT)
        if float(osd.get("orientation_conf", 0)) < 1.5:
            return 0
        return int(osd.get("rotate", 0))
    except Exception:
        return 0


def auto_orient(img: Image.Image) -> tuple[Image.Image, int]:
    rot = detect_orientation(img)
    if rot:
        img = img.rotate(-rot, expand=True, fillcolor="white")
    return img, rot


def skew_angle(img: Image.Image) -> float:
    """Metin satırlarının eğiklik açısını (derece) tahmin eder."""
    g = cv2.cvtColor(to_cv(img), cv2.COLOR_BGR2GRAY)
    scale = 1500 / max(g.shape)
    if scale < 1:
        g = cv2.resize(g, None, fx=scale, fy=scale, interpolation=cv2.INTER_AREA)
    th = cv2.threshold(g, 0, 255, cv2.THRESH_BINARY_INV + cv2.THRESH_OTSU)[1]
    # yatay olarak bulaştır -> satırlar
    k = cv2.getStructuringElement(cv2.MORPH_RECT, (max(15, g.shape[1] // 40), 1))
    lines = cv2.dilate(th, k)
    best, best_score = 0.0, -1
    for ang in np.arange(-10, 10.01, 0.5):
        M = cv2.getRotationMatrix2D((lines.shape[1] / 2, lines.shape[0] / 2), ang, 1)
        r = cv2.warpAffine(lines, M, (lines.shape[1], lines.shape[0]), flags=cv2.INTER_NEAREST)
        prof = r.sum(axis=1).astype(np.float64)
        score = float(np.var(prof))
        if score > best_score:
            best, best_score = ang, score
    # ince ayar
    for ang in np.arange(best - 0.5, best + 0.51, 0.1):
        M = cv2.getRotationMatrix2D((lines.shape[1] / 2, lines.shape[0] / 2), ang, 1)
        r = cv2.warpAffine(lines, M, (lines.shape[1], lines.shape[0]), flags=cv2.INTER_NEAREST)
        score = float(np.var(r.sum(axis=1).astype(np.float64)))
        if score > best_score:
            best, best_score = ang, score
    return float(round(best, 2))


def deskew(img: Image.Image) -> tuple[Image.Image, float]:
    ang = skew_angle(img)
    if abs(ang) < 0.15:
        return img, 0.0
    return img.rotate(ang, resample=Image.BICUBIC, expand=False, fillcolor="white"), ang


def whiten_background(img: Image.Image) -> Image.Image:
    """Gölge ve gri/sarı arka planı beyaza çeker (bölme ile normalizasyon)."""
    arr = to_cv(img)
    out = np.empty_like(arr)
    for ch in range(3):
        c = arr[:, :, ch]
        bg = cv2.medianBlur(cv2.dilate(c, np.ones((7, 7), np.uint8)), 31)
        norm = cv2.divide(c, bg, scale=255)
        out[:, :, ch] = norm
    return to_pil(out)


def denoise(img: Image.Image) -> Image.Image:
    arr = to_cv(img)
    return to_pil(cv2.fastNlMeansDenoisingColored(arr, None, 7, 7, 7, 21))


def binarize(img: Image.Image) -> Image.Image:
    g = cv2.cvtColor(to_cv(img), cv2.COLOR_BGR2GRAY)
    th = cv2.adaptiveThreshold(g, 255, cv2.ADAPTIVE_THRESH_GAUSSIAN_C, cv2.THRESH_BINARY, 31, 15)
    return to_pil(th).convert("RGB")


def _order(pts):
    pts = pts.reshape(4, 2).astype(np.float32)
    s = pts.sum(axis=1)
    d = np.diff(pts, axis=1).ravel()
    return np.array([pts[np.argmin(s)], pts[np.argmin(d)], pts[np.argmax(s)], pts[np.argmax(d)]], dtype=np.float32)


def find_document_quad(img: Image.Image):
    arr = to_cv(img)
    scale = 1000 / max(arr.shape[:2])
    small = cv2.resize(arr, None, fx=scale, fy=scale) if scale < 1 else arr.copy()
    scale = min(scale, 1)
    g = cv2.GaussianBlur(cv2.cvtColor(small, cv2.COLOR_BGR2GRAY), (5, 5), 0)
    edges = cv2.Canny(g, 50, 150)
    edges = cv2.dilate(edges, np.ones((3, 3), np.uint8))
    cnts, _ = cv2.findContours(edges, cv2.RETR_LIST, cv2.CHAIN_APPROX_SIMPLE)
    area = small.shape[0] * small.shape[1]
    for c in sorted(cnts, key=cv2.contourArea, reverse=True)[:8]:
        peri = cv2.arcLength(c, True)
        approx = cv2.approxPolyDP(c, 0.02 * peri, True)
        if len(approx) == 4 and cv2.contourArea(approx) > area * 0.25:
            return _order(approx) / scale
    return None


def perspective_correct(img: Image.Image) -> tuple[Image.Image, bool]:
    quad = find_document_quad(img)
    if quad is None:
        return img, False
    tl, tr, br, bl = quad
    w = int(max(np.linalg.norm(br - bl), np.linalg.norm(tr - tl)))
    h = int(max(np.linalg.norm(tr - br), np.linalg.norm(tl - bl)))
    if w < 100 or h < 100:
        return img, False
    dst = np.array([[0, 0], [w - 1, 0], [w - 1, h - 1], [0, h - 1]], dtype=np.float32)
    M = cv2.getPerspectiveTransform(quad, dst)
    out = cv2.warpPerspective(to_cv(img), M, (w, h), borderValue=(255, 255, 255))
    return to_pil(out), True


def crop_borders(img: Image.Image, pad: int = 10) -> Image.Image:
    """Tarayıcı kapağından kalan siyah kenarları ve boş beyaz alanları kırpar."""
    g = cv2.cvtColor(to_cv(img), cv2.COLOR_BGR2GRAY)
    h, w = g.shape
    # koyu kenar şeritlerini temizle
    mask = (g < 60).astype(np.uint8)
    top, bottom, left, right = 0, h, 0, w
    while top < h // 4 and mask[top].mean() > 0.5:
        top += 1
    while bottom > h * 3 // 4 and mask[bottom - 1].mean() > 0.5:
        bottom -= 1
    while left < w // 4 and mask[:, left].mean() > 0.5:
        left += 1
    while right > w * 3 // 4 and mask[:, right - 1].mean() > 0.5:
        right -= 1
    return img.crop((max(0, left), max(0, top), min(w, right), min(h, bottom)))


def split_spread(img: Image.Image) -> list[Image.Image]:
    """Yan yana iki sayfalık kitap taramasını ortadaki boşluktan böler."""
    if img.width < img.height * 1.15:
        return [img]
    g = cv2.cvtColor(to_cv(img), cv2.COLOR_BGR2GRAY)
    w = g.shape[1]
    lo, hi = int(w * 0.4), int(w * 0.6)
    col = (g[:, lo:hi] < 128).sum(axis=0)
    mid = lo + int(np.argmin(cv2.blur(col.reshape(1, -1).astype(np.float32), (25, 1)).ravel()))
    return [img.crop((0, 0, mid, img.height)), img.crop((mid, 0, img.width, img.height))]


def upscale_if_needed(img: Image.Image, dpi_hint: int | None = None) -> Image.Image:
    """Küçük yazılı düşük çözünürlüklü görüntüleri büyütür (OCR doğruluğu için)."""
    if dpi_hint and dpi_hint < 200:
        f = 300 / dpi_hint
        return img.resize((int(img.width * f), int(img.height * f)), Image.LANCZOS)
    if max(img.size) < 1400:
        f = 2
        return img.resize((img.width * f, img.height * f), Image.LANCZOS)
    return img


def preprocess(img: Image.Image, opts: PreprocessOptions | None = None, dpi_hint: int | None = None) -> tuple[list[Image.Image], dict]:
    opts = opts or PreprocessOptions()
    info = {"rotated": 0, "skew": 0.0, "perspective": False}
    img = ImageOps.exif_transpose(img).convert("RGB")
    if opts.invert:
        img = ImageOps.invert(img)
    if opts.resolution_fix:
        img = upscale_if_needed(img, dpi_hint)
    if opts.perspective:
        img, info["perspective"] = perspective_correct(img)
    if opts.auto_orient:
        img, info["rotated"] = auto_orient(img)
    if opts.deskew:
        img, info["skew"] = deskew(img)
    if opts.crop_borders:
        img = crop_borders(img)
    if opts.whiten_background:
        img = whiten_background(img)
    if opts.denoise:
        img = denoise(img)
    if opts.brightness != 1.0:
        img = ImageEnhance.Brightness(img).enhance(opts.brightness)
    if opts.contrast != 1.0:
        img = ImageEnhance.Contrast(img).enhance(opts.contrast)
    if opts.binarize:
        img = binarize(img)
    pages = split_spread(img) if opts.split_pages else [img]
    return pages, info
