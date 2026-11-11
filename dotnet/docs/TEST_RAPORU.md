# Mesly PDF Reader — Test Raporu

## 0.4.0 (Aşama 3: gerçek PDF düzenleme) — GitHub Actions çalışması `43028853171`

| Test grubu | Ortam | Sonuç |
|---|---|---|
| Birim testleri (119 test; yeni: 14 düzenleme motoru + 6 yazı tipi eşleme testi) | Linux ve Windows | ✅ 119 / 119 |
| Arayüz öz testi (37 adım; yeni: 5 düzenleme adımı) | Tek dosya exe, Windows | ✅ 37 / 37 |
| Kurulu uygulamada öz test | Windows Server 2022 / 2025 | ✅ 37 / 37 · ✅ 37 / 37 |

**Düzenleme testlerinin denetledikleri (her biri kaydedip yeniden açarak):**
* Üçüncü taraf (reportlab) PDF'de satır başına ayrı metin nesnesi olan 3 satırlık paragraf tek paragraf olarak algılanır; satır aralığı 14 pt doğru ölçülür.
* Paragraf değiştirilince eski metin nesneleri içerik akışından **silinir** (eski metin aranınca bulunmaz), yeni metin kutu genişliğinde birden çok satıra akar, Türkçe karakterleri birebir içerir ve aranabilir; sayfaya **beyaz dolgulu nesne eklenmediği** ayrıca denetlenir; başlık, görsel ve diğer paragraf korunur.
* Ortalı/sağa hizalı metin kutusu konumu, altı çizgi nesnesi; elips (Bézier) ve ok yolları, çizgi/dolgu renkleri; metin rengi değişimi.
* Görsel taşıma (±0,1 pt), dikdörtgen boyutlandırma, sola hizalama, silme; görsel ekleme (orantılı sığdırma) ve değiştirme (yeni en-boy oranı).
* Bağlantı: `javascript:` reddedilir; https bağlantısı eklenir, okunur, noktadan kaldırılır.
* Filigran 90° döndürülmüş sayfa dahil sayfa merkezinde; paragraf listesine karışmaz; 3 sayfadan kaldırılınca 3 nesne silinir.
* Üst/altbilgi: “Sayfa 2 / 3” (seçilen sayfalara göre numaralama), dosya adı, alt ortada 20–40 pt yükseklik; kaldırma 6 nesne.
* Arka plan sayfanın ilk (en arkadaki) nesnesi; ikinci kez uygulanınca öncekinin yerine geçer; çizimde köşe pikseli arka plan rengi.
* Anlık görüntüden geri yükleme düzenlemeyi tamamen geri alır.
* Gerçek Windows'ta: 131 kurulu yazı tipi dizinlendi; “ABCDEF+Arial-BoldMT” → Arial Kalın (tam eşleşme); kurulu olmayan yazı tipi → Arial + kullanıcıya not.

**Bu sürümde testle bulunup düzeltilen hata:** yerinde metin düzenleyici açıkken ölçme ve yerleştirme farklı genişlik kullandığı için sonsuz yerleşim döngüsü oluşuyordu (öz test “arayüz 5 sn boşta kalmadı” diye yakaladı). Ölçme ve yerleştirme aynı dikdörtgeni kullanacak şekilde düzeltildi.

Ekran görüntüleri: `ekran-goruntuleri/13-duzenleme-paragraf.png`, `14-duzenleme-yerinde.png`, `15-duzenleme-nesneler.png`, `16-filigran-sayfa-no.png`.

**Test edilemeyen:** fareyle sürükleme/boyutlandırma hareketleri CI'da gerçek fare olmadan çalıştırılamaz; aynı işlemler (taşıma, boyutlandırma, yerleştirme) öz testte doğrudan ViewModel üzerinden ve birim testlerinde motor üzerinden doğrulandı. Fare etkileşimini kendi bilgisayarınızda denemeniz gerekir.

## 0.3.0 (tarama + hızlı kurulum) — GitHub Actions çalışması `43001623137`

