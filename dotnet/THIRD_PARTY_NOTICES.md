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
| DejaVu Sans yazı tipi | 2.37 | Türkçe karakterli metin gömme | Bitstream Vera / Kamu malı (bkz. LICENSE-DejaVu.txt) | https://dejavu-fonts.github.io |

## Yalnızca geliştirme/test aşamasında

| Bileşen | Kullanım | Lisans |
|---|---|---|
| xUnit, Microsoft.NET.Test.Sdk | Birim testleri | Apache-2.0 / MIT |
| Inno Setup 6 | Kurulum paketi oluşturma | Inno Setup Lisansı (ücretsiz, ticari kullanım serbest) |
| reportlab, pikepdf, Pillow (Python) | Test fikstürü üretimi (`tests/.../generate_fixtures.py`) | BSD / MPL-2.0 / HPND — uygulamayla dağıtılmaz |

## Sonraki aşamalarda eklenecek (henüz pakette yok)

| Bileşen | Kullanım | Lisans |
|---|---|---|
| Tesseract OCR 5 + tessdata | OCR (Aşama 4) | Apache-2.0 |
| OpenCvSharp4 / OpenCV | Görüntü ön işleme (Aşama 4) | Apache-2.0 |
| DocumentFormat.OpenXml | DOCX/XLSX/PPTX üretimi (Aşama 5) | MIT |

## Bilerek kullanılmayanlar

iText (AGPL), MuPDF (AGPL), Ghostscript (AGPL), Syncfusion / Aspose / ABBYY SDK (ücretli lisans), SixLabors.ImageSharp (belirli ciro üstünde ücretli lisans).
