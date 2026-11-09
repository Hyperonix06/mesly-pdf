"""Mesly PDF çekirdek testleri (Linux ve Windows'ta çalışır)."""
import os
import shutil
import sys

import pytest

sys.path.insert(0, os.path.dirname(__file__))
sys.path.insert(0, os.path.dirname(os.path.dirname(__file__)))

from samples import make_text_pdf, make_table_pdf, make_scan_image  # noqa: E402
from meslypdf.core import (document, pages, editing, security, annotations as ann, forms, sign, ocr, convert,  # noqa: E402
                           compare, tasks, hotfolder, preprocess)
from meslypdf.core.document import PdfDocument  # noqa: E402

HAS_OCR = ocr.tesseract_ok()
needs_ocr = pytest.mark.skipif(not HAS_OCR, reason="Tesseract yok")


@pytest.fixture(scope="session")
def d(tmp_path_factory):
    os.environ["APPDATA"] = str(tmp_path_factory.mktemp("appdata"))
    base = tmp_path_factory.mktemp("mesly")
    make_text_pdf(str(base / "a.pdf"))
    make_text_pdf(str(base / "b.pdf"), 2)
    make_table_pdf(str(base / "table.pdf"))
    make_scan_image(str(base / "scan.png"))
    make_scan_image(str(base / "scan_skew.png"), angle=4)
    pages.images_to_pdf([str(base / "scan.png")], str(base / "imgonly.pdf"))
    return base


def text(p, i=0):
    with PdfDocument(str(p)) as doc:
        return doc.page_text(i)


def count(p):
    with PdfDocument(str(p)) as doc:
        return doc.page_count


# ---------------------------------------------------------------- görüntüleme / arama
def test_open_and_search(d):
    with PdfDocument(str(d / "a.pdf")) as doc:
        assert doc.page_count == 3
        assert "Sözleşme" in doc.page_text(0)
        assert len(doc.search("sözleşme")) >= 3
        hit = doc.search("Ödeme")[0]
        assert hit.rects and hit.page == 0
        assert doc.render(0, 0.5).size[0] > 100


def test_coordinates_roundtrip(d):
    pages.rotate(str(d / "a.pdf"), [0], 90, str(d / "rot.pdf"))
    with PdfDocument(str(d / "rot.pdf")) as doc:
        w, h = doc.page_size(0)
        dx, dy = doc.page_to_device(0, w, h, 72, 770)
        x, y = doc.device_to_page(0, w, h, dx, dy)
        assert abs(x - 72) < 1 and abs(y - 770) < 1


# ---------------------------------------------------------------- sayfalar
def test_merge_split(d):
    m = str(d / "m.pdf")
    pages.merge([str(d / "a.pdf"), str(d / "b.pdf"), str(d / "scan.png")], m)
    assert count(m) == 6
    assert [b["title"] for b in editing.get_bookmarks(m)] == ["a", "b", "scan"]
    assert len(pages.split(m, str(d / "sp"), "every", 2)) == 3
    assert len(pages.split(m, str(d / "sb"), "bookmarks")) == 3
    assert len(pages.split(m, str(d / "sr"), "ranges", "1-2;3-6")) == 2


def test_page_ops(d):
    a = str(d / "a.pdf")
    out = str(d / "ops.pdf")
    pages.delete(a, [1], out)
    assert count(out) == 2
    pages.reorder(a, [2, 1, 0], out)
    assert "Bölüm 3" in text(out, 0)
    pages.insert_blank(a, 1, out)
    assert pages.detect_blank_pages(out) == [1]
    pages.crop(a, [0], (10, 10, 10, 10), out)
    assert pages.parse_ranges("1-2,4-", 6) == [0, 1, 3, 4, 5]
    with pytest.raises(ValueError):
        pages.parse_ranges("7", 6)


