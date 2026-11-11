# Mesly PDF Reader — Sürüm Notları

## 0.5.1 (11 Kasım 2026) — FineReader düzeni ve yeni logo
* Arayüz ABBYY FineReader PDF 16 düzeninde: “Dosya” menüsü; **Belge · Sayfaları Düzenle · İçeriği Düzenle** sekmeleri; tek satır araç çubuğu; Sayfaları Düzenle'de sayfa ızgarası; açılışta **Yeni Görev** ekranı (Aç · Tara · Son Kullanılanlar, belge dilleri). Ctrl+E İçeriği Düzenle, Ctrl+N yeni PDF.
* Yeni Mesly PDF Reader logosu ve uygulama simgesi (küçük boyutlarda yalnız PDF-M işareti, büyük boyutlarda tam logo); PDF dosyaları ve kısayollar yeni simgeyle görünür.
* Düzeltme: gizli sayfa ızgarası büyük belgelerde gereksiz bellek kullanıyordu (1.000 sayfada ~650 MB → ~50–100 MB).

## 0.5.0 (11 Kasım 2026) — Aşama 4: OCR, otomatik tarama düzeltme, yeni arayüz

### Tarama artık kendini düzeltiyor
* Tarayıcı sayfayı yan veya ters verse bile sayfa **metne bakılarak otomatik dik çevrilir**; eğik taranan satırlar düzeltilir, kapak gölgesi gibi koyu kenarlar temizlenir. Düzeltilen küçük resim tarama penceresinde hemen görünür.
* Tarama bitince **metin tanınır**: PDF aranabilir, kopyalanabilir ve düzenlenebilir olur. Her ikisi tarama penceresinden kapatılabilir.

### Yeni: Tanıma (OCR) sekmesi
* **Metni tanı:** Türkçe ve İngilizce; seçilen sayfalar aynı anda birden çok çekirdekte tanınır. Sayfa aynı görünür, altına görünmez metin katmanı eklenir.
* **Görüntüyü düzelt:** var olan taranmış PDF'lerde yön, eğiklik, kenar ve arka plan düzeltme.
* **Doğrula:** düşük güvenle tanınan sözcükler sayfadaki görüntüsüyle yan yana gösterilir; Enter ile düzeltip sonrakine geçilir.
* Taranmış bir sayfa açıldığında üstte “Metni tanı” bilgi şeridi çıkar.
* **Taranmış sayfada düzenleme:** tanınan metni Düzenle aracıyla değiştirdiğinizde görüntüdeki eski yazı da silinir (kâğıt rengiyle), yeni metin gerçek metin olarak yazılır.
* Tanıma tamamen bilgisayarınızda yapılır; belgeler hiçbir yere gönderilmez.

### Arayüz toplandı
* Office / FineReader düzeninde **gruplu şerit**: büyük ana düğmeler, yanında küçük ikincil düğmeler, grup başlıkları; taşma ve kaydırma çubuğu yok.
* Sekmeler: Dosya · Okuma · Düzenle · Tanıma (OCR) · Sayfalar · Dönüştür. Yinelenen “Ana Sayfa” kaldırıldı; Kaydet, Geri al, Yinele, Yazdır her zaman sağ üstte.
* **Düzenle sekmesine geçmek düzenleme kipini açar** (FineReader gibi): metne çift tıklayıp yazın; sekmeden çıkınca kip kapanır.
* Belge yokken yalnız ilgili düğmeler görünür (devre dışı düğme kalabalığı yok). Başlangıç ekranı 8 eşit kartla yeniden düzenlendi: PDF aç, PDF düzenle, Tara, Metni tanı, Görüntülerden PDF, Birleştir, Dönüştür, Böl.

## 0.4.0 (11 Kasım 2026) — Aşama 3: gerçek PDF düzenleme

### Yeni: Düzenle aracı ve Düzenle sekmesi
* **Düzenle aracı (E):** sayfadaki metin paragraflarını, görselleri ve şekilleri seçin; sürükleyerek taşıyın, tutamaçlarla boyutlandırın, Delete ile silin, ok tuşlarıyla 1 pt (Shift: 10 pt) kaydırın. Ctrl ile çoklu seçim ve hizalama.
* **Metin düzenleme:** paragrafa çift tıklayın (veya F2), metni yazın, Ctrl+Enter ile uygulayın. Eski metin nesneleri sayfadan gerçekten silinir; yeni metin aynı konumdan başlayıp kutu genişliğine göre satırlara akar, aranabilir ve kopyalanabilir.
* **Biçim:** yazı tipi (bilgisayarınızda kurulu yazı tipleri), boyut, renk, kalın/eğik/altı çizili, hizalama, satır aralığı — sağdaki özellikler panelinden.
* **Eklenebilenler:** metin kutusu, görsel (değiştirme dahil), dikdörtgen, elips, çizgi, ok, web bağlantısı; bağlantı kaldırma.
* **Sayfa genelinde:** filigran, üstbilgi/altbilgi, sayfa numarası, arka plan rengi — eklenir ve tek tıkla kaldırılır.
* Her düzenleme geri alınabilir; meşgul katmanı yalnızca uzun süren işlemlerde görünür.

### Dürüst sınırlamalar
* PDF'ye gömülü alt küme yazı tipleri yeni harfleri içermeyebileceğinden düzenlenen metin, adı eşlenen kurulu yazı tipiyle (ör. Arial-BoldMT → Arial Kalın) yazılır; bulunamazsa benzeri kullanılır ve durum çubuğunda söylenir.
* Form nesnesi (gruplanmış içerik) içindeki metin düzenlenemez; nesne bütün olarak taşınabilir/silinebilir.
* Belge içi sayfaya giden bağlantı ekleme henüz yok (web/e-posta bağlantıları eklenebilir).

## 0.3.0 (11 Kasım 2026)

### Yeni: Tarama
* Windows Görüntü Alma (WIA) ile **düz camdan**, **belge besleyiciden** ve **çift taraflı belge besleyiciden** tarama.
* Renkli / gri / siyah-beyaz, 150–600 DPI, A4 / A3 / A5 / Letter / Legal veya tarayıcının tüm alanı; ayarlar hatırlanır.
* Düz camda sayfa sayfa tarayıp biriktirme; besleyicide tüm sayfalar tek seferde. Kâğıt sıkışırsa o ana kadar taranan sayfalar korunur.
* Taranan sayfaları önizleme, döndürme, sıralama, silme; yeni PDF oluşturma veya açık belgeye istenen konuma ekleme.
* Tarayıcının kendi (sürücü) tarama penceresiyle tarama seçeneği.
* Erişim: Ana ekran → Tara, Dosya / Ana Sayfa → Tara, Sayfalar → Tarayıcıdan ekle, Dönüştür → Tarayıcı → PDF.

### Hızlı kurulum
* Uygulama tek exe olarak dağıtılıyor: kurulan dosya sayısı 286'dan ~11'e indi. Windows Defender her dosyayı ayrı taradığı için kurulum ve kaldırma belirgin şekilde hızlandı (ölçümler: TEST_RAPORU.md).
* Kurulumun “uygulama kapatma” denetimi yalnızca Mesly PDF Reader'ı denetliyor; eski sürümden kalan DLL'ler güncellemede temizleniyor.

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
