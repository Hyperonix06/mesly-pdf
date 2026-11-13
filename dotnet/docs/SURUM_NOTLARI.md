# Mesly PDF Reader — Sürüm Notları

## 0.8.0 (14 Kasım 2026) — Daha doğru OCR, çizgisiz tablolar, gölge giderme

### Gelişmiş metin tanıma
* **PP-OCRv6 motoru (yeni):** PaddleOCR'ın PP-OCRv6 modelleri (Apache-2.0) ONNX Runtime ile **bilgisayarınızda** çalışır; belgeleriniz hiçbir dış servise gönderilmez. Gürültülü, soluk, gölgeli ve düşük çözünürlüklü taramalarda Tesseract'tan belirgin biçimde daha doğru okur.
* **Otomatik (karma) kip:** İki motor birlikte çalışır. Türkçe harflerde (İ, ı, Ğ, Ş…) Tesseract'ın emin olduğu yerlerde onun, gerçek farklarda yalnızca çok yüksek güvenle onun sonucu alınır; PP-OCR'ın kaçırdığı sözcükler eklenir. Ayarlar → OCR → "Metin tanıma motoru" ile "Yalnız gelişmiş" veya "Yalnız Tesseract" seçilebilir.
* Sayfa başına süre Tesseract'a göre daha uzundur (yaklaşık 3–8 sn).

### Tablo ve yerleşim
* **Çizgisiz tablolar:** Hizalamayla oluşmuş (kenarlıksız) tablolar sözcük konumlarından bulunur; Word'e kenarlıksız tablo, Excel'e hücre/sayı olarak aktarılır. Düz yazı, iki sütunlu metin ve iki satırlık hizalamalar tablo sayılmaz. Taranmış sayfalarda OCR sonrası da çalışır.

### Tarama düzeltme
* **Aydınlık düzleştirme:** Gölge, kenar kararması ve ışık degradesi yerel olarak giderilir (mürekkep korunur; zaten düzgün sayfalara dokunulmaz). "Arka planı beyazlat" artık varsayılan olarak açık.

### Bilinen sınırlar
* Kıvrık (kitap sırtı / buruşuk) sayfaların düzeltilmesi (dewarp) henüz yok; yalnız yön ve eğiklik düzeltilir.
* Çizgili tablo algılama düz çizgilere dayanır; belirgin eğri taramalarda önce eğiklik düzeltilmelidir.
* El yazısı tanıma yok.

## 0.7.0 (12 Kasım 2026) — Yeni tasarım: afişteki 12 ekran

Arayüz, paylaşılan afiş tasarımına göre baştan kuruldu: kırmızı Mesly renkleri, solda menü (Ana Sayfa, PDF Aç, Dönüştür, OCR, Araçlar, Tarama, Ayarlar, Yardım) ve her işlem için ayrı ekran. Önceki sürümlerin tüm işlevleri korunuyor.

