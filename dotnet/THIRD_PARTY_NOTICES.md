# Mesly PDF Reader — Üçüncü Taraf Bileşenler ve Lisanslar

Mesly PDF Reader yalnızca ticari dağıtıma izin veren, kaynak kodu açma zorunluluğu getirmeyen (copyleft olmayan) bileşenler kullanır.

| Bileşen | Sürüm | Kullanım | Lisans | Kaynak |
|---|---|---|---|---|
| PDFium (bblanchon/pdfium-binaries) | 159.0.8152 | PDF görüntüleme, metin, arama, bağlantılar, sayfa işlemleri, kaydetme | BSD-3-Clause / Apache-2.0 (paket: Apache-2.0) | https://pdfium.googlesource.com/pdfium · https://github.com/bblanchon/pdfium-binaries |
| PDFsharp | 6.2.4 | Yer imi (outline) yazma | MIT | https://github.com/empira/PDFsharp |
| WPF-UI (lepoco/wpfui) | 4.3.0 | Fluent tasarım denetimleri, tema, Mica | MIT | https://github.com/lepoco/wpfui |
| Fluent System Icons (WPF-UI içinde) | — | Simgeler | MIT | https://github.com/microsoft/fluentui-system-icons |
| CommunityToolkit.Mvvm | 8.4.2 | MVVM altyapısı | MIT | https://github.com/CommunityToolkit/dotnet |
| Microsoft.Extensions.DependencyInjection | 10.0.15 | Bağımlılık enjeksiyonu | MIT | https://github.com/dotnet/runtime |
| Microsoft.Data.Sqlite + SQLitePCLRaw + SQLite | 10.0.15 | Ayarlar, son dosyalar, görev geçmişi | MIT / Apache-2.0 / Kamu malı | https://github.com/dotnet/efcore · https://sqlite.org |
| System.Security.Cryptography.Pkcs | 10.0.15 | (PDFsharp bağımlılığı) | MIT | https://github.com/dotnet/runtime |
| .NET 10 çalışma zamanı ve WPF | 10.0 | Uygulama çalışma zamanı (self-contained) | MIT | https://github.com/dotnet |
| Tesseract OCR (yerel DLL: charlesw/tesseract NuGet paketinden) | 5.0 (Windows) | Metin tanıma, sayfa yönü algılama | Apache-2.0 | https://github.com/tesseract-ocr/tesseract · https://github.com/charlesw/tesseract |
| PaddleOCR PP-OCRv6 modelleri (RapidOCR 3.10.0 paketinden, ONNX) | PP-OCRv6 | Gelişmiş metin tanıma (algılama + tanıma; Tesseract ile birleştirilir) | Apache-2.0 | https://github.com/PaddlePaddle/PaddleOCR · https://github.com/RapidAI/RapidOCR |
| ONNX Runtime (yerel DLL) | 1.22.1 | OCR modellerini çalıştırma | MIT | https://github.com/microsoft/onnxruntime |
| Leptonica | 1.82.0 | Tesseract'ın görüntü kitaplığı | BSD-2-Clause benzeri Leptonica lisansı | http://www.leptonica.org |
| tessdata_best (tur, eng) ve tessdata (osd) | — | OCR dil modelleri ve yön algılama verisi | Apache-2.0 | https://github.com/tesseract-ocr/tessdata_best · https://github.com/tesseract-ocr/tessdata |
| Microsoft Visual C++ 2015–2022 çalışma zamanı (vcruntime140, vcruntime140_1, msvcp140) | 14.x | Tesseract DLL'lerinin bağımlılığı; yalnızca sistemde yoksa yüklenir | Microsoft Visual Studio yeniden dağıtılabilir dosya lisansı (uygulama yanında dağıtıma izin verilir) | https://learn.microsoft.com/cpp/windows/redistributing-visual-cpp-files |
| DocumentFormat.OpenXml + DocumentFormat.OpenXml.Framework | 3.5.1 | Word (.docx) ve Excel (.xlsx) dosyası yazma | MIT | https://github.com/dotnet/Open-XML-SDK |
| System.IO.Packaging | 10.0.2 | OpenXml paket (ZIP) altyapısı | MIT | https://github.com/dotnet/runtime |
| DejaVu Sans yazı tipi | 2.37 | Türkçe karakterli metin gömme | Bitstream Vera / Kamu malı (bkz. LICENSE-DejaVu.txt) | https://dejavu-fonts.github.io |

## Yalnızca geliştirme/test aşamasında

| Bileşen | Kullanım | Lisans |
|---|---|---|
| xUnit, Microsoft.NET.Test.Sdk | Birim testleri | Apache-2.0 / MIT |
| Inno Setup 6 | Kurulum paketi oluşturma | Inno Setup Lisansı (ücretsiz, ticari kullanım serbest) |
| reportlab, pikepdf, Pillow (Python) | Test fikstürü üretimi (`tests/.../generate_fixtures.py`) | BSD / MPL-2.0 / HPND — uygulamayla dağıtılmaz |

## Pakette olmayan, kullanıcının bilgisayarında kuruluysa kullanılanlar

| Bileşen | Kullanım | Not |
|---|---|---|
| Microsoft Office (Word, Excel, PowerPoint) | Office belgesi → PDF | COM otomasyonu; uygulamayla dağıtılmaz, kullanıcının kendi lisansıyla çalışır |
| LibreOffice | Office belgesi → PDF (Office yoksa) | Ayrı bir program olarak çağrılır (MPL-2.0); uygulamayla dağıtılmaz |

Görüntü düzeltme (eğiklik ölçümü, döndürme, kenar ve arka plan temizleme), sayfa düzeni çözümleme (paragraf, sütun, tablo algılama) ve PDF görsel sıkıştırma kendi yönetilen kodumuzla yapılır; OpenCV kullanılmaz.

## Bilerek kullanılmayanlar

iText (AGPL), MuPDF (AGPL), Ghostscript (AGPL), Syncfusion / Aspose / ABBYY SDK (ücretli lisans), SixLabors.ImageSharp (belirli ciro üstünde ücretli lisans).