| Test grubu | Ortam | Sonuç |
|---|---|---|
| Birim testleri (99 test; yeni: 10 tarama iş mantığı testi) | Linux ve Windows | ✅ 99 / 99 |
| Arayüz öz testi (32 adım; yeni: 3 tarama adımı) | Tek dosya exe, Windows NT 10.0.26100 | ✅ 32 / 32 |
| Kurulu uygulamada öz test | Windows Server 2022 / 2025 | ✅ 32 / 32 · ✅ 32 / 32 |

**Kurulum süresi (aynı makinede, aynı sürümün iki paketi art arda, sessiz kurulum):**

| Makine | Eski paket (284 dosya) | Yeni paket (13 dosya) | Fark |
|---|---|---|---|
| Windows Server 2022 (Windows 10 çekirdeği) | 7,3 sn | 5,4 sn | −%26 |
| Windows Server 2025 (Windows 11 çekirdeği) | 8,1 sn | 5,7 sn | −%30 |

CI sunucularında Windows Defender gerçek zamanlı taraması kişisel bilgisayarlardakinden hafif çalışır; dosya başına tarama maliyeti yüksek olan masaüstü Windows 11'de farkın daha büyük olması beklenir (kişisel bilgisayarda ölçülmedi).

**Açılış süresi (pencere ilk kez çizilip boşta kalana kadar):** Server 2022: ilk 1,35 sn, sonraki açılışlar ≈0,6 sn · Server 2025: ilk 1,11 sn, sonraki ≈0,94 sn.

**Tarama — dürüst kapsam:** CI makinelerinde tarayıcı yok. Test edilenler: tarama iş mantığı (düz cam tek sayfa, besleyici kâğıt bitene kadar, çift taraf, boş besleyici hatası, sıkışmada taranan sayfaların korunması, iptal — sahte tarayıcıyla), Windows WIA hizmetiyle iletişim ve cihaz yokken anlaşılır mesaj, tarama penceresinin açılması, taranan JPEG/PNG sayfalardan döndürmeli PDF üretimi. **Gerçek bir tarayıcıyla düz cam / besleyici taraması henüz doğrulanmadı.**

---

## 0.2.0

Bu rapordaki her sonuç **gerçekten çalıştırılmış** testlerden alınmıştır. Çalıştırılmamış veya atlanan hiçbir test başarılı gösterilmez.

| Test grubu | Ortam | Sonuç |
|---|---|---|
| Birim testleri (xUnit, 89 test) | Linux (Ubuntu 24.04, geliştirme ortamı) | ✅ 89 / 89 |
| Birim testleri (xUnit, 89 test) | Windows (GitHub `windows-latest`, Windows NT 10.0.26100) | ✅ 89 / 89 |
| Arayüz öz testi — yayınlanmış exe, gerçek WPF penceresi | Windows NT 10.0.26100 | ✅ 29 / 29 |
| Kurulum testi + kurulu uygulamada öz testi | Windows Server 2022 (NT 10.0.20348, Windows 10 çekirdeği) | ✅ kurulum, entegrasyon, 29 / 29, PDF ile açılış, kaldırma |
| Kurulum testi + kurulu uygulamada öz testi | Windows Server 2025 (NT 10.0.26100, Windows 11 çekirdeği) | ✅ kurulum, entegrasyon, 29 / 29, PDF ile açılış, kaldırma |

Kaynak: GitHub Actions çalışması `42997340711` (dal `dotnet-wpf`), iş akışı `.github/workflows/dotnet-windows.yml`. Ekran görüntüleri ve JSON sonuçları çalışmanın artefaktlarındadır.

## Testlerle bulunup düzeltilen hatalar

Testler yazıldıktan sonra **başarısız olan** ve kodda düzeltilen gerçek hatalar:

