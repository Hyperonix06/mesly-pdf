# Mesly PDF Reader — Sürüm Notları

## 0.2.0 (11 Kasım 2026) — Aşama 1 ve 2

İlk .NET 10 / WPF sürümü. Önceki Python (PySide6) sürümünden bağımsız, sıfırdan yazıldı.

### Yeni
* **Altyapı:** C# / .NET 10 / WPF, MVVM; arayüzden bağımsız PDF motoru (PDFium, kendi bağlamalarımız), SQLite veri katmanı; self-contained x64 dağıtım.
* **Arayüz:** Fluent (WPF-UI) tasarım, Mica, açık/koyu/sistem teması, antrasit–beyaz–mavi palet, Türkçe arayüz, Per-Monitor V2 DPI; ana ekran, sekmeli belgeler, şerit menü, durum çubuğu.
* **Okuyucu:** sanal kaydırmalı görüntüleyici (yalnız görünen sayfalar çizilir), sürekli / tek / çift / kitap görünümü, %10–%800 yakınlaştırma (yüksek yakınlaştırmada bölge çizimi), genişliğe/sayfaya sığdırma, görünüm döndürme, tam ekran, karanlık okuma, metin seçme ve kopyalama, Türkçe kurallı arama (tam sözcük, büyük/küçük, aksan duyarsız), bağlantılar, yer imi okuma/ekleme/düzenleme, sayfaya git, son sayfayı hatırlama, parolalı PDF.
* **Sayfa yönetimi:** birleştirme, bölme (4 yöntem), ayıklama, boş sayfa / dosyadan / görüntüden ekleme, silme, sürükle-bırak sıralama, çoğaltma, döndürme, kırpma, sayfa boyutu (A4/A3/A5/Letter/Legal/özel), 25 adım geri al / yinele.
* **Dönüştürme:** PDF → PNG / JPG / TIFF (çok sayfalı) / BMP / TXT; JPG / PNG / TIFF / BMP → PDF (JPEG kayıpsız gömülür).
* **Yazdırma:** Windows yazdırma sistemi, yazıcı/kopya/aralık/kâğıt/ölçek/yön/renk seçenekleri, canlı önizleme.
* **Kararlılık:** atomik ve doğrulamalı kaydetme, otomatik kurtarma, genel hata yakalayıcı, günlük, geçici dosya temizliği.
* **Windows:** Inno Setup kurulumu (yönetici izni gerekmez), Başlat menüsü, masaüstü kısayolu, “Birlikte aç”, Gezgin sağ tık menüsü, Varsayılan uygulamalar kaydı, tek örnek çalışma.
* **Test:** 85 birim testi (Linux + Windows), gerçek pencerede uçtan uca öz test, Windows Server 2022/2025 kurulum testi.

### Bilinen sınırlamalar
Bkz. [KONTROL_LISTESI.md](KONTROL_LISTESI.md). Metin/görsel düzenleme (Aşama 3), OCR (4), Office dönüşümleri ve sıkıştırma (5), açıklamalar/formlar/imza/güvenlik (6), karşılaştırma ve toplu işlemler (7), yapay zekâ araçları (8) sonraki sürümlerde.
