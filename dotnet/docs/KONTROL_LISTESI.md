# Mesly PDF Reader — Özellik Kontrol Listesi

Durumlar: ✅ çalışıyor ve testle doğrulandı · 🟡 kısmen / sınırlı · ⏳ planlandı (aşama numarası) · ⛔ bu sürümde yok
Test kaynakları: **B** = birim testi (`dotnet/tests`, Linux + Windows), **Ö** = Windows'ta gerçek pencerede arayüz öz testi (`--selftest`), **K** = CI kurulum testi (Windows Server 2022 + 2025).

Sürüm 0.5.0 — Aşama 1–4 tamamlandı: okuyucu, sayfa yönetimi, tarama, gerçek PDF düzenleme, OCR ve görüntü düzeltme.

## 2. Teknoloji ve mimari
| Madde | Durum | Not |
|---|---|---|
| C# / .NET 10 / WPF | ✅ | `net10.0-windows`, self-contained x64 |
| MVVM, modüler katmanlı yapı | ✅ | Core / Pdf / Data / App; CommunityToolkit.Mvvm, DI |
| Lisansı uygun PDF motoru | ✅ | PDFium (BSD/Apache), kendi P/Invoke katmanı |
| PDF/OCR/dönüştürme arayüzden bağımsız servis | ✅ | Pdf ve Data `net10.0`, WPF'siz test edilir |
| SQLite: ayarlar, geçmiş | ✅ B Ö | |
| Arka plan / iptal edilebilir / asenkron | ✅ B Ö | Tek iş parçacıklı PDF motoru, öncelikli kuyruk, CancellationToken |
| İnternetsiz çalışma | ✅ | Hiçbir özellik ağ kullanmıyor |
| Tesseract, OpenCV | ⏳ 4 | |
| Kurulum paketi (EXE) | ✅ K | Inno Setup, kullanıcı başına, yönetici izni gerekmez; tek dosya yayını (286 → ~11 dosya) ile hızlı kurulum |
| Ticari dağıtıma uygun bağımlılıklar | ✅ | `THIRD_PARTY_NOTICES.md` |

## 3. Arayüz
| Madde | Durum | Not |
|---|---|---|
| Windows 11 Fluent tasarım, Mica | ✅ Ö | WPF-UI 4.3; Windows 10'da düz arka plan |
| Açık / koyu tema, sistem teması | ✅ Ö | Antrasit, beyaz, yumuşak mavi |
| Tüm ekranlar Türkçe, Türkçe karakter | ✅ B Ö | tr-TR kültürü |
| DPI / ölçekleme | ✅ | Per-Monitor V2 bildirimi, cihaz pikseline hizalı çizim |
| Klavye kısayolları | ✅ | Ayarlar → Klavye kısayolları listesi |
| Erişilebilirlik | 🟡 | Klavye ile gezinme, ipuçları; ekran okuyucu etiketleri (AutomationProperties) eksik |
| Sürükle-bırak ile açma | ✅ | Pencereye, ana ekrana, küçük resimlere |
| Sekmeli çoklu belge | ✅ Ö | Orta tık ile kapatma, Ctrl+Tab |
| Ana ekran: aç, PDF oluştur, dönüştür, birleştir | ✅ Ö | |
| Ana ekran: Tara | ✅ Ö | |
| Ana ekran: OCR, belge karşılaştır | ⏳ 4 / 7 | Henüz çalışmadığı için düğmeleri gösterilmiyor |
| Son belgeler, hızlı işlemler, bırakma alanı | ✅ Ö | Sabitleme, klasörde göster, listeden kaldır |
| Editör: sol panel küçük resim / yer imi / arama | ✅ Ö | |
| Editör: katmanlar paneli | ⏳ | Aşama 6 ile (açıklamalar) birlikte |
| Üst menü (gruplu şerit): Dosya, Okuma, Düzenle, Tanıma (OCR), Sayfalar, Dönüştür | ✅ Ö | 1024 px genişlikte taşmadığı öz testte denetlenir; Düzenle sekmesi düzenleme kipini açar |
| Üst menü: Açıklamalar, Formlar, Güvenlik | ⏳ 6 | Çalışmayan menü olmaması için gizli |
| Sağ panel: özellikler | ✅ Ö | Belge, sayfa ve seçili nesne (metin biçimi, konum/boyut, şekil renkleri) |
| Durum çubuğu: sayfa, toplam, yakınlaştırma, boyut, durum | ✅ Ö | |