### Ekranlar
* **Ana Sayfa:** Hoş Geldiniz kartı, PDF Aç / OCR / Dönüştür / Tarama kutuları, Hızlı Erişim (Son Belgeler, Sık Kullanılanlar) ve bu ayın gerçek sayılarını gösteren gösterge paneli.
* **Belgelerim:** Son Kullanılanlar, Tüm Belgeler (Belgeler, Masaüstü ve İndirilenler klasörlerindeki PDF'ler) ve Sık Kullanılanlar; arama, yıldızla sık kullanılanlara ekleme, liste/kart görünümü, satır menüsü (aç, yeni pencerede aç, klasörde göster, dönüştür, OCR, listeden kaldır), Yeni Pencere.
* **PDF Görüntüleyici:** Giriş / Görünüm / Araçlar şeridi, sayfa küçük resimleri, sayfaya git kutusu, birden çok belge için sekmeler.
* **PDF Düzenleme:** Giriş / Düzenle / Açıklama / İmza / Form sekmeleri.
  * **Vurgula, Altını Çiz, Üstünü Çiz:** Metni sürükleyerek seçin; standart PDF açıklaması olarak eklenir (Acrobat dahil her okuyucuda görünür, silinebilir). Renk seçilebilir.
  * **Açıklama (not):** Sayfaya tıklayıp yazın; Mesly'de yanında balon olarak, diğer okuyucularda not simgesi olarak görünür. Açıklamalar listesi: sayfasına git, seçerek sil; sayfadaki tüm açıklamaları kaldır.
  * **İmza:** Fareyle/kalemle çizin, adınızı yazın veya imzanızın fotoğrafını seçin (kâğıt zemini otomatik saydam yapılır). İmza **saydam** yerleşir, alttaki yazıyı örtmez. İmzalar bu bilgisayarda kaydedilip yönetilebilir. **Tarih** ve **Ad Soyad** damgası.
  * **Form:** Formu Doldur (metin, onay kutusu, radyo düğmesi, açılır liste), Formu Sıfırla, Düzleştir. Doldurulan değerler her PDF okuyucusunda aynı görünür.
* **OCR (Metin Tanıma):** Kaynak (PDF, resim veya tarayıcı), dil, çıktı türü (Düzenlenebilir Metin, Aranabilir PDF, Word, TXT), sayfa yapısını koru, otomatik eğiklik düzeltme, **gürültü azaltma** (yeni: tek tük benekler temizlenir, nokta/virgül korunur), düzeltme önizlemesi.
* **OCR Sonucu Düzenleme:** Tanınan metin sayfa görünümlü düzenleyicide: yazı tipi, boyut, kalın/eğik/altı çizili, renk, hizalama, madde işaretli ve numaralı liste, bağlantı; sayfa küçük resimleri; Word, TXT veya PDF olarak kaydet, yazdır, tümünü kopyala.
* **Dönüştür:** Word, Excel, **PowerPoint (yeni)**, Resim, TXT; sayfa aralığı, gerektiğinde OCR. PowerPoint'te her sayfa bir slayt; metinler düzenlenebilir metin kutuları, tablolar gerçek PowerPoint tablosu.
* **Toplu Dönüştürme:** Dosya / klasör ekle, satır başına biçim, durum (Beklemede, İşleniyor %, Tamamlandı, Hata), genel ilerleme, Durdur, çıktı klasörü.
* **Sayfa Yönetimi:** Birleştir (sıra, dosya başına sayfa aralığı), Böl (her N sayfa, aralıklar, her sayfa, tek/çift), **Sırala** (küçük resimleri sürükleyip bırakın; Sıfırla / Uygula, Geri Al ile geri dönülür).
* **Tarama:** Tarayıcı, renk, çözünürlük, dosya türü (PDF / JPG / PNG); hedef: yeni PDF, açık PDF'e ekle, resim olarak kaydet; **otomatik kırpma** (yeni), otomatik hizalama, besleyiciden çok sayfa, önizleme.
* **Ayarlar:** Genel, Görüntüleme, Dönüştürme, OCR, Tarama, Güvenlik, Gelişmiş. Yeni: başlangıçta Ana Sayfa ya da son açık belgeler, otomatik güncelleme denetimi, varsayılan PDF görüntüleyici, tüm ayarları sıfırla, klavye kısayolları listesi.
* **Hakkında / Yardım / Lisans:** sürüm, Windows 10/11 uyumluluğu, Lisans Sözleşmesi, Gizlilik Politikası, Destek ile İletişim (son.hayal06@gmail.com), yardım konuları, üçüncü taraf lisansları.

### Düzeltmeler
* **Form alanları artık görüntüleniyor:** Önceki sürümlerde formlu PDF'lerde alanların çerçeveleri ve değerleri görüntüleyicide çizilmiyordu (PDF motoru form alanlarını ayrı bir adımda çizer). Düzeltildi.

### Güncellemeler
* Ayarlar'da açıksa uygulama günde bir kez GitHub'daki en son sürümü sorar (yalnız sürüm numarası; belge veya kişisel bilgi gönderilmez). Yeni sürüm varsa indirip kurmadan önce onayınız istenir.

### Bilinen sınırlamalar (açıkça)
* **Varsayılan PDF görüntüleyici:** Windows 10/11, programların kendini varsayılan yapmasına izin vermez. Anahtar, Windows'un Varsayılan uygulamalar sayfasını açar (Windows 11'de doğrudan Mesly'nin sayfası) ve gerçek durumu gösterir.
* **İmza**, belgeye yerleştirilen el yazısı imza görüntüsüdür; nitelikli elektronik imza (e-imza) değildir.
* **Arayüz dili** yalnızca Türkçedir.
* **OCR sonucunu PDF olarak kaydet**, Windows'un yerleşik “Microsoft Print to PDF” yazıcısını kullanır; dosya adı Windows'un penceresinde sorulur. Bu yazıcı kapalıysa nasıl açılacağı söylenir.
* **Otomatik kırpma**, kâğıttan farklı renkteki tarayıcı zeminini kırpar; zemin kâğıtla aynı renkteyse (beyaz kapak) kırpılacak bir şey ayırt edilemez ve görüntü değiştirilmez.

## 0.6.2 (12 Kasım 2026) — Otomatik tanıma ve düzeltme

* **Taranmış PDF açılınca her şey kendiliğinden:** Metni olmayan taranmış sayfalar arka planda düzeltilir (yan/ters sayfa dik çevrilir, eğiklik giderilir, kenar gölgeleri temizlenir) ve metni tanınır — önce ekrandaki sayfa. Belge bu sırada okunabilir; üstteki şeritte ilerleme ve **Durdur** düğmesi görünür.
* Kaydetme, düzenleme gibi bir işlem başlatırsanız tanıma o sayfayı bitirip bekler, işlem bitince kalan sayfalarla sürer. Tamamı **Ctrl+Z** ile tek adımda geri alınır; kaydetmeden kapatırsanız özgün dosya değişmez.
* **Görüntülerden PDF** (telefonla çekilmiş veya tarayıcıdan JPG/PNG olarak gelen sayfalar) de artık otomatik düzeltilip tanınır.
* **İçeriği Düzenle**'ye geçtiğiniz sayfa taranmışsa önce kendiliğinden tanınır; hemen düzenleyebilirsiniz.
* Ayarlar → **Metin tanıma (OCR)**: otomatik tanımayı ve düzeltme seçeneklerini açıp kapatabilirsiniz (varsayılan: açık). Tanıma tamamen bu bilgisayarda yapılır.

## 0.6.1 (12 Kasım 2026) — Metin düzenleme: metin artık yerinden kaymıyor

Kullanıcı bildirimi: “Düzenledikten sonra metin aynı yerinde durmuyor, kayıyor.” Ayrıntılı inceleme beş ayrı neden buldu; hepsi düzeltildi ve 14 yeni testle kalıcı olarak denetleniyor.

* **Sayfadaki diğer metinler kayıyordu:** Bir paragraf düzenlenince PDF motoru (PDFium) sayfayı yeniden yazarken sözcük ve harf aralığı ayarlarını (Tw/Tc) atıyordu; iki yana yaslı paragraflar daralıyordu. Artık her karakterin gerçek konumu korunuyor — düzenlenen paragrafın dışında tek bir piksel bile değişmiyor (Word/LibreOffice, Chrome ve reportlab çıktılarında ölçüldü).
* **Satırlar alt satıra düşüyordu:** Yeni metin özgün mürekkep genişliğine sıkıştırılıyordu; başlıklar ve tablo hücreleri bölünüp alttaki metne karışıyordu. Artık satırlar, metin aynıysa özgün yerlerinden kırılıyor; tek satırlık metin (başlık, hücre) yandaki komşu metne kadar sağa uzayabiliyor.
* **Hizalama korunuyor:** Ortalanmış satır ortasından, sağa yaslı satır sağ kenarından sabit kalıyor; ilk satır girintisi korunuyor; **iki yana yaslı** paragraflar iki yana yaslı yazılıyor (yeni “İki yana” hizalama seçeneği). Boş satırsız art arda gelen paragraflar artık ayrı paragraflar olarak tanınıyor.
* **Özgün yazı tipi:** Yazı tipini değiştirmediyseniz düzeltilen metin belgenin kendi gömülü yazı tipiyle yazılıyor — özgünüyle birebir aynı görünüyor. Yeni bir karakter o yazı tipinde yoksa en yakın sistem yazı tipine geçiliyor ve durum çubuğunda söyleniyor.
* **Art arda düzenleme:** Aynı paragraf defalarca düzenlense de birikimli kayma yok (paragraf kutusu PDF'nin içinde saklanıyor). Döndürülmüş metin kendi açısıyla yeniden yazılıyor. Yazı boyutu ve satır aralığı, arayüzde yuvarlanmış değerle değil kesin değerle kullanılıyor.

## 0.6.0 (11 Kasım 2026) — Aşama 5: Word, Excel, Office → PDF, sıkıştırma

### PDF → Microsoft Word (.docx)
* Sayfa düzeni çözümlenir: **başlıklar** (Word'ün Başlık 1/2 stilleriyle, gezinti bölmesinde görünür), paragraflar, hizalama (iki yana yaslı dahil), yazı tipi, boyut, kalın/eğik ve renkler, satır aralığı ve girinti korunur.
* **Tablolar** gerçek Word tablosu olur; birleşik başlık hücreleri korunur. **Resimler** yerinde eklenir (fotoğraflar JPEG, çizimler PNG). Her PDF sayfası kendi boyutu ve yönüyle (dikey/yatay) ayrı bölüm olur.
* İki sütunlu sayfalarda metin doğru okuma sırasıyla (önce sol sütun, sonra sağ) aktarılır.

### PDF → Microsoft Excel (.xlsx)
* Tablolar hücrelere, birleşik hücreler birleşik hücrelere dönüşür. **Sayılar gerçek sayıdır:** “1.250”, “3.437,50”, “%18”, “12.800,00 TL” doğrudan toplanabilir. Her sayfa ayrı çalışma sayfası veya hepsi tek sayfada; istenirse yalnız tablolar.

### Taranmış belgeler de dönüşür
* Metni olmayan taranmış sayfalar dönüştürmeden önce **otomatik tanınır** (OCR). Tanıma belgenin geçici bir kopyasında yapılır; açık belge ve diskteki dosya değişmez.

### Office belgesinden PDF
* Word, Excel, PowerPoint ve OpenDocument dosyaları PDF'ye çevrilir: bilgisayarınızda **Microsoft Office** varsa o kullanılır (görünmez), yoksa ücretsiz **LibreOffice**. Office dosyalarını pencereye sürükleyip bırakabilir veya “Aç” ile seçebilirsiniz. İkisi de kurulu değilse ne yapmanız gerektiği açıkça söylenir. Dosyalar hiçbir çevrim içi hizmete gönderilmez.

### PDF'yi sıkıştır
* Görseller seçilen çözünürlüğe (220 / 150 / 100 DPI) küçültülüp yeniden kodlanır; isteğe bağlı gri tonlama. Metin, yazı tipleri, bağlantılar ve yer imleri olduğu gibi kalır. Saydam görseller ve siyah-beyaz taramalar bozulmasın diye değiştirilmez. Sonuç yeni bir dosyaya yazılır; özgün dosya korunur, kazanç yoksa hiçbir şey yazılmaz.

### Açılış ekranı
* Uygulama açılırken Mesly PDF Reader logosu ve “Powered by Mesly” yazısıyla açılış ekranı görünür; ana pencere hazır olunca yumuşakça kaybolur.

### Nerede?
* **Dosya → Dışa aktar → Word / Excel**, **Dosya → Office belgesinden PDF**, **Dosya → PDF'yi sıkıştır**; Belge sekmesinde **Dönüştür** menüsü; Yeni Görev ekranında dört yeni görev kutusu.

### Dürüst sınırlamalar
* Yalnız **çizgili** tablolar Word tablosuna dönüşür; çizgisiz (boşlukla hizalanmış) tablolar Word'de paragraf olarak kalır.
* Çok sütunlu sayfalar Word'ün sütun özelliğiyle değil, sırayla akan metin olarak yazılır.
* PDF → PowerPoint ve PDF → HTML henüz yok.
* Office → PDF için Microsoft Office veya LibreOffice kurulu olmalıdır (uygulamayla dağıtılmaz). Parola korumalı Office belgeleri çevrilemez; açık bir hata verilir.

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
