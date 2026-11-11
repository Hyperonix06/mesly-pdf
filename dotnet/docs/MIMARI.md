# Mesly PDF Reader — Mimari Plan

Hedef: Windows 10/11 (x64) için C# / .NET 10 / WPF ile yazılmış, MVVM tabanlı, modüler, çevrimdışı çalışan profesyonel PDF + OCR uygulaması.

## 1. Katmanlar

```
┌──────────────────────────────────────────────────────────────┐
│ MeslyPdfReader.App  (net10.0-windows, WPF, MVVM)             │
│  Views (XAML) · ViewModels · Denetimler (PageViewer, ...)    │
│  Uygulama servisleri: tema, diyalog, yazdırma, pano, kabuk   │
└───────────────▲──────────────────────────────▲───────────────┘
                │ arayüzler (Core)             │
┌───────────────┴──────────────┐ ┌─────────────┴────────────────┐
│ MeslyPdfReader.Pdf (net10.0) │ │ MeslyPdfReader.Data (net10.0)│
│  PDFium P/Invoke katmanı     │ │  SQLite: ayarlar, son        │
│  PdfEngine (tek iş parçacığı)│ │  dosyalar, son sayfa,        │
│  PdfDocument: render, metin, │ │  görev geçmişi               │
│  arama, bağlantı, yer imi,   │ └──────────────────────────────┘
│  sayfa işlemleri, kaydetme   │
│  Dışa aktarma: görüntü / TXT │   İleride: MeslyPdfReader.Ocr,
│  Görüntü → PDF               │   .Convert, .Security, .Compare,
└───────────────▲──────────────┘   .Batch, .Ai (aynı desen)
                │
┌───────────────┴──────────────────────────────────────────────┐
│ MeslyPdfReader.Core (net10.0) — modeller, sayfa aralığı       │
│ ayrıştırıcı, sonuç/hata tipleri, servis arayüzleri            │
└──────────────────────────────────────────────────────────────┘
```

* **Arayüz bağımsızlığı:** Pdf, Data ve ileride gelecek OCR/dönüştürme servisleri WPF'ye bağımlı değildir; `net10.0` hedefler. Bu sayede hem Windows'ta hem Linux CI'da birim testleriyle doğrulanırlar.
* **MVVM:** CommunityToolkit.Mvvm (`ObservableObject`, `RelayCommand`, `AsyncRelayCommand`). Görünümler yalnızca bağlama yapar; iş mantığı ViewModel ve servislerdedir.
* **Bağımlılık enjeksiyonu:** Microsoft.Extensions.DependencyInjection; `App.xaml.cs` içinde kompozisyon kökü.

## 2. PDF motoru

* **PDFium** (Google, BSD-3/Apache-2.0) — bblanchon/pdfium-binaries derlemesi (V8/XFA içermez → JavaScript çalıştırmaz, saldırı yüzeyi küçük).
* Kendi `LibraryImport` bağlamalarımız (`Interop/PdfiumNative.cs`): üçüncü taraf sarmalayıcıya bağımlılık yok, AOT/trim uyumlu.
* **İş parçacığı güvenliği:** PDFium iş parçacığı güvenli değildir. Tüm çağrılar `PdfEngine` içindeki **tek bir ayrılmış iş parçacığında** sıraya alınır (`PdfEngine.RunAsync`). Arayüz iş parçacığı hiçbir zaman PDFium çağrısı yapmaz → arayüz donmaz, aynı anda birden çok belge güvenle açılır.
* **Görüntüleme:** Sayfa, istenen ölçekte BGRA tampona çizilir; WPF tarafında `WriteableBitmap`'e kopyalanır. Görünür sayfalar öncelikli, uzaklaşan sayfaların isteği iptal edilir.
* **Bellek:** Bitmap önbelleği LRU + bayt bütçesi (varsayılan 256 MB). Sayfa nesneleri (FPDF_PAGE) ayrı bir LRU'da tutulur ve kapatılır. 1.000 sayfalık belge yalnızca görünen sayfaları belleğe alır.
* **Kaydetme:** Sayfa işlemleri (ekle, sil, taşı, döndür, kırp, boyut) doğrudan PDF yapısına uygulanır, `FPDF_SaveAsCopy` ile geçici dosyaya yazılır, doğrulanır (yeniden açılabiliyor mu) ve atomik olarak hedefin yerine taşınır. Orijinal dosya, başarılı kayıttan önce asla bozulmaz.
* **Güvenlik:** Bozuk / kötü amaçlı dosyalar için: PDFium hata kodları kullanıcıya Türkçe açıklanır; motor iş parçacığında yakalanmayan hata uygulamayı kapatmaz; JavaScript çalıştırılmaz; dosya yolu dışında ağ erişimi yoktur.

### Neden PDFium + ileride PDFsharp?

