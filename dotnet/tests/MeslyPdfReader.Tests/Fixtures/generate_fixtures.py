import sys, os
from reportlab.pdfgen import canvas
from reportlab.lib.pagesizes import A4
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from PIL import Image, ImageDraw
import pikepdf
out = sys.argv[1]; font = sys.argv[2]
pdfmetrics.registerFont(TTFont('DV', font))
# 1) Bağlantılı + yer imli belge, PDF 1.4
c = canvas.Canvas(os.path.join(out,'links_outline.pdf'), pagesize=A4, pdfVersion=(1,4))
c.setTitle('Bağlantı ve Yer İmi Testi'); c.setAuthor('Mesly Test')
for i in range(1,6):
    c.setFont('DV', 18); c.drawString(72, 760, f'Bölüm {i}: Çalışma Şartları')
    c.bookmarkPage(f'p{i}'); c.addOutlineEntry(f'Bölüm {i} — Giriş', f'p{i}', level=0)
    if i == 2: c.addOutlineEntry('Alt başlık ığüşöç', f'p{i}', level=1)
    if i == 1:
        c.drawString(72, 700, 'Web sitesi'); c.linkURL('https://example.com/mesly', (72, 695, 200, 715), relative=0)
        c.drawString(72, 650, 'Dördüncü sayfaya git'); c.linkRect('', 'p4', (72, 645, 300, 665), relative=0)
    c.showPage()