# ---------------------------------------------------------------- düzenleme
def test_watermark_header_stamp_remove(d):
    a, o = str(d / "a.pdf"), str(d / "w.pdf")
    editing.add_watermark(a, o, "GİZLİ")
    editing.add_header_footer(o, o, footer={"center": "Sayfa {sayfa} / {toplam}"})
    editing.add_stamp(o, o, "ONAYLANDI")
    t = text(o)
    assert "GİZLİ" in t and "Sayfa 1 / 3" in t and "ONAYLANDI" in t
    _, n = editing.remove_marks(o, str(d / "w2.pdf"), "watermark")
    assert n == 3 and "GİZLİ" not in text(d / "w2.pdf")


def test_bates(d):
    outs, nxt = editing.bates_number([str(d / "a.pdf"), str(d / "b.pdf")], str(d / "bates"), "MSL-")
    assert nxt == 6 and "MSL-000004" in text(outs[1], 0)


def test_text_edit_reflow(d):
    a, o = str(d / "a.pdf"), str(d / "edit.pdf")
    blocks = editing.text_blocks(a, 0)
    assert blocks[0]["bold"] and blocks[1]["lines"] == 4
    b = blocks[1]
    editing.edit_text_block(a, o, 0, b["bounds"], "Yeniden yazılmış paragraf metni " * 6, b["size"], b["first_baseline"], b["leading"])
    t = text(o)
    assert "Yeniden yazılmış" in t and "Taraflar" not in t and "Bölüm 1" in t


def test_metadata_links_attachments_bookmarks(d):
    a, o = str(d / "a.pdf"), str(d / "meta.pdf")
    editing.set_metadata(a, o, title="Başlık", author="Mesly")
    assert editing.get_metadata(o)["Title"] == "Başlık"
    editing.add_link(o, o, 0, (72, 700, 200, 720), uri="https://mesly.com.tr")
    assert editing.list_links(o)[0]["uri"] == "https://mesly.com.tr"
    editing.add_attachment(o, o, str(d / "scan.png"), "ek")
    assert editing.list_attachments(o)[0]["name"] == "scan.png"
    editing.rename_attachment(o, o, "scan.png", "foto.png")
    assert editing.extract_attachments(o, str(d / "att"))[0].endswith("foto.png")
    editing.auto_bookmarks_from_headings(a, o)
    assert len(editing.get_bookmarks(o)) == 3


def test_compress(d):
    if HAS_OCR:
        ocr.ocr_images_to_pdf([str(d / "scan.png")], str(d / "big.pdf"))
    else:
        pages.images_to_pdf([str(d / "scan.png")], str(d / "big.pdf"))
    _, before, after = editing.compress(str(d / "big.pdf"), str(d / "small.pdf"), "high")
    assert after < before


# ---------------------------------------------------------------- yorumlar
def test_annotations(d):
    a, o = str(d / "a.pdf"), str(d / "ann.pdf")
    ann.add_markup(a, o, 0, [(72, 725, 300, 742)], "Highlight", contents="önemli")
    ann.add_note(o, o, 0, 400, 700, "Not çğış")
    ann.add_freetext(o, o, 0, (300, 500, 520, 560), "Kutu")
    ann.add_shape(o, o, 0, (100, 400, 250, 470), "Circle")
    ann.add_shape(o, o, 0, (100, 350, 300, 300), "Line")
    ann.add_ink(o, o, 0, [[(100, 200), (150, 250)]])
    items = ann.list_annotations(o)
    assert [i["kind"] for i in items] == ["Highlight", "Text", "FreeText", "Circle", "Line", "Ink"]
    ann.reply(o, o, items[0]["objgen"], "katılıyorum")
    ann.set_status(o, o, items[0]["objgen"], "Accepted")
    items = ann.list_annotations(o)
    assert items[0]["replies"][0]["contents"] == "katılıyorum" and items[0]["status"] == "Accepted"
    assert len(ann.filter_sort(items, kind="Text")) == 1
    ann.delete_annotation(o, o, items[0]["objgen"])
    assert len(ann.list_annotations(o)) == 5
    ann.flatten_annotations(o, str(d / "flat.pdf"))
    assert ann.list_annotations(str(d / "flat.pdf")) == []


