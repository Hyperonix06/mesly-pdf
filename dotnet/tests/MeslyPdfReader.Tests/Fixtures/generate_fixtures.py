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
