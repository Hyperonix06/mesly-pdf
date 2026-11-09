# Mesly PDF

**Windows 10 / 11 için PDF düzenleyici, OCR, dönüştürücü, belge karşılaştırma ve otomasyon uygulaması.**
ABBYY FineReader PDF Corporate'ın özellik setini temel alır; tamamen ticari kullanıma uygun açık kaynak bileşenlerle yazılmıştır.

![Mesly PDF](meslypdf/assets/icon_256.png)

## Modüller ve özellikler

| Modül | Özellikler |
|---|---|
| **PDF Düzenleyici** | Sürekli / tek / iki sayfa görünüm, yakınlaştırma, gece modu, tam ekran, küçük resimler, yer imleri, tam metin arama (yorum ve yer imleri dahil), **paragraf metnini düzenleme ve yeniden akıtma**, metin/görsel/bağlantı ekleme, bul-değiştir, filigran, üst/altbilgi, Bates numaralandırma, damgalar, meta veri, ekli dosyalar, sıkıştırma (MRC benzeri), geri al / yinele, yazdırma (yorumlu / yorumsuz) |
| **Sayfa düzenleme** | Sürükle-bırak sıralama, döndürme, silme, çoğaltma, dosyadan / tarayıcıdan / boş sayfa ekleme, çıkarma, bölme (N sayfa, aralık, yer imi, boyut, boş sayfa ayırıcı), kırpma, boş sayfa tespiti, sayfa etiketleri |
| **Yorum ve işbirliği** | Vurgulama, altı/üstü çizili, yapışkan not, metin kutusu, dikdörtgen, elips, ok, serbest çizim, yanıtlar, durum atama (Kabul / Red / İptal / Tamamlandı), yazar-tür-durum filtreleri, sıralama, düzleştirme, yorum özeti dışa aktarma |
| **Formlar** | Form doldurma (sayfa üzerinde), metin / onay kutusu / seçenek / açılır liste / liste / düğme / imza alanı oluşturma, zorunlu & salt okunur alanlar, düğme eylemleri, FDF ve JSON içe/dışa aktarma, düzleştirme, Türkçe karakterli görünüm akışları |
| **Koruma** | Parola (RC4-40/128, AES-128/256) ve izinler, **gerçek karartma** (içerik silinir, OCR ile aranabilirlik geri kazandırılır), ara ve karart (TC Kimlik, IBAN, e-posta, telefon, kredi kartı, tarih, özel regex), gizli veri temizleme |
| **Dijital imza** | PAdES imza (PFX/P12 veya akıllı kart / e-imza PKCS#11), görünür imza görünümü, zaman damgası sunucuları, LTV, onaylı (certify) imza, imza doğrulama ve kurcalama tespiti, kendinden imzalı sertifika oluşturma, faks imzası (görsel) |
| **OCR Düzenleyici** | Tesseract 5 LSTM, 100+ dil (indirilebilir), Türkçe karakter düzeltmeli çift geçiş, otomatik dil algılama, otomatik yön ve eğiklik düzeltme, arka plan beyazlatma, gürültü giderme, perspektif (yamuk) düzeltme, kenar kırpma, çift sayfa bölme, parlaklık/karşıtlık, metin/tablo/resim/barkod alanları (otomatik + elle), tablo tanıma, barkod/QR, okuma sırası ve sütun algılama, düşük güvenli kelime vurgusu, doğrulama aracı, kullanıcı sözlüğü, düzeltme hafızası, OCR projeleri (.meslyocr) |
| **Dönüştürme** | Word (biçimli / düz), Excel (sayılar sayı olarak), PowerPoint (birebir konum), ODT, RTF, TXT, CSV, HTML, EPUB, FB2, aranabilir PDF, PDF/A-1b/2b/3b, PDF/UA (etiketli), sayfa görüntüleri (PNG/JPG/TIFF); Office/görüntü/metin → PDF; toplu dönüştürme; birleştirerek dönüştürme |
| **Belge karşılaştırma** | Farklı formatlar arası (PDF ↔ taranmış PDF ↔ Word ↔ görüntü ↔ TXT), kelime düzeyinde farklar, üst/altbilgi ayrımı, yok sayma seçenekleri, yan yana eşzamanlı görünüm, Word "Değişiklikleri İzle" çıktısı, Excel fark tablosu, işaretli PDF'ler, metin raporu |
| **Otomasyon** | Hot Folder (klasör / ağ klasörü / IMAP e-posta / FTP), sürekli izleme, aralıklı, günlük, tek seferlik zamanlama, işlenen dosyaları taşıma/silme, birleştirme, kendi adımlı **otomatik görevleriniz** (OCR, ön işleme, birleştir, boş sayfadan ayır, sıkıştır, filigran, Bates, damga, şifre, karartma, temizleme, PDF/A, imza, dışa aktar), sistem tepsisi, Windows açılışında başlatma |
| **Ekran Okuyucu** | Ekranın bir bölgesinden metin, tablo (Excel'e yapıştırılabilir) veya görüntü yakalama |
| **Windows entegrasyonu** | Gezgin sağ tık menüsü (Aç, Aranabilir PDF yap, Word/Excel'e dönüştür, Sıkıştır, Karşılaştır), Birlikte aç, varsayılan PDF uygulaması, Gönder menüsü, tek örnek çalışma, WIA/TWAIN tarayıcı desteği, Microsoft Office COM ile Office→PDF |
| **Komut satırı** | `meslypdf-cli.exe ocr / convert / merge / split / compress / protect / unprotect / redact / clean / watermark / compare / sign / verify / pdfa / task / hotfolder / languages` |

## Kurulum (son kullanıcı)

`MeslyPDF-Kurulum-x.y.z.exe` dosyasını çalıştırın. Yönetici izni gerekmez. Tesseract OCR motoru ve Türkçe/İngilizce dil paketleri kurulumla birlikte gelir. Ek diller **Ayarlar → OCR dil paketleri** bölümünden indirilebilir.

## Geliştirme

```bash
pip install -r requirements.txt
# Tesseract 5 kurulu olmalı (Windows: https://github.com/UB-Mannheim/tesseract/wiki)
python run_app.py                 # arayüz
python -m meslypdf --help         # komut satırı
python -m pytest tests            # 35 çekirdek testi
python run_app.py --selftest      # kurulum öz testi
```

### Windows kurulum dosyası üretme

GitHub'a gönderdiğinizde `.github/workflows/windows.yml` otomatik olarak:

1. Windows'ta testleri çalıştırır,
2. PyInstaller ile `MeslyPDF.exe` + `meslypdf-cli.exe` üretir, Tesseract'ı pakete ekler,
3. Derlenmiş uygulamada komut satırı ve arayüz öz testini çalıştırır,
4. Inno Setup ile `MeslyPDF-Kurulum-x.y.z.exe` ve taşınabilir zip üretir,
5. Kurulumu **Windows Server 2022 ve 2025** (Windows 10/11 çekirdeği) üzerinde sessizce kurar, öz testi çalıştırır, sağ tık kayıtlarını doğrular ve kaldırır,
6. `v1.0.0` gibi bir etiket gönderildiğinde GitHub Release oluşturur.

Elle derleme: `pyinstaller packaging/meslypdf.spec` ardından `iscc packaging\MeslyPDF.iss`.

## Kullanılan açık kaynak projeler (GitHub)

| Proje | Kullanım | Lisans |
|---|---|---|
| [pdfium](https://pdfium.googlesource.com/pdfium/) / [pypdfium2](https://github.com/pypdfium2-team/pypdfium2) | PDF görüntüleme, metin konumları, form çizimi | Apache-2.0 / BSD-3 |
| [qpdf](https://github.com/qpdf/qpdf) / [pikepdf](https://github.com/pikepdf/pikepdf) | PDF yapısı, sayfa işlemleri, şifreleme, ekler, formlar | Apache-2.0 / MPL-2.0 |
| [tesseract](https://github.com/tesseract-ocr/tesseract), [tessdata](https://github.com/tesseract-ocr/tessdata) | OCR motoru ve dil modelleri | Apache-2.0 |
| [UB-Mannheim/tesseract](https://github.com/UB-Mannheim/tesseract) | Windows Tesseract derlemesi | Apache-2.0 |
| [pyHanko](https://github.com/MatthiasValvekens/pyHanko) | PAdES dijital imza, doğrulama, PKCS#11 | MIT |
| [opencv](https://github.com/opencv/opencv) | Görüntü ön işleme, tablo çizgisi tespiti | Apache-2.0 |
| [zxing-cpp](https://github.com/zxing-cpp/zxing-cpp) | Barkod / QR okuma | Apache-2.0 |
| [reportlab](https://hg.reportlab.com/hg-public/reportlab) | Katman çizimi, PDF üretimi | BSD |
| [python-docx](https://github.com/python-openxml/python-docx), [python-pptx](https://github.com/scanny/python-pptx), openpyxl, [odfpy](https://github.com/eea/odfpy) | Office / ODF çıktıları | MIT / Apache-2.0 |
| [PySide6 (Qt)](https://code.qt.io/cgit/pyside/pyside-setup.git/) | Arayüz | LGPL-3.0 (dinamik bağlı) |
| [QtAwesome](https://github.com/spyder-ide/qtawesome) | Material Design simgeleri | MIT (simgeler Apache-2.0) |
| [watchdog](https://github.com/gorakhargosh/watchdog) | Hot Folder klasör izleme | Apache-2.0 |
| [PyInstaller](https://github.com/pyinstaller/pyinstaller), [Inno Setup](https://github.com/jrsoftware/issrc) | Paketleme ve kurulum | GPL+istisna / özel serbest |

**Bilerek kullanılmayanlar:** MuPDF/PyMuPDF, iText, Ghostscript, EbookLib (AGPL – kapalı kaynak dağıtımda kaynak kodunu açma zorunluluğu getirir), pdf2docx (PyMuPDF'e bağımlı).

**İleride işe yarayabilecekler:** [PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR) (Apache-2.0, daha güçlü düzen analizi), [img2table](https://github.com/xavctn/img2table) (MIT, çizgisiz tablolar), [OCRmyPDF](https://github.com/ocrmypdf/OCRmyPDF) (MPL-2.0, fikir kaynağı), [Stirling-PDF](https://github.com/Stirling-Tools/Stirling-PDF) (özellik fikirleri), [veraPDF](https://github.com/veraPDF/veraPDF-library) (PDF/A doğrulama).

## Bilinen sınırlamalar

- OCR doğruluğu ABBYY'nin kapalı kaynak motoru seviyesinde değildir; temiz taramalarda Türkçe/İngilizce sonuçlar çok iyidir, karmaşık düzenlerde (çizgisiz tablolar, çok sütunlu dergiler) elle alan düzeltmesi gerekebilir.
- Metin düzenleme satır/paragraf düzeyinde çalışır; değiştirilen paragraf uygulamanın Unicode yazı tipiyle (DejaVu) yeniden yazılır.
- Windows Gezgini/Outlook önizleme bölmesi eklentisi ve Office şerit eklentisi yoktur (sağ tık menüsü ve "Birlikte aç" vardır).
- Sanal PDF yazıcısı yerine Windows'un yerleşik "Microsoft Print to PDF" yazıcısı ve Office COM dönüştürmesi kullanılır.
- PDF/A dönüştürmesi gömülmemiş yazı tiplerini uyarı olarak bildirir; tam doğrulama için veraPDF önerilir.
- Kendinden imzalı sertifikalar bütünlüğü kanıtlar; yasal geçerlilik için nitelikli elektronik sertifika (e-imza) kullanın.