## 4. PDF okuyucu
| Madde | Durum | Test |
|---|---|---|
| Açma / görüntüleme / çok sayfa | ✅ | B Ö |
| Büyük PDF'yi performanslı açma (1.000 sayfa) | ✅ | B Ö |
| Yakınlaştırma, sayfaya / genişliğe sığdır, %800'e kadar | ✅ | Ö (yüksek yakınlaştırmada bölge çizimi) |
| Tek / çift sayfa, sürekli kaydırma, kitap görünümü | ✅ | Ö |
| Tam ekran | ✅ | F11 / Esc |
| Metin seçme ve kopyalama | ✅ | B Ö |
| Gelişmiş arama (Türkçe kurallı, tam sözcük, büyük/küçük, aksan duyarsız) | ✅ | B Ö |
| Yer imi ekleme, alt yer imi, yeniden adlandırma, silme, taşıma | ✅ | B Ö (PDF'e yazılır) |
| Bağlantıları açma (iç / dış, onaylı) | ✅ | B |
| Sayfaya git | ✅ | Ö |
| Sayfa döndürme (görünüm ve kalıcı) | ✅ | B Ö |
| Yazdırma, yazdırma önizlemesi | ✅ | Ö (XPS çıktısı ve önizleme penceresi) — gerçek yazıcıya çıktı CI'da test edilemez |
| Karanlık okuma modu | ✅ | Ö |
| Son kalınan sayfayı hatırlama | ✅ | B Ö |
| Parolalı PDF açma | ✅ | B |
| Form alanlarının etkileşimli çizimi | ⏳ 6 | Görünüm akışları çizilir; doldurma Aşama 6 |

## 5. PDF düzenleyici — ✅ Aşama 3 (0.4.0)
Tüm düzenlemeler sayfanın gerçek içerik akışına uygulanır: nesneler silinir/taşınır/yeniden yazılır, sayfa içeriği yeniden üretilir. **Hiçbir değişiklik üstüne beyaz kutu çizilerek yapılmaz** (birim testi ve öz test bunu ayrıca denetler).

| Madde | Durum | Test |
|---|---|---|
| Metinleri paragraflara gruplama (satır ve paragraf algılama, hizalama, tire birleştirme) | ✅ | B (üçüncü taraf PDF: reportlab) |
| Mevcut metni düzenleme (çift tıklama / F2, yerinde düzenleyici) | ✅ | B Ö |
| Paragrafı yeniden akıtma (kutu genişliğine göre sözcük kaydırma; tutamaçla genişlik değiştirme) | ✅ | B Ö |
| Yazı tipi, boyut, renk, kalın, eğik, altı çizili, hizalama, satır aralığı | ✅ | B Ö — kurulu Windows yazı tiplerinden; Türkçe karakter kapsamı denetlenir |
| Özgün yazı tipini koruma | 🟡 | PDF'deki ad (ör. “Arial-BoldMT”) kurulu yazı tipine eşlenir. Gömülü **alt küme** yazı tipleri yeni karakterleri içermeyebileceği için yeniden kullanılmaz; yazı tipi kurulu değilse benzeri/yedek kullanılır ve kullanıcıya bildirilir |
| Yeni metin kutusu ekleme | ✅ | B Ö |
| Görsel ekleme, değiştirme, taşıma, boyutlandırma (orantılı / Shift ile serbest), silme | ✅ | B Ö |
| Şekil: dikdörtgen, elips, çizgi, ok; çizgi/dolgu rengi, kalınlık | ✅ | B Ö |
| Nesne taşıma (sürükleme, ok tuşları 1 pt / Shift 10 pt), silme, çoklu seçim, hizalama (6 yön) | ✅ | B Ö |
| Konum/boyutu mm olarak girme | ✅ | Ö |
| Bağlantı ekleme (http/https/mailto) ve kaldırma | ✅ | B Ö — belge içi sayfa bağlantısı ekleme ⏳ |
| Filigran (metin, açı, opaklık, renk, sayfa aralığı; döndürülmüş sayfada doğru) ve kaldırma | ✅ | B Ö |
| Üstbilgi/altbilgi (6 konum, {sayfa} {toplam} {tarih} {dosya}), sayfa numarası, kaldırma | ✅ | B Ö |
| Sayfa arka plan rengi ve kaldırma | ✅ | B Ö |
| Geri al / yinele (her düzenleme) | ✅ | B Ö |
| Form nesnesi (XObject) içindeki metni düzenleme | ⛔ | Form nesnesi bütün olarak taşınır/boyutlandırılır/silinir; içindeki metin düzenlenemez — panelde açıkça yazılır |
| Döndürülmüş metin | 🟡 | Seçilir, taşınır, silinir; yeniden yazılırsa yatay yazılır |
| Katmanlar paneli | ⏳ | |

## 6. OCR ve görüntü düzeltme — ✅ Aşama 4 (0.5.0)
Tanıma tamamen bu bilgisayarda yapılır (Tesseract 5, tessdata_best Türkçe + İngilizce); belge hiçbir yere gönderilmez.

| Madde | Durum | Test |
|---|---|---|
| Türkçe / İngilizce metin tanıma | ✅ | B (Linux: tessdata_fast) Ö (Windows: tessdata_best — 4/4 satır birebir, ort. güven %96) |
| Sayfa yönünü otomatik düzeltme (90° / 180° / 270°) | ✅ | B (üç yön) Ö — önce Tesseract yön algılama, emin değilse dört yönde hızlı tanıma karşılaştırması; PDF sayfalarında kayıpsız sayfa döndürme |
| Eğiklik düzeltme | ✅ | B (+3°, −2,2°, +0,8° ölçülüp ±0,2° içinde düzeltilir) Ö (2° eğik tarama) — boş sayfada düzeltme yapılmaz |
| Tarayıcı kenar gölgesi temizleme, arka plan beyazlatma | ✅ | B |
| Aranabilir PDF (görünmez metin katmanı, görünüm değişmez) | ✅ | B (çizim pikselleri birebir aynı; döndürülmüş sayfada konum doğru) Ö |
| Taranmış sayfada metni düzenleme | ✅ | B Ö — OCR metni değiştirilince görüntüdeki eski yazı pikselleri kâğıt rengiyle silinir, yeni metin gerçek metin olarak yazılır; üstüne kutu çizilmez |
| Tanıma doğrulama (düşük güvenli sözcükler görüntüsüyle) | ✅ | Arayüz; düzeltmeler metin katmanına yazılır |
| Taranmış sayfa algılama ve “Metni tanı” bilgi şeridi | ✅ | Ö |
| Metin katmanını kaldırma, yeniden tanıma | ✅ | B |
| Çok çekirdekli tanıma | ✅ | Aynı anda en fazla 4 sayfa (çekirdek sayısına göre) |
| Tablo / sütun yapısını Word'e aktarma | ⏳ 5 | Dönüştürme aşamasında |
| El yazısı tanıma | ⛔ | Tesseract el yazısını güvenilir tanımaz; sözde sonuç üretilmez |
| Diğer diller (Almanca, Arapça…) | ⏳ | Dil verisi eklenerek; şimdilik Türkçe + İngilizce kurulur |

**Sınırlama (dürüst):** OCR doğruluğu tarama kalitesine bağlıdır; 300 DPI ve üzeri önerilir. Tesseract kendi dil modeliyle sözcükleri düzeltir; ek olarak güveni %60'ın altındaki sözcükler “Doğrula” penceresinde kullanıcıya gösterilir. Otomatik “yazım düzeltme” ile anlamı değiştirme riski alınmaz.

## 7. Dönüştürme
| Madde | Durum | Test |
|---|---|---|
| PDF → PNG / JPG / TIFF (tek ve çok sayfalı) / BMP | ✅ | B Ö |
| PDF → TXT (UTF-8) | ✅ | B Ö |
| JPG / PNG / TIFF / BMP → PDF, çoklu görsel → tek PDF | ✅ | B Ö (JPEG yeniden sıkıştırılmaz, EXIF yönü uygulanır) |
| PDF → DOCX / XLSX / PPTX / HTML | ⏳ 5 | |
| Word / Excel / PowerPoint → PDF | ⏳ 5 | |

## 8. Sayfa yönetimi
| Madde | Durum | Test |
|---|---|---|
| Birleştirme (sıralama, sayfa aralığı, şifreli kaynak) | ✅ | B Ö |
| Bölme (her N sayfa, aralıklar, tek tek, tek/çift) | ✅ | B Ö |
| Sayfa ayıklama / aralığı dışa aktarma | ✅ | B |
| Sayfa ekleme (PDF'den, görüntüden), boş sayfa | ✅ | B |
| Silme, taşıma, sürükle-bırak sıralama, kopyalama | ✅ | B Ö |
| Döndürme, kırpma | ✅ | B Ö |
| Sayfa boyutu değiştirme: A4, A3, A5, Letter, Legal, özel | ✅ | B |
| Geri al / yinele (25 adım) | ✅ | B Ö |
| PDF sıkıştırma / boyut optimizasyonu | ⏳ 5 | Görüntü yeniden örnekleme gerekir |

## 9–14
Açıklamalar (9), imza ve güvenlik (10), formlar (11), karşılaştırma (12), toplu işlemler (13), yapay zekâ (14): ⏳ Aşama 6–8.

## 15. Performans ve kararlılık
| Madde | Durum | Not |
|---|---|---|
| Sayfaların gerektikçe yüklenmesi, önbellek temizleme | ✅ B Ö | Bayt bütçeli LRU, görünmeyen istekler iptal |
| Arayüz donmaması | ✅ | PDFium yalnızca motor iş parçacığında |
| Hata durumunda kapanmama | ✅ | Genel hata yakalayıcı + günlük |
| Güvenli kaydetme (atomik, doğrulamalı) | ✅ B | Başarısız kayıtta özgün dosya ve değişiklikler korunur |
| Otomatik kurtarma | ✅ | Kopyalar arka planda; açılışta geri yükleme önerilir |
| Geçici dosya temizliği | ✅ | Açılışta ve Ayarlar'dan |
| Bozuk / kötü amaçlı dosyalar | ✅ B | Anlaşılır hata; JavaScript çalıştırılmaz (V8'siz PDFium) |

## 16. Windows entegrasyonu
| Madde | Durum | Test |
|---|---|---|
| Windows 10 / 11 | ✅ | K (Server 2022 = Win10 çekirdeği, Server 2025 = Win11 çekirdeği) |
| Gezgin sağ tık menüsü | ✅ | K |
| Birlikte aç, dosya ilişkilendirme, Varsayılan uygulamalar kaydı | ✅ | K |
| Başlat menüsü, masaüstü kısayolu | ✅ | K |
| Çift tıklayınca açma, tek örnek (yeni sekmede açar) | ✅ | K (komut satırından PDF ile açılış) |
| Windows yazdırma sistemi | ✅ | Yazıcı listesi, PrintTicket |
| Tarayıcı — WIA: düz cam, belge besleyici, çift taraflı besleyici | 🟡 B Ö | İş mantığı testli; WIA hizmetiyle iletişim ve pencere Windows'ta testli. **Gerçek tarayıcıyla CI'da test edilemedi** — kullanıcı cihazında doğrulanmalı |
| Tarayıcının kendi (sürücü) tarama penceresi | 🟡 | Aynı şekilde gerçek cihazla doğrulanmalı |
| Tarama sonrası otomatik düzeltme (yan/ters sayfa, eğiklik, kenar) ve metin tanıma | ✅ Ö | Sürücünün yan verdiği sayfa simülasyonuyla testli (90° + 2° → dik ve düz); varsayılan açık, tarama penceresinden kapatılabilir |
| TWAIN (yalnız TWAIN sürücüsü olan tarayıcılar) | ⏳ | WIA sürücüsü olmayan cihazlar için |

## 17. Ayarlar
Tema ✅, arayüz dili (yalnız Türkçe) 🟡, görüntüleme varsayılanları ✅, otomatik kayıt/kurtarma ✅, dosya geçmişi ✅, PDF çıktı kalitesi (DPI, JPG kalitesi) ✅, geçici dosyalar ✅, donanım hızlandırma ✅, kısayollar ✅, varsayılan klasörler ✅, hakkında / sürüm / lisanslar ✅, OCR ayarları ⏳ 4, güncelleme denetimi ⛔ (çevrimiçi servis gerektirir; açık izinle Aşama 9).

## Bilinen sınırlamalar
* Düzenleme: metin değiştirildiğinde paragraf kurulu bir yazı tipiyle yeniden yazılır; satır içinde karışık biçim (tek sözcük kalın vb.) paragrafın baskın biçimine indirgenir. Her düzenleme geri alma için belgenin anlık görüntüsünü alır; çok büyük belgelerde (yüzlerce MB) düzenleme adımları birkaç saniye sürebilir.
* Yer imi yazımı PDFsharp ile yapılır; çok bozuk yapılı bazı PDF'lerde yer imi kaydı başarısız olursa kullanıcıya hata gösterilir, belge kaybolmaz.
* Sayfa boyutu değiştirmede açıklama (annotation) konumları ölçeklenmez.
* Arayüzde ekran okuyucu (UI Automation) etiketleri henüz eksiksiz değil.
