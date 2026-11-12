namespace MeslyPdfReader.App.Services;

/// <summary>Hakkında ekranındaki Lisans Sözleşmesi ve Gizlilik Politikası metinleri.</summary>
public static class LegalTexts
{
    public const string Eula = """
MESLY PDF READER — SON KULLANICI LİSANS SÖZLEŞMESİ

Bu sözleşme, Mesly Pdf Reader yazılımını (“Yazılım”) kuran veya kullanan kişi (“Kullanıcı”) ile Mesly arasında yapılmıştır. Yazılımı kurarak veya kullanarak bu koşulları kabul etmiş sayılırsınız.

1. Kullanım hakkı
Kullanıcıya, Yazılımı kendi bilgisayarlarında kişisel veya ticari amaçla kullanmak üzere münhasır olmayan, devredilemez bir kullanım hakkı verilir.

2. Kısıtlamalar
Kullanıcı; Yazılımı kiralayamaz, satamaz, Yazılımdaki telif ve marka bildirimlerini kaldıramaz ve Yazılımı yürürlükteki mevzuata aykırı amaçlarla kullanamaz.

3. Üçüncü taraf bileşenler
Yazılım, Lisans sekmesinde ve “Licenses” klasöründeki THIRD_PARTY_NOTICES.md dosyasında listelenen açık kaynak bileşenleri içerir. Bu bileşenler kendi lisans koşullarına tabidir ve bu sözleşme o koşulları kısıtlamaz.

4. Belgeleriniz
Yazılımla açtığınız, düzenlediğiniz veya oluşturduğunuz belgeler size aittir. Yazılım belgelerinizi internete göndermez (ayrıntılar Gizlilik Politikası'ndadır). Özgün dosyalarınız yalnızca siz kaydettiğinizde değişir; önemli belgelerinizin yedeğini almanız önerilir.

5. Garanti reddi
Yazılım “olduğu gibi” sağlanır. OCR (metin tanıma) ve dönüştürme sonuçları kaynak belgenin kalitesine bağlıdır ve hatasız olacağı garanti edilmez; önemli belgelerde sonuçları kontrol ediniz.

6. Sorumluluğun sınırlandırılması
Yürürlükteki mevzuatın izin verdiği ölçüde Mesly, Yazılımın kullanımından doğan dolaylı zararlardan, veri veya kâr kaybından sorumlu tutulamaz.

7. Güncellemeler
Ayarlarda açıksa Yazılım, yeni sürüm olup olmadığını denetler. Güncellemeler yalnızca sizin onayınızla indirilip kurulur.

8. Uygulanacak hukuk
Bu sözleşme Türkiye Cumhuriyeti hukukuna tabidir.
""";

    public const string Privacy = """
MESLY PDF READER — GİZLİLİK POLİTİKASI

Kısaca: Belgeleriniz bu bilgisayardan çıkmaz.

1. Belgeler ve içerikleri
PDF görüntüleme, düzenleme, OCR (metin tanıma), dönüştürme, sıkıştırma, birleştirme ve tarama işlemlerinin tamamı bu bilgisayarda yapılır. Belgeleriniz, içerikleri, tanınan metinler veya taranan görüntüler hiçbir sunucuya gönderilmez.

2. Bu bilgisayarda saklananlar
• Ayarlarınız, son açılan belgelerin listesi (dosya yolu, sayfa sayısı, boyut), sık kullanılanlar, kaldığınız sayfa ve işlem geçmişi yerel bir veritabanında (%LOCALAPPDATA%\Mesly\PdfReader) tutulur.
• Otomatik kurtarma açıksa değiştirdiğiniz belgelerin geçici kopyaları aynı klasörde saklanır ve belge kaydedilince/kapatılınca silinir.
• Hata günlükleri yalnızca bu bilgisayarda tutulur; siz paylaşmadıkça kimseye gönderilmez.
Geçmişi ve geçici dosyaları Ayarlar ekranından istediğiniz zaman silebilirsiniz.

3. İnternet bağlantısı
Yazılımın kurduğu tek bağlantı, Ayarlar › Genel'deki “Otomatik güncellemeleri kontrol et” açıksa GitHub'a yapılan sürüm sorgusudur. Bu sorguda belge, dosya adı veya kişisel bilgi gönderilmez; GitHub'ın standart sunucu kayıtları (IP adresi gibi) GitHub'ın gizlilik politikasına tabidir. Bu seçeneği kapatabilirsiniz.
PDF içindeki web bağlantıları, siz tıkladığınızda (ve isterseniz onayınızdan sonra) varsayılan tarayıcınızda açılır.

4. Office dönüştürme
Office belgelerini PDF'ye çevirirken bilgisayarınızda kurulu Microsoft Office veya LibreOffice kullanılır; bu programlar kendi gizlilik koşullarına tabidir.

5. İletişim
Gizlilikle ilgili sorularınız için Hakkında ekranındaki “Destek ile İletişim” bağlantısını kullanabilirsiniz.
""";
}