| İhtiyaç | Bileşen |
|---|---|
| Görüntüleme, metin/karakter konumları, arama, bağlantılar, yer imi okuma, sayfa nesnesi düzenleme, sayfa içe aktarma/silme/taşıma, kaydetme | PDFium |
| Yer imi (outline) **yazma**, şifreleme (AES-256), izinler, meta veri, dijital imza | PDFsharp 6 (MIT) — Aşama 2 sonu / Aşama 6 |
| Gerçek metin düzenleme (Aşama 3) | PDFium sayfa nesnesi API'si (`FPDFText_SetText`, `FPDFPageObj_*`, `FPDFPage_GenerateContent`) — beyaz kutu taklidi yok |

## 3. OCR, görüntü işleme, dönüştürme (sonraki aşamalar)

| Modül | Bileşen | Lisans |
|---|---|---|
| OCR | Tesseract 5 (`Tesseract` NuGet 5.x sarmalayıcı) + `tessdata_best` tur/eng/osd | Apache-2.0 |
| Ön işleme (eğrilik, gürültü, perspektif, kontrast) | OpenCvSharp4 + OpenCV 4.x | Apache-2.0 |
| DOCX / XLSX / PPTX üretimi | DocumentFormat.OpenXml | MIT |
| Office → PDF | Kurulu Microsoft Office (COM) varsa o; yoksa kurulu LibreOffice (ayrı süreç). Uygulama içinde dağıtılmaz; yoksa kullanıcıya açıkça söylenir. | — |
| Tarayıcı | WIA 2.0 (Windows bileşeni); TWAIN için NTwain | Windows / MIT |
| Dijital imza doğrulama | System.Security.Cryptography.Pkcs | MIT |

**Bilerek kullanılmayanlar:** iText (AGPL), MuPDF (AGPL), Ghostscript (AGPL), ImageSharp (ticari kullanımda ücretli lisans koşulu), Syncfusion/Aspose/ABBYY SDK (ücretli). Ticari dağıtımda kaynak kodu açma zorunluluğu getirecek hiçbir bileşen eklenmez.

## 4. Veri

* `%LOCALAPPDATA%\Mesly\PdfReader\mesly.db` (SQLite, WAL): `settings`, `recent_files`, `document_state` (son sayfa, yakınlaştırma), `task_history`.
* Kurtarma ve geçici dosyalar: `%LOCALAPPDATA%\Mesly\PdfReader\temp` ve `\recovery`; açılışta 7 günden eski geçici dosyalar silinir.
* Günlük: `%LOCALAPPDATA%\Mesly\PdfReader\logs\` (dönen dosya, kullanıcı verisi içermez).
* **Hiçbir veri internete gönderilmez.** İsteğe bağlı çevrimiçi özellikler (Aşama 8) ayrı açık izinle çalışır.

## 5. Arayüz

* WPF-UI 4 (lepoco/wpfui, MIT): Fluent denetimleri, Mica arka plan (Windows 11), Windows 10'da düz arka plana düşer, sistem teması takibi, Fluent System Icons.
* Renkler: antrasit (#1F2329 / #2B3038), beyaz, yumuşak mavi vurgu (#3D7BFD / #6FA0FF).
* Ekranlar: **Ana Sayfa** (hızlı işlemler, son belgeler, sürükle-bırak alanı) ve sekme başına **Belge Görünümü** (sol panel: küçük resimler / yer imleri / arama; orta: sayfa görüntüleyici; sağ: özellikler; alt: durum çubuğu).
* Per-Monitor V2 DPI farkındalığı (uygulama bildirimi), tüm metinler Türkçe, `tr-TR` kültürü, Segoe UI Variable.
* Uzun işlemler `AsyncRelayCommand` + `CancellationToken` + ilerleme bildirimi ile; arayüz iş parçacığı bloklanmaz.

## 6. Dağıtım

* `dotnet publish -r win-x64 --self-contained` → kullanıcı .NET kurmak zorunda kalmaz.
* Inno Setup 6 ile `MeslyPdfReader-Setup-x.y.z.exe` (kullanıcı başına kurulum, yönetici izni gerekmez; dosya ilişkilendirme, Başlat menüsü, isteğe bağlı masaüstü kısayolu, Gezgin sağ tık "Mesly PDF Reader ile aç").
* GitHub Actions (`windows-latest`): derleme → birim testleri → yayınlanmış exe üzerinde **öz test** (gerçek pencere açılır, PDF görüntülenir, ekran görüntüsü alınır) → kurulum paketi → sessiz kurulum / öz test / kaldırma.

## 7. Test stratejisi

* `MeslyPdfReader.Tests` (xUnit): motor, sayfa işlemleri, kaydetme, arama, Türkçe metin, bozuk/şifreli dosya, 1.000 sayfa, bellek, veri katmanı. Hem Linux hem Windows'ta çalışır.
* Test PDF'leri testler içinde PDFium ile **üretilir** (Türkçe karakterli gömülü yazı tipiyle) → sabit, telifsiz fikstürler.
* Arayüz öz testi (`MeslyPdfReader.exe --selftest <rapor.json>`): Windows CI'da gerçek WPF penceresinde uçtan uca senaryo, ekran görüntüleri artefakt olarak saklanır.
* `docs/TEST_RAPORU.md`: yalnızca **gerçekten çalıştırılmış** testlerin beklenen / gerçek sonuçları.