# ---------------------------------------------------------------- formlar
def test_forms(d):
    pages.new_blank(str(d / "f0.pdf"))
    f = str(d / "f.pdf")
    forms.add_field(str(d / "f0.pdf"), f, 0, "text", "ad", (72, 700, 300, 722), required=True)
    forms.add_field(f, f, 0, "checkbox", "onay", (72, 660, 88, 676))
    forms.add_field(f, f, 0, "radio", "secim", (72, 620, 200, 636), options=["A", "B"])
    forms.add_field(f, f, 0, "combo", "sehir", (72, 580, 250, 600), options=["Ankara", "İzmir"])
    forms.add_field(f, f, 0, "button", "sifirla", (72, 500, 180, 525), value="Sıfırla", action={"type": "reset"})
    assert forms.validate_required(f) == ["ad"]
    forms.fill(f, str(d / "ff.pdf"), {"ad": "Şükrü Çağlayan", "onay": True, "secim": "B", "sehir": "İzmir"})
    vals = {x["name"]: x["value"] for x in forms.list_fields(str(d / "ff.pdf"))}
    assert vals["ad"] == "Şükrü Çağlayan" and vals["onay"] == "Evet" and vals["secim"] == "B" and vals["sehir"] == "İzmir"
    forms.export_fdf(str(d / "ff.pdf"), str(d / "ff.fdf"))
    forms.import_fdf(f, str(d / "ff.fdf"), str(d / "fi.pdf"))
    assert {x["name"]: x["value"] for x in forms.list_fields(str(d / "fi.pdf"))}["ad"] == "Şükrü Çağlayan"
    forms.fill(f, str(d / "flat.pdf"), {"ad": "Çağrı"}, flatten=True)
    assert "Çağrı" in text(d / "flat.pdf") and forms.list_fields(str(d / "flat.pdf")) == []


# ---------------------------------------------------------------- güvenlik
def test_encrypt_decrypt(d):
    a = str(d / "a.pdf")
    for m in security.ENCRYPTIONS:
        security.encrypt(a, str(d / "e.pdf"), "1234", method=m, allow_copy=False)
        info = security.encryption_info(str(d / "e.pdf"), "1234")
        assert info["encrypted"] and not info["copy"]
    assert document.is_encrypted(str(d / "e.pdf"))
    security.decrypt(str(d / "e.pdf"), str(d / "dec.pdf"), "1234")
    assert not document.is_encrypted(str(d / "dec.pdf"))


def test_redaction_secure(d):
    out, n = security.redact_text(str(d / "a.pdf"), str(d / "red.pdf"), ["Ödeme"], ["TC Kimlik No", "E-posta"], ocr_lang=None)
    assert n == 9
    t = text(d / "red.pdf")
    assert "12345678901" not in t and "ornek@" not in t and "Ödeme" not in t


def test_clean(d):
    a = str(d / "a.pdf")
    o = str(d / "c.pdf")
    editing.add_attachment(a, o, str(d / "scan.png"))
    ann.add_note(o, o, 0, 100, 100, "gizli not")
    _, removed = security.remove_hidden_data(o, str(d / "c2.pdf"))
    assert "Ekli dosyalar" in removed and "Yorumlar" in removed
    assert editing.list_attachments(str(d / "c2.pdf")) == [] and ann.list_annotations(str(d / "c2.pdf")) == []


# ---------------------------------------------------------------- imza
def test_sign_verify(d):
    p12 = str(d / "me.p12")
    sign.create_self_signed(p12, "Şükrü Çağlayan", "1234", "a@b.c", "Mesly")
    s1 = str(d / "s1.pdf")
    sign.sign_pdf(str(d / "a.pdf"), s1, p12, "1234", reason="Onay", location="İstanbul")
    sign.sign_pdf(s1, str(d / "s2.pdf"), p12, "1234", field_name="Imza2", box=None)
    res = sign.verify(str(d / "s2.pdf"))
    assert len(res) == 2 and all(r["intact"] for r in res)
    data = bytearray(open(s1, "rb").read())
    data[200] = (data[200] + 1) % 256
    open(d / "t.pdf", "wb").write(bytes(data))
    assert not sign.verify(str(d / "t.pdf"))[0]["intact"]


