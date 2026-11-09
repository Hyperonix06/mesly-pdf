"""Hafif yardımcılar: uygulama veri klasörü ve dil adları (açılışta ağır kütüphane yüklemeden)."""
import os

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


