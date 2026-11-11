# Mesly PDF Reader — Özellik Kontrol Listesi

Durumlar: ✅ çalışıyor ve testle doğrulandı · 🟡 kısmen / sınırlı · ⏳ planlandı (aşama numarası) · ⛔ bu sürümde yok
Test kaynakları: **B** = birim testi (`dotnet/tests`, Linux + Windows), **Ö** = Windows'ta gerçek pencerede arayüz öz testi (`--selftest`), **K** = CI kurulum testi (Windows Server 2022 + 2025).

Sürüm 0.2.0 — Aşama 1 ve Aşama 2 tamamlandı.

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
| Kurulum paketi (EXE) | ✅ K | Inno Setup, kullanıcı başına, yönetici izni gerekmez |
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
| Ana ekran: OCR ile tara, belge karşılaştır | ⏳ 4 / 7 | Henüz çalışmadığı için düğmeleri gösterilmiyor |
| Son belgeler, hızlı işlemler, bırakma alanı | ✅ Ö | Sabitleme, klasörde göster, listeden kaldır |
| Editör: sol panel küçük resim / yer imi / arama | ✅ Ö | |
| Editör: katmanlar paneli | ⏳ 3 | |
| Üst menü: Dosya, Ana Sayfa, Sayfalar, Dönüştür, Görünüm | ✅ Ö | |
| Üst menü: Düzenle, OCR, Açıklamalar, Formlar, Güvenlik | ⏳ 3–6 | Çalışmayan menü olmaması için gizli |
| Sağ panel: özellikler | 🟡 | Belge ve sayfa özellikleri; nesne özellikleri Aşama 3 |
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

## 5. PDF düzenleyici — ⏳ Aşama 3
Gerçek PDF metin nesnesi yazma altyapısı hazır (`AddTextAsync`, gömülü yazı tipi, test edildi). Mevcut metni değiştirme, yazı tipi/biçim, görsel/şekil, üst-altbilgi, filigran, sayfa numarası Aşama 3'te.

## 6. OCR — ⏳ Aşama 4
Taranmış belgede metin bulunamazsa kullanıcıya açıkça söylenir (TXT dışa aktarımı).

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
| Tarayıcı (TWAIN / WIA) | ⏳ 4 | |

## 17. Ayarlar
Tema ✅, arayüz dili (yalnız Türkçe) 🟡, görüntüleme varsayılanları ✅, otomatik kayıt/kurtarma ✅, dosya geçmişi ✅, PDF çıktı kalitesi (DPI, JPG kalitesi) ✅, geçici dosyalar ✅, donanım hızlandırma ✅, kısayollar ✅, varsayılan klasörler ✅, hakkında / sürüm / lisanslar ✅, OCR ayarları ⏳ 4, güncelleme denetimi ⛔ (çevrimiçi servis gerektirir; açık izinle Aşama 9).

## Bilinen sınırlamalar
* Yer imi yazımı PDFsharp ile yapılır; çok bozuk yapılı bazı PDF'lerde yer imi kaydı başarısız olursa kullanıcıya hata gösterilir, belge kaybolmaz.
* Sayfa boyutu değiştirmede açıklama (annotation) konumları ölçeklenmez.
* Arayüzde ekran okuyucu (UI Automation) etiketleri henüz eksiksiz değil.