# ---------------------------------------------------------------- OCR
@needs_ocr
def test_preprocess_skew_orientation(d):
    from PIL import Image
    im = Image.open(d / "scan_skew.png")
    assert 3.5 < abs(preprocess.skew_angle(im)) < 4.5
    assert preprocess.detect_orientation(Image.open(d / "scan.png").rotate(90, expand=True)) == 90


@needs_ocr
def test_ocr_turkish(d):
    from PIL import Image
    make_scan_image(str(d / "tr_words.png"), ["Şirketimizin İstanbul şubesinde çalışan öğretmenler",
                                              "ÇAĞDAŞ ÜLKELERİN GÜÇLÜ EKONOMİSİ ÖĞRENİLDİ",
                                              "Fatura numarası 2026-0042 tarih 09.11.2026"], angle=4)
    r = ocr.recognize_image(Image.open(d / "tr_words.png"), "tur+eng", 200, opts=preprocess.PreprocessOptions())
    for word in ("TARANMIŞ", "Şirketimizin", "İstanbul", "şubesinde", "çalışan", "öğretmenler", "ÇAĞDAŞ",
                 "ÜLKELERİN", "GÜÇLÜ", "EKONOMİSİ", "ÖĞRENİLDİ", "numarası", "2026-0042"):
        assert word in r.text, (word, r.text)
    assert ocr.detect_language(Image.open(d / "scan.png")) == "tur"


@needs_ocr
def test_ocr_pdf_and_tables(d):
    ocr.ocr_pdf(str(d / "imgonly.pdf"), str(d / "ocr.pdf"))
    assert "TARANMIŞ" in text(d / "ocr.pdf")
    res = ocr.recognize_pdf(str(d / "table.pdf"))
    tb = [b for b in res[0].blocks if b.kind == "table"][0]
    assert [c["text"] for c in tb.cells[2]] == ["Defter", "5", "40,00"]


@needs_ocr
def test_redact_keeps_searchable(d):
    security.redact_text(str(d / "a.pdf"), str(d / "red2.pdf"), patterns=["TC Kimlik No"])
    t = text(d / "red2.pdf")
    assert "12345678901" not in t and "Sözleşme" in t


# ---------------------------------------------------------------- dönüştürme
@needs_ocr
@pytest.mark.parametrize("fmt", ["docx", "xlsx", "pptx", "odt", "rtf", "txt", "csv", "html", "epub", "fb2"])
def test_convert_formats(d, fmt):
    m = str(d / "mix.pdf")
    if not os.path.exists(m):
        pages.merge([str(d / "a.pdf"), str(d / "table.pdf")], m)
    out = str(d / f"mix.{fmt}")
    convert.convert(m, out, fmt)
    assert os.path.getsize(out) > 0


@needs_ocr
def test_convert_content(d):
    from docx import Document
    from openpyxl import load_workbook
    m = str(d / "mix2.pdf")
    pages.merge([str(d / "a.pdf"), str(d / "table.pdf")], m)
    convert.convert(m, str(d / "mix2.docx"), "docx")
    doc = Document(str(d / "mix2.docx"))
    assert doc.paragraphs[0].text.startswith("Bölüm 1") and doc.tables[0].cell(1, 0).text == "Kalem"
    convert.convert(m, str(d / "mix2.xlsx"), "xlsx")
    ws = load_workbook(str(d / "mix2.xlsx"))["Tablo 1"]
    assert ws["B2"].value == 10 and ws["C3"].value == 40


def test_pdfa(d):
    out, warns = convert.make_pdfa(str(d / "a.pdf"), str(d / "pdfa.pdf"))
    info = convert.check_pdfa(out)
    assert info["part"] == "2" and info["output_intent"]


