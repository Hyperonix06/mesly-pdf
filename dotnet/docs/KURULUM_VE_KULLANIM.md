# Mesly PDF Reader — Kurulum ve Kullanım Rehberi

## Kurulum

1. `MeslyPdfReader-Setup-0.2.0.exe` dosyasını çalıştırın. Yönetici izni gerekmez; uygulama `%LOCALAPPDATA%\Programs\Mesly PDF Reader` klasörüne kurulur. (Tüm kullanıcılar için kurmak isterseniz kurulumun ilk ekranında seçebilirsiniz.)
2. İsteğe bağlı görevler:
   * **Masaüstü kısayolu**
   * **PDF dosyalarını “Birlikte aç” listesine ve sağ tık menüsüne ekle** — Gezgin'de bir PDF'ye sağ tıklayınca “Mesly PDF Reader ile aç” görünür.
3. .NET veya başka bir bileşen kurmanız gerekmez; her şey kurulumun içindedir.

**Varsayılan PDF uygulaması yapmak:** Windows bu seçimi yalnızca kullanıcıya bırakır. Ayarlar → *Windows entegrasyonu* → **Varsayılan uygulamalar…** düğmesi Windows Ayarları'nı açar; `.pdf` için “Mesly PDF Reader”ı seçin.

**Taşınabilir kullanım:** `MeslyPdfReader-Portable-…zip` dosyasını bir klasöre açıp `MeslyPdfReader.exe`'yi çalıştırabilirsiniz.

**Kaldırma:** Ayarlar → Uygulamalar → Mesly PDF Reader → Kaldır. Ayarlarınızın ve son dosyalar listesinin de silinmesini isteyip istemediğiniz sorulur.

## İlk bakış

* **Başlangıç** sekmesi: hızlı işlemler (PDF aç, PDF oluştur, birleştir, dönüştür, böl, boş PDF), son açılan belgeler (sabitleme, klasörde gösterme), dosya bırakma alanı.
* **Belge sekmesi**: solda sayfa küçük resimleri / yer imleri / arama; ortada sayfa görüntüleyici ve alttaki gezinme çubuğu; sağda (isteğe bağlı) belge ve sayfa özellikleri; en altta durum çubuğu.
* Üst menü: **Dosya**, **Ana Sayfa**, **Sayfalar**, **Dönüştür**, **Görünüm**.

## Sık yapılan işler

| İş | Nasıl |
|---|---|
| PDF açmak | Ctrl+O, dosyayı pencereye sürükleyin veya Gezgin'de çift tıklayın (açık pencerede yeni sekme olur) |
| Parolalı PDF | Açarken parola sorulur; kaydettiğinizde parola koruması korunur |
| Arama | Ctrl+F → yazın → Enter. F3 / Shift+F3 sonraki/önceki. Türkçe büyük-küçük harf kuralları (İ/i, I/ı) uygulanır; “Türkçe karakterleri yok say” ile *sofor* → *şoför* bulunur |
| Metin kopyalama | Seç aracıyla sürükleyin (çift tık sözcük seçer), Ctrl+C. Ctrl+A tüm belge |
| Yakınlaştırma | Ctrl+fare tekerleği (imleç etrafında), Ctrl+ / Ctrl−, Ctrl+0 sayfaya sığdır, Ctrl+2 genişliğe sığdır, Ctrl+1 %100 |
| Sayfa düzeni | Görünüm → Sürekli / Tek sayfa / Çift sayfa / Kitap |
| Karanlık okuma | Ctrl+Shift+N (belgeyi değiştirmez) |
| Tam ekran | F11, çıkmak için Esc |
| Yer imi | Ctrl+D geçerli sayfaya ekler (seçili metin varsa onu ad önerir). Yer imleri panelinde sağ tık: alt yer imi, yeniden adlandır (F2), taşı, sil |
| Sayfaları döndürmek / silmek | Küçük resimlerde sayfaları seçin (Ctrl/Shift ile çoklu) → sağ tık veya Sayfalar menüsü. Delete siler |
| Sayfa sırası | Küçük resimleri sürükleyip bırakın |
| Başka dosyadan sayfa eklemek | Bir PDF'yi veya görüntüyü küçük resimler arasına sürükleyin ya da Sayfalar → Dosyadan ekle |
| Birleştirme | Sayfalar/Dönüştür → Birleştir: dosyaları ekleyin, sıralayın, isterseniz her dosya için sayfa aralığı yazın |
| Bölme | Sayfalar → Böl: her N sayfa, aralıklar (1-3; 4-10; 11-son), her sayfa ayrı, tek/çift |
| Kırpma, sayfa boyutu | Sayfalar → Kırp (mm) / Sayfa boyutu (A4, A3, A5, Letter, Legal, özel) |
| PDF → görüntü | Dönüştür → PDF → Görüntü: PNG, JPG, TIFF (isteğe bağlı tek çok sayfalı dosya), BMP; DPI seçimi |
| PDF → metin | Dönüştür → PDF → Metin (UTF-8, Türkçe karakterler korunur) |
| Görüntülerden PDF | Dönüştür → Görüntü → PDF veya görüntüleri pencereye sürükleyin |
| Geri al / yinele | Ctrl+Z / Ctrl+Y (sayfa işlemleri ve yer imleri, 25 adım) |
| Yazdırma | Ctrl+P: yazıcı, kopya, sayfa aralığı, kâğıt, ölçek, yön, renk; canlı önizleme |

**Sayfa aralığı yazımı:** `1-3, 5, 8-son`, `tek`, `çift`, `tümü`.

## Güvenli çalışma

* Kaydetme önce geçici dosyaya yapılır, doğrulanır, sonra özgün dosyanın yerine geçer. Bir hata olursa özgün dosyanız değişmez ve değişiklikleriniz açık kalır.
* Otomatik kurtarma: değiştirdiğiniz belgelerin kopyası birkaç dakikada bir saklanır. Uygulama beklenmedik kapanırsa sonraki açılışta geri yüklemeniz önerilir.
* PDF içindeki web bağlantıları açılmadan önce onayınız istenir. PDF'lerdeki JavaScript hiçbir zaman çalıştırılmaz.
* Hiçbir belge internete gönderilmez.

## Sorun giderme

* **Görüntü bozukluğu / ekran kartı sorunu:** Ayarlar → Performans → Donanım hızlandırmayı kapatın, uygulamayı yeniden başlatın.
* **Günlük dosyaları:** Ayarlar → Hakkında → Günlük klasörünü aç (`%LOCALAPPDATA%\Mesly\PdfReader\logs`). Günlüklere belge içeriği yazılmaz.
* **Taranmış belgede metin seçilemiyor:** Taranmış sayfalar görüntüdür; metin tanıma (OCR) sonraki sürümde eklenecek.