c.save()
# 2) PDF 1.3 sürümlü düz belge
c = canvas.Canvas(os.path.join(out,'v13.pdf'), pagesize=(595,842), pdfVersion=(1,3))
c.setFont('DV', 14); c.drawString(72, 760, 'PDF 1.3 belgesi: İĞÜŞÖÇ ığüşöç'); c.showPage(); c.save()
# 3) PDF 1.7 + nesne akışları (qpdf ile)
pdf = pikepdf.open(os.path.join(out,'links_outline.pdf'))
pdf.save(os.path.join(out,'v17_objstreams.pdf'), object_stream_mode=pikepdf.ObjectStreamMode.generate, min_version='1.7')
# 4) AES-256 şifreli
pdf.save(os.path.join(out,'encrypted_aes256.pdf'), encryption=pikepdf.Encryption(user='açık şifre', owner='sahip', R=6))
pdf.save(os.path.join(out,'encrypted_rc4.pdf'), encryption=pikepdf.Encryption(user='1234', owner='5678', R=3, metadata=False, aes=False))
# 5) Küçük JPEG ve PNG görüntüler
im = Image.new('RGB', (640, 400), (240, 244, 250)); d = ImageDraw.Draw(im)
d.rectangle([40,40,600,360], outline=(61,123,253), width=8); d.text((60,60), 'Mesly test', fill=(20,20,20))
im.save(os.path.join(out,'photo.jpg'), quality=85, dpi=(150,150))
im.rotate(90, expand=True).save(os.path.join(out,'portrait.png'), dpi=(96,96))
# 6) Hasarlı dosyalar
data = open(os.path.join(out,'links_outline.pdf'),'rb').read()
open(os.path.join(out,'truncated.pdf'),'wb').write(data[:len(data)//2])
open(os.path.join(out,'garbage.pdf'),'wb').write(os.urandom(4096))
open(os.path.join(out,'notpdf.pdf'),'wb').write(b'Bu bir PDF degil, sadece metin.\n'*50)
# 6) Düzenleme testi: çok satırlı paragraf (satır başına ayrı metin nesnesi), başlık, görsel, dikdörtgen
c = canvas.Canvas(os.path.join(out,'edit_paragraphs.pdf'), pagesize=A4)
c.setFont('DV', 20); c.drawString(72, 770, 'Sözleşme Başlığı')
c.setFont('DV', 11)
lines = ['Bu metin birinci paragrafın ilk satırıdır ve düzenleme', 'testi için hazırlanmıştır; ikinci satır burada devam',
         'eder ve üçüncü satırda paragraf sona erer.']
for k, l in enumerate(lines): c.drawString(72, 720 - k * 14, l)
c.drawString(72, 640, 'İkinci paragraf tek satırdan oluşur: ığüşöç İĞÜŞÖÇ.')
c.setFillColorRGB(0.2, 0.4, 0.8); c.rect(72, 500, 120, 60, fill=1, stroke=0)
c.drawImage(os.path.join(out, 'photo.jpg'), 300, 480, width=120, height=90)
c.showPage(); c.save()
# 7) Dönüştürme testi: başlık, iki yana yaslı paragraflar, birleşik hücreli çizgili tablo, görsel, iki sütun
from reportlab.platypus import Paragraph as RLPara, Frame
from reportlab.lib.styles import ParagraphStyle
from reportlab.lib.enums import TA_JUSTIFY, TA_CENTER
import os as _os
bold_path = '/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf'
if _os.path.exists(bold_path):
    pdfmetrics.registerFont(TTFont('DVB', bold_path))
BOLD = 'DVB' if _os.path.exists(bold_path) else 'DV'
c = canvas.Canvas(os.path.join(out, 'convert_sample.pdf'), pagesize=A4)
W, H = A4
c.setFont(BOLD, 20); c.drawCentredString(W / 2, H - 70, 'Hizmet Raporu 2026')
body = ParagraphStyle('b', fontName='DV', fontSize=11, leading=14.5, alignment=TA_JUSTIFY)
text1 = ('Bu rapor, Mesly PDF Reader dönüştürme özelliğinin sınanması için hazırlanmıştır. Paragraflar iki yana yaslıdır ve '
         'birden fazla satıra yayılır; dönüştürülen Word belgesinde de aynı hizalamayla tek paragraf olarak görünmelidir. '
         'Türkçe karakterler: ığüşöç İĞÜŞÖÇ.')
text2 = ('İkinci paragraf, ilk paragraftan boşlukla ayrılmıştır. Tablonun ve görselin metin akışındaki yeri korunmalı, '
         'sayılar Excel\'de sayı olarak yazılmalıdır.')
f = Frame(60, H - 230, W - 120, 140, showBoundary=0, leftPadding=0, rightPadding=0, topPadding=0, bottomPadding=0)
f.addFromList([RLPara(text1, body), RLPara('<br/>', body), RLPara(text2, body)], c)
# Çizgili tablo: başlık satırı birleşik (3 sütunu kaplar), 3 veri satırı
x0, y0, cw, rh = 60, H - 260, [190, 120, 120], 22
cols = [x0, x0 + cw[0], x0 + cw[0] + cw[1], x0 + sum(cw)]
rows = [y0 - i * rh for i in range(5)]
c.setLineWidth(0.8)
for y in rows: c.line(cols[0], y, cols[-1], y)
c.line(cols[0], rows[0], cols[0], rows[-1]); c.line(cols[-1], rows[0], cols[-1], rows[-1])
for x in cols[1:-1]: c.line(x, rows[1], x, rows[-1])   # ilk satırda iç dikey çizgi yok → birleşik hücre
c.setFont(BOLD, 11); c.drawCentredString((cols[0] + cols[-1]) / 2, rows[0] - 15, 'Aylık Satışlar')
data = [('Ürün', 'Adet', 'Tutar'), ('Kalem', '1.250', '3.437,50'), ('Defter', '320', '12.800,00')]
for i, (a, b, d) in enumerate(data):
    c.setFont(BOLD if i == 0 else 'DV', 10)
    y = rows[i + 1] - 15
    c.drawString(cols[0] + 5, y, a); c.drawRightString(cols[2] - 5, y, b); c.drawRightString(cols[3] - 5, y, d)
c.drawImage(os.path.join(out, 'photo.jpg'), 60, H - 480, width=160, height=110)
# İki sütun
colstyle = ParagraphStyle('c', fontName='DV', fontSize=10, leading=13)
left = 'Sol sütun metni birinci satır. ' * 6
right = 'Sağ sütun metni ikinci bölüm. ' * 6
Frame(60, 140, 225, 200, showBoundary=0).addFromList([RLPara(left, colstyle)], c)
Frame(310, 140, 225, 200, showBoundary=0).addFromList([RLPara(right, colstyle)], c)
c.showPage(); c.save()

# 8) masked_image.pdf — yumuşak maskeli (SMask) yoğun görsel: sıkıştırma saydamlığı bozmamalı
rgba = Image.new('RGBA', (1000, 750), (0, 0, 0, 0))
d = ImageDraw.Draw(rgba)
for r in range(370, 0, -10):
    d.ellipse((500 - r, 375 - r * 0.9, 500 + r, 375 + r * 0.9), fill=(200 - r // 3, 80 + r // 4, 160, 255))
rgba.save('/tmp/mesly_masked.png')
c = canvas.Canvas(os.path.join(out, 'masked_image.pdf'), pagesize=A4)
c.setFillColorRGB(0.95, 0.85, 0.2); c.rect(60, 500, 300, 220, fill=1, stroke=0)
c.drawImage('/tmp/mesly_masked.png', 80, 520, width=120, height=90, mask='auto')
c.showPage(); c.save()

# 9) Metin düzenleme sadakati fikstürleri
#  edit_chrome.pdf — Chrome/Skia tarzı: y ekseni ters, 0,75 ölçekli CTM; ayrıca 90° döndürülmüş etiket.
c = canvas.Canvas(os.path.join(out, 'edit_chrome.pdf'), pagesize=(612, 792))
c.saveState()
c.transform(0.75, 0, 0, -0.75, 0, 792)
for i, (y, t) in enumerate([(120, 'Web sayfası başlığı: Öğrenci Başvuruları'),
                            (160, 'Birinci paragraf satırı burada başlıyor ve devam ediyor;'),
                            (182, 'ikinci satır aynı paragrafın devamıdır, çok güzel.')]):
    tx = c.beginText(); tx.setTextTransform(1, 0, 0, -1, 96, y); tx.setFont('DV', 20 if i == 0 else 14); tx.textOut(t); c.drawText(tx)
c.restoreState()
c.saveState(); c.translate(560, 300); c.rotate(90); c.setFont('DV', 12); c.drawString(0, 0, 'Döndürülmüş etiket: Arşiv No 42'); c.restoreState()
c.showPage(); c.save()
#  edit_word.pdf — LibreOffice ile Word belgesinden üretilmiş: başlık, iki yana yaslı, ilk satır girintili,
#  ortalanmış, sağa yaslı ve sol girintili paragraflar. LibreOffice ve python-docx gerektirir; yoksa depodaki dosya kullanılır.
try:
    import docx, subprocess, tempfile, shutil
    from docx.shared import Pt, Cm
    from docx.enum.text import WD_ALIGN_PARAGRAPH
    d = docx.Document()
    st = d.styles['Normal']; st.font.name = 'Liberation Serif'; st.font.size = Pt(12)
    d.add_heading('Kira Sözleşmesi Özeti', level=1)
    d.add_paragraph('Bu belge İstanbul Şişli’deki dairenin kira koşullarını özetler. Kiracı Çağrı Öztürk, aylık kirayı her ayın beşinci gününe kadar ödemeyi kabul eder.')
    p = d.add_paragraph('İki yana yaslı paragraf: Taraflar, sözleşmenin uygulanmasından doğacak uyuşmazlıklarda İstanbul mahkemelerinin ve icra dairelerinin yetkili olduğunu kabul eder. Bu madde bütün ekleri kapsar ve değiştirilemez.')
    p.alignment = WD_ALIGN_PARAGRAPH.JUSTIFY
    p = d.add_paragraph('İlk satırı girintili paragraf: Depozito tutarı sözleşme bitiminde, dairenin teslim alındığı hâliyle geri verilmesi şartıyla iade edilir ve faiz işletilmez.')
    p.paragraph_format.first_line_indent = Cm(1.25)
    p = d.add_paragraph('Ortalanmış satır: Ek-1 Demirbaş Listesi'); p.alignment = WD_ALIGN_PARAGRAPH.CENTER
    p = d.add_paragraph('Sağa yaslı: 12.11.2026'); p.alignment = WD_ALIGN_PARAGRAPH.RIGHT
    p = d.add_paragraph('Sol girintili blok paragraf metni burada iki satıra uzayacak kadar uzun yazılmıştır, böylece sol kenar boşluğu korunmalıdır.')
    p.paragraph_format.left_indent = Cm(2)
    tmp = tempfile.mkdtemp(); src = os.path.join(tmp, 'edit_word.docx'); d.save(src)
    if shutil.which('soffice'):
        subprocess.run(['soffice', '--headless', '--convert-to', 'pdf', '--outdir', tmp, src], check=True, capture_output=True)
        shutil.copy(os.path.join(tmp, 'edit_word.pdf'), os.path.join(out, 'edit_word.pdf'))
except ImportError:
    pass