@needs_ocr
def test_pdfua_and_any_to_pdf(d):
    convert.make_pdfua(str(d / "imgonly.pdf"), str(d / "ua.pdf"))
    import pikepdf
    with pikepdf.open(str(d / "ua.pdf")) as p:
        assert p.Root.MarkInfo.Marked and len(p.Root.StructTreeRoot.K.K) >= 3
    open(d / "x.txt", "w", encoding="utf-8").write("Merhaba Türkçe ığüşöç")
    convert.any_to_pdf([str(d / "x.txt"), str(d / "scan.png"), str(d / "a.pdf")], str(d / "any.pdf"), ocr=True)
    assert count(d / "any.pdf") == 5 and "ığüşöç" in text(d / "any.pdf")


# ---------------------------------------------------------------- karşılaştırma
def _mk(path, lines, footer):
    from reportlab.pdfgen import canvas
    from meslypdf.core.fonts import font_name
    c = canvas.Canvas(path)
    c.setFont(font_name(), 12)
    y = 760
    for l in lines:
        c.drawString(72, y, l)
        y -= 20
    c.setFont(font_name(), 8)
    c.drawString(72, 30, footer)
    c.save()


def test_compare(d):
    _mk(str(d / "v1.pdf"), ["Bedel 1.000 TL olarak belirlenmiştir.", "Süre 30 gündür.", "Silinecek madde.", "Son satır."], "Altbilgi v1")
    _mk(str(d / "v2.pdf"), ["Bedel 1.500 TL olarak belirlenmiştir.", "Süre 30 iş günüdür.", "Son satır.", "Yeni madde."], "Altbilgi v2")
    r = compare.compare(str(d / "v1.pdf"), str(d / "v2.pdf"))
    s = r.summary()
    assert s["Toplam"] >= 4 and s["Eklendi"] >= 1
    assert any(x.old_text == "1.000" and x.new_text == "1.500" for x in r.diffs)
    assert any(x.zone == "footer" for x in r.diffs)
    r2 = compare.compare(str(d / "v1.pdf"), str(d / "v2.pdf"), ignore_headers=True)
    assert all(x.zone == "body" for x in r2.diffs)
    compare.export_xlsx(r, str(d / "c.xlsx"))
    compare.export_docx_track_changes(r, str(d / "c.docx"))
    import zipfile
    xml = zipfile.ZipFile(str(d / "c.docx")).read("word/document.xml").decode()
    assert "<w:ins" in xml and "<w:del " in xml
    compare.export_annotated_pdfs(r, str(d / "co.pdf"), str(d / "cn.pdf"))
    assert ann.list_annotations(str(d / "cn.pdf"))


# ---------------------------------------------------------------- otomasyon
@needs_ocr
def test_tasks_and_hotfolder(d):
    ts = tasks.load_tasks()
    outs = tasks.run_task(ts[0], [str(d / "scan_skew.png")], str(d / "tout"), log=lambda m: None)
    assert "TARANMIŞ" in text(outs[0])
    pages.insert_blank(str(d / "a.pdf"), 1, str(d / "wb.pdf"))
    t = tasks.Task("ayır", [{"type": "split_blank", "params": {}}, {"type": "save", "params": {}}])
    assert len(tasks.run_task(t, [str(d / "wb.pdf")], str(d / "tout2"), log=lambda m: None)) == 2
    inbox = d / "hf_in"
    inbox.mkdir()
    shutil.copy(d / "scan.png", inbox / "s.png")
    job = hotfolder.HotFolderJob("T", source=str(inbox), output_dir=str(d / "hf_out"), task=ts[0].to_json())
    produced = hotfolder.run_job(job, log=lambda m: m)
    assert len(produced) == 1 and (inbox / "İşlenenler" / "s.png").exists()


def test_cli(d, capsys):
    from meslypdf import cli
    assert cli.main(["merge", str(d / "a.pdf"), str(d / "b.pdf"), "-o", str(d / "cli.pdf")]) == 0
    assert count(d / "cli.pdf") == 5
    assert cli.main(["protect", str(d / "a.pdf"), "-o", str(d / "cp.pdf"), "--user-pw", "1"]) == 0
    assert document.is_encrypted(str(d / "cp.pdf"))
    assert cli.main(["task", "--list"]) == 0