| Hata | Nasıl bulundu | Düzeltme |
|---|---|---|
| Kaydetme hedefe taşınamazsa (ör. hedef kullanımda) kaydedilmemiş değişiklikler kayboluyordu | `FailedSaveKeepsOriginalIntact` | Belge, değişiklikleri içeren geçici dosyadan açık kalır; özgün dosya değişmez |
| “Aksanları yok say” Türkçede çalışmıyordu (ICU, ş/ç/ğ'yi ayrı harf sayar) | `IgnoreDiacriticsMatchesSCedilla` | Konum koruyan Türkçe harf katlama |
| Yakınlaştırma tam %800 / %100 gibi bir liste değerine gelince arayüz sonsuz döngüye giriyordu | Öz test: arayüz iş parçacığı boşta kalmadı | Açılır liste metni korumalı güncelleniyor, metinle otomatik seçim kapalı |
| Sayfa silindikten hemen sonra sekmedeki sayfa sayısı gecikmeli güncelleniyordu | Öz test: “40 → 40 → 38” | Düzenleme bitince eşzamanlı yenileme |
| Silinen sayfalara ait yer imleri dosyada hedefsiz kalıyordu | Öz test + `BookmarkPruneTests` | Bu yer imleri kaldırılır, alt yer imleri bir üst düzeye taşınır |
| PDFsharp, PDFium'un sayfa silme sonrası bıraktığı null hedeflerde çöküyordu | `DeletingPagesRemovesTheirBookmarksAndLiftsChildren` | Eski ağaç okunmadan katalogdan atılıp yeniden yazılır |
| **Güvenlik:** sahip parolasıyla açılan şifreli belgede yer imi kaydı parola korumasını kaldırıyordu | `OwnerPasswordRewritesBookmarksAndKeepsEncryption` | Şifreli belgelerde yer imi düzenleme açık bir mesajla reddedilir; sayfa işlemleri şifreyi korur (testli) |
| Çift sayfa düzenine geçişte “genişliğe sığdır” yeniden hesaplanmıyordu | Öz test ekran görüntüsü + `sideBySide` denetimi | Düzen değişince sığdırma kipi yeniden uygulanır |

## Performans ölçümleri (gerçek)

| Ölçüm | Linux birim testi | Windows öz testi |
|---|---|---|
| 1.000 sayfalık PDF açılışı | 31 ms | açılış + ilk sayfa çizimi 178 ms |
| 1.000 sayfada tam metin arama | 99 ms | — |
| 40 rastgele sayfa çizimi (300 px) | 19 ms | — |
| 300 sayfa çizimi sonrası bellek artışı | −15 MB (artış yok) | 20 noktaya atlayıp son sayfaya gitme sonrası +156 MB (önbellek bütçesi içinde: 112 MB / 42 görüntü) |
| 500 sayfa silme + taşıma + kaydetme | 86 ms | — |
| 40 sayfada Türkçe arama | — | 138 ms |

## Arayüz öz testi (Windows, yayınlanmış exe)

29 adım · **29 geçti** · 0 başarısız · 34,4 sn

| Alan | Test | Beklenen | Gerçek | Durum | Süre |
|---|---|---|---|---|---|
| Arayüz | Ana ekran açılır | Başlangıç sekmesi görünür, belge yok | IsHome=True, görünür=True | ✅ GEÇTİ | 97 ms |
| Hazırlık | 40 sayfalık Türkçe PDF üret | 40 sayfa, yer imleri, gömülü yazı tipi | 563 KB | ✅ GEÇTİ | 9304 ms |
| Okuyucu | PDF'yi sekmede aç | Sekme açılır, 40 sayfa, sayfalar çizilir | sekme=1, sayfa=40, çizildi=True, önbellek 5 MB / 2 görüntü, bekleyen 0, motor kuyruğu 0 | ✅ GEÇTİ | 398 ms |
| Okuyucu | Küçük resimler ve yer imleri | 40 küçük resim, 40 kök yer imi + alt yer imi | küçük resim=40, yer imi=40, alt=1 | ✅ GEÇTİ | 2 ms |
| Okuyucu | Sayfaya git | Sayfa 21'e gidilir ve görünür | geçerli=21, görünen=21 | ✅ GEÇTİ | 164 ms |
| Okuyucu | Yakınlaştırma | Genişliğe sığdır → yakınlaştır: oran artar, kip Özel olur | 0,89 → 1,00, kip=Custom | ✅ GEÇTİ | 305 ms |
| Okuyucu | Yüksek yakınlaştırma (%800) | Bölge çizimi ile görüntülenir, hata yok | önbellek 16 MB / 6 görüntü, bekleyen 0, motor kuyruğu 0 | ✅ GEÇTİ | 470 ms |
| Arama | Türkçe büyük/küçük harf duyarsız arama | “istanbul” 40 sayfada 40 sonuç bulur | 40 sonuç, durum: 40 sonuç · 40 sayfada · 138 ms | ✅ GEÇTİ | 800 ms |
| Arama | Aksan duyarsız arama | “sofore” → “şoföre” her sayfada bulunur | 40 sonuç | ✅ GEÇTİ | 121 ms |
| Okuyucu | Metin seçme | Tüm belge seçilir, seçili metin Türkçe karakterleri birebir içerir | 113829 karakter seçildi | ✅ GEÇTİ | 108 ms |
| Okuyucu | Panoya kopyalama | Pano metni Türkçe karakterleri içerir | kopyalandı=True, pano=113829 karakter | ✅ GEÇTİ | 109 ms |
| Görünüm | Çift sayfa düzeni | Yan yana iki sayfa | çift: 6 sayfa görünür, genişliğe sığdı=True, tek: 1 | ✅ GEÇTİ | 532 ms |
| Görünüm | Görünüm döndürme | Görünüm 90° döner, belge değişmez | döndürme=0, değişti=False | ✅ GEÇTİ | 382 ms |
| Görünüm | Koyu tema ve karanlık okuma | Tema koyu olur, sayfalar koyu zeminde çizilir | koyu tema ve karanlık okuma ekran görüntüleri alındı | ✅ GEÇTİ | 820 ms |
| Sayfalar | Sayfa döndürme (kalıcı) | Sayfa 1 yatay olur, belge değişti olarak işaretlenir | yatay=True, değişti=True | ✅ GEÇTİ | 425 ms |
| Sayfalar | Sayfa silme + geri al + yinele | 40 → 38 → 40 → 38 | 38 → 40 → 38 | ✅ GEÇTİ | 508 ms |
| Sayfalar | Sayfa çoğaltma ve taşıma | Çoğaltma +1 sayfa; son sayfa başa taşınır | çoğaltma sonrası=39, ilk sayfa: Bölüm 40: İSTANBUL Çalışma Raporu | ✅ GEÇTİ | 126 ms |
| Sayfalar | Kaydet ve yeniden aç | Kaydedilen dosya 39 sayfa, yer imleri korunur | sayfa=39, yer imi=38, hepsi geçerli=True, değişti=False | ✅ GEÇTİ | 160 ms |
| Dönüştürme | PDF → PNG / JPG / TIFF / BMP | Dört dosya oluşur, boyutları 150 DPI'ya uygun | .png 1754×1240, .jpg 1754×1240, .tif 1754×1240, .bmp 1754×1240, çok sayfalı TIFF: 3 kare | ✅ GEÇTİ | 395 ms |
| Dönüştürme | Görüntü → PDF | JPG ve PNG'den 2 sayfalık PDF; JPEG yeniden sıkıştırılmaz | sayfa=2, JPEG doğrudan=True, 491 KB | ✅ GEÇTİ | 82 ms |
| Dönüştürme | PDF → TXT | UTF-8 BOM'lu, Türkçe karakterli metin | 125 KB | ✅ GEÇTİ | 98 ms |
| Sayfa yönetimi | Birleştirme sekmede açılır | İki belge birleşir, ikinci sekme açılır | sekme=2, birleşik sayfa=7 | ✅ GEÇTİ | 303 ms |
| Sayfa yönetimi | Bölme | 39 sayfa her 10 sayfada bölünür → 4 dosya | parca_01 (s. 1-10).pdf, parca_02 (s. 11-20).pdf, parca_03 (s. 21-30).pdf, parca_04 (s. 31-39).pdf | ✅ GEÇTİ | 93 ms |
| Yazdırma | Yazdırma sayfalayıcısı (XPS) | Tüm sayfalar XPS'e yazılır | 39 sayfa, 15989 KB | ✅ GEÇTİ | 3423 ms |
| Yazdırma | Yazdırma önizlemesi penceresi | Önizleme açılır, yazıcı listelenir, Yazdır düğmesi etkin | yazıcı sayısı=1, seçili=“Microsoft Print to PDF”, Yazdır etkin=True | ✅ GEÇTİ | 984 ms |
| Ayarlar | Ayarlar penceresi | Açılır, tüm bölümler oluşturulur | ekran görüntüsü alındı | ✅ GEÇTİ | 734 ms |
| Veri | Son dosyalar ve son sayfa | Örnek belge geçmişte; son sayfa hatırlanır | son sayfa=13, geçmiş=1 | ✅ GEÇTİ | 3 ms |
| Performans | 1.000 sayfalık belge | Hızlı açılır, son sayfaya gidilir, bellek sınırlı | açılış+ilk çizim 178 ms, sayfa=1000, geçerli=1000, bellek artışı 156 MB, önbellek 112 MB / 42 görüntü, bekleyen 0, motor kuyruğu 0 | ✅ GEÇTİ | 12963 ms |
| Arayüz | Sekmeleri kapatma | Değişmemiş sekmeler soru sormadan kapanır | kalan (kaydedilmemiş) sekme=1 | ✅ GEÇTİ | 63 ms |

Aynı 29 adım, kurulum paketinden kurulan uygulamada Windows Server 2022 (29 adım · 29 geçti · 0 başarısız · 30,4 sn
) ve Windows Server 2025 (29 adım · 29 geçti · 0 başarısız · 37,0 sn
) üzerinde de çalıştırıldı.

## Kurulum testi (Windows Server 2022 ve 2025)

| Adım | Beklenen | Sonuç |
|---|---|---|
| Sessiz kurulum (`/VERYSILENT /CURRENTUSER`) | Çıkış kodu 0, `%LOCALAPPDATA%\Programs\Mesly PDF Reader\MeslyPdfReader.exe` var | ✅ |
| ProgID ve açma komutu | `HKCU\Software\Classes\MeslyPdfReader.Document\shell\open\command` | ✅ |
| Gezgin sağ tık menüsü | `SystemFileAssociations\.pdf\shell\MeslyPdfReader.Open` | ✅ |
| Birlikte aç / Varsayılan uygulamalar | `.pdf\OpenWithProgids`, `Capabilities\FileAssociations` | ✅ |
| Başlat menüsü ve masaüstü kısayolu | `.lnk` dosyaları oluşur | ✅ |
| Kurulu uygulamada öz test | 29 / 29 | ✅ |
| PDF yoluyla başlatma (çift tıklama eşdeğeri) | Uygulama açılır, kapanmaz | ✅ |
| Sessiz kaldırma | exe ve ProgID kaldırılır | ✅ |

## Birim testleri (Windows, 89 / 89)

**Silinen sayfaların yer imleri** (`BookmarkPruneTests`)

| Test | Sonuç | Süre |
|---|---|---|
| `DeletingPagesRemovesTheirBookmarksAndLiftsChildren` | ✅ Geçti | 1519 ms |
| `DeletingUnrelatedPagesKeepsOutlineUntouched` | ✅ Geçti | 30 ms |

**Oluşturma, kaydetme, yer imi, geri alma** (`CreateEditSaveTests`)

| Test | Sonuç | Süre |
|---|---|---|
| `BookmarksAreWrittenAndRemapped` | ✅ Geçti | 1474 ms |
| `FailedSaveKeepsOriginalIntact` | ✅ Geçti | 228 ms |
| `HitTestAndSelectionRects` | ✅ Geçti | 99 ms |
| `SaveOverOpenFileIsAtomicAndReloads` | ✅ Geçti | 159 ms |
| `SearchFindsTurkishTextAcrossPages` | ✅ Geçti | 521 ms |
| `SnapshotsRestorePreviousState` | ✅ Geçti | 256 ms |
| `WritesRealTextObjectsWithTurkishFont` | ✅ Geçti | 88 ms |

**SQLite veri katmanı** (`DataTests`)

| Test | Sonuç | Süre |
|---|---|---|
| `ConcurrentAccessIsSafe` | ✅ Geçti | 1353 ms |
| `DocumentStateRemembersLastPage` | ✅ Geçti | 78 ms |
| `RecentFilesOrderPinAndClear` | ✅ Geçti | 207 ms |
| `SettingsRoundTrip` | ✅ Geçti | 187 ms |
| `TaskHistory` | ✅ Geçti | 67 ms |

**Şifreli belgede parola korumasının korunması** (`EncryptedBookmarkTests`)

| Test | Sonuç | Süre |
|---|---|---|
| `BookmarkEditingIsRefusedToProtectEncryption(password: "açık şifre")` | ✅ Geçti | 187 ms |
| `BookmarkEditingIsRefusedToProtectEncryption(password: "sahip")` | ✅ Geçti | 130 ms |

**Dışa aktarma ve görüntüden PDF** (`ExportTests`)

| Test | Sonuç | Süre |
|---|---|---|
| `ExportsPngThatIsValid` | ✅ Geçti | 121 ms |
| `ExportsTextAsUtf8WithBom` | ✅ Geçti | 205 ms |
| `JpegAndBitmapImagesToPdf` | ✅ Geçti | 136 ms |

**Ekran ↔ sayfa dönüşümü (PDFium ile karşılaştırma)** (`GeometryTests`)

| Test | Sonuç | Süre |
|---|---|---|
| `MatchesPdfiumDisplayMatrix(pageRotation: 0, viewTurns: 0)` | ✅ Geçti | 92 ms |
| `MatchesPdfiumDisplayMatrix(pageRotation: 0, viewTurns: 1)` | ✅ Geçti | 67 ms |
| `MatchesPdfiumDisplayMatrix(pageRotation: 1, viewTurns: 0)` | ✅ Geçti | 178 ms |
| `MatchesPdfiumDisplayMatrix(pageRotation: 1, viewTurns: 3)` | ✅ Geçti | 85 ms |
| `MatchesPdfiumDisplayMatrix(pageRotation: 2, viewTurns: 0)` | ✅ Geçti | 105 ms |
| `MatchesPdfiumDisplayMatrix(pageRotation: 2, viewTurns: 2)` | ✅ Geçti | 162 ms |
| `MatchesPdfiumDisplayMatrix(pageRotation: 3, viewTurns: 0)` | ✅ Geçti | 188 ms |

**1.000 sayfa: hız ve bellek** (`LargeDocumentTests`)

| Test | Sonuç | Süre |
|---|---|---|
| `MemoryStaysBoundedWhileRenderingManyPages` | ✅ Geçti | 31606 ms |
| `OpensQuicklyAndRendersRandomPages` | ✅ Geçti | 33 ms |
| `PageOperationsOnLargeDocument` | ✅ Geçti | 110 ms |
| `SearchesWholeDocument` | ✅ Geçti | 197 ms |

**Açma, sürümler, şifreli ve hasarlı dosyalar** (`OpenAndReadTests`)

| Test | Sonuç | Süre |
|---|---|---|
| `CorruptFilesGiveClearErrors(file: "garbage.pdf", kind: Format)` | ✅ Geçti | 12 ms |
| `CorruptFilesGiveClearErrors(file: "notpdf.pdf", kind: Format)` | ✅ Geçti | 44 ms |
| `CorruptFilesGiveClearErrors(file: "yok-boyle-dosya.pdf", kind: File)` | ✅ Geçti | 9 ms |
| `EmptyFileIsRejected` | ✅ Geçti | 45 ms |
| `EncryptedDocumentStaysEncryptedAfterEditAndSave` | ✅ Geçti | 528 ms |
| `EncryptedNeedsPassword(file: "encrypted_aes256.pdf", password: "açık şifre")` | ✅ Geçti | 68 ms |
| `EncryptedNeedsPassword(file: "encrypted_rc4.pdf", password: "1234")` | ✅ Geçti | 54 ms |
| `ExtractsTurkishCharactersExactly` | ✅ Geçti | 59 ms |
| `OpensDifferentVersions(file: "links_outline.pdf", pages: 5, version: 14)` | ✅ Geçti | 241 ms |
| `OpensDifferentVersions(file: "v13.pdf", pages: 1, version: 13)` | ✅ Geçti | 35 ms |
| `OpensDifferentVersions(file: "v17_objstreams.pdf", pages: 5, version: 17)` | ✅ Geçti | 28 ms |
| `ReadsBookmarksTree` | ✅ Geçti | 25 ms |
| `ReadsInternalAndExternalLinks` | ✅ Geçti | 55 ms |
| `ReadsMetadataInTurkish` | ✅ Geçti | 30 ms |
| `TruncatedFileIsRepairedOrRejectedWithoutCrash` | ✅ Geçti | 18 ms |

**Sayfa işlemleri, birleştirme, bölme** (`PageOperationTests`)

| Test | Sonuç | Süre |
|---|---|---|
| `CropAndResize` | ✅ Geçti | 51 ms |
| `CropOnRotatedPageUsesDisplayedEdges` | ✅ Geçti | 56 ms |
| `DeletePages` | ✅ Geçti | 91 ms |
| `ExtractMergeAndSplit` | ✅ Geçti | 1506 ms |
| `ImportFromAnotherDocumentAndFile` | ✅ Geçti | 319 ms |
| `InsertBlankAndDuplicate` | ✅ Geçti | 233 ms |
| `MergeReportsWhichFileFailed` | ✅ Geçti | 170 ms |
| `MovePagesLikeDragAndDrop` | ✅ Geçti | 292 ms |
| `RotatePagesSwapsSizeAndPersists` | ✅ Geçti | 1453 ms |

**Sayfa sırası / bölme planı** (`PageOrderTests`)

| Test | Sonuç | Süre |
|---|---|---|
| `ComputesMove(count: 3, sel: [1], before: 1, expected: [0, 1, 2])` | ✅ Geçti | 0 ms |
| `ComputesMove(count: 4, sel: [0], before: 4, expected: [1, 2, 3, 0])` | ✅ Geçti | 0 ms |
| `ComputesMove(count: 4, sel: [3], before: 0, expected: [3, 0, 1, 2])` | ✅ Geçti | 0 ms |
| `ComputesMove(count: 5, sel: [0, 1], before: 3, expected: [2, 0, 1, 3, 4])` | ✅ Geçti | 2 ms |
| `ComputesMove(count: 5, sel: [1, 3], before: 0, expected: [1, 3, 0, 2, 4])` | ✅ Geçti | 0 ms |
| `PaperSizeDescription` | ✅ Geçti | 0 ms |
| `SplitPlans` | ✅ Geçti | 2 ms |

**Sayfa aralığı ayrıştırma** (`PageRangeTests`)

| Test | Sonuç | Süre |
|---|---|---|
| `FormatsCompactly` | ✅ Geçti | 7 ms |
| `Parses(text: "", count: 2, expected: [0, 1])` | ✅ Geçti | 0 ms |
| `Parses(text: "-2", count: 10, expected: [0, 1])` | ✅ Geçti | 0 ms |
| `Parses(text: "1,1,2", count: 3, expected: [0, 1])` | ✅ Geçti | 0 ms |
| `Parses(text: "1-3, 5", count: 10, expected: [0, 1, 2, 4])` | ✅ Geçti | 0 ms |
| `Parses(text: "2-son", count: 4, expected: [1, 2, 3])` | ✅ Geçti | 87 ms |
| `Parses(text: "3-1", count: 10, expected: [2, 1, 0])` | ✅ Geçti | 0 ms |
| `Parses(text: "8-", count: 10, expected: [7, 8, 9])` | ✅ Geçti | 0 ms |
| `Parses(text: "TÜMÜ", count: 3, expected: [0, 1, 2])` | ✅ Geçti | 0 ms |
| `Parses(text: "son", count: 4, expected: [3])` | ✅ Geçti | 0 ms |
| `Parses(text: "tek", count: 5, expected: [0, 2, 4])` | ✅ Geçti | 0 ms |
| `Parses(text: "çift", count: 5, expected: [1, 3])` | ✅ Geçti | 1 ms |
| `RejectsInvalid(text: "0", count: 5)` | ✅ Geçti | 0 ms |
| `RejectsInvalid(text: "1-99", count: 5)` | ✅ Geçti | 0 ms |
| `RejectsInvalid(text: "6", count: 5)` | ✅ Geçti | 0 ms |
| `RejectsInvalid(text: "abc", count: 5)` | ✅ Geçti | 1 ms |

**Çizim, iptal, eşzamanlılık** (`RenderTests`)

| Test | Sonuç | Süre |
|---|---|---|
| `CancelledRenderIsSkipped` | ✅ Geçti | 49 ms |
| `ParallelRequestsFromManyThreadsAreSafe` | ✅ Geçti | 118 ms |
| `RendersPageWithInk` | ✅ Geçti | 68 ms |
| `RendersRegionAndRotation` | ✅ Geçti | 114 ms |

**Türkçe arama kuralları** (`TurkishSearchTests`)

| Test | Sonuç | Süre |
|---|---|---|
| `CaseInsensitiveTurkish(text: "IŞIK ışık", q: "ışık", count: 2)` | ✅ Geçti | 0 ms |
| `CaseInsensitiveTurkish(text: "istanbul", q: "İSTANBUL", count: 1)` | ✅ Geçti | 0 ms |
| `CaseInsensitiveTurkish(text: "ÇĞÖÜ çğöü", q: "çğöü", count: 2)` | ✅ Geçti | 1 ms |
| `CaseInsensitiveTurkish(text: "İSTANBUL ve Iğdır", q: "istanbul", count: 1)` | ✅ Geçti | 0 ms |
| `CaseInsensitiveTurkish(text: "ŞOFÖR şoför", q: "şoför", count: 2)` | ✅ Geçti | 0 ms |
| `IgnoreDiacriticsMatchesSCedilla` | ✅ Geçti | 1 ms |
| `MatchCaseAndWholeWord` | ✅ Geçti | 1 ms |
| `TurkishDotlessIIsNotI` | ✅ Geçti | 3 ms |

## Test edilemeyenler / sınırlar

* **Gerçek yazıcıya çıktı:** CI'da fiziksel yazıcı yok. Yazdırma sayfalayıcısı XPS dosyasına yazdırılarak (39 sayfa) ve önizleme penceresi açılarak test edildi; “Microsoft Print to PDF” yazıcısı listelendi ve Yazdır düğmesi etkin.
* **Gezgin'de fareyle çift tıklama:** Kayıt defteri kayıtları ve aynı komut satırıyla başlatma test edildi; Gezgin arayüzü otomasyonu yapılmadı.
* **Fiziksel Windows 10 / 11 masaüstü:** Windows Server 2022 (Windows 10 çekirdeği) ve 2025 (Windows 11 çekirdeği) üzerinde test edildi. Windows 11'deki Mica arka planı ekran görüntülerinde görünmez (görüntüler pencere içeriğinden alınır).
* **Yüksek DPI (%150–%200) ekran:** CI ekranı %100. Çizim cihaz pikseline göre yapılır; farklı DPI'da elle doğrulama önerilir.
* **Fare ile sürükle-bırak sayfa sıralama:** Taşıma işlemi (`MovePagesAsync`) birim ve öz testte doğrulandı; fare hareketinin kendisi otomasyonla test edilmedi.
