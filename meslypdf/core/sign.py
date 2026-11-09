"""Dijital imza (PAdES): sertifika oluşturma, imzalama (PFX/P12 veya akıllı kart PKCS#11),
zaman damgası, LTV ve imza doğrulama. pyHanko tabanlı."""
from __future__ import annotations

import datetime as _dt
import os

HASHES = ["sha256", "sha384", "sha512"]
TSA_SERVERS = {
    "Yok": None,
    "FreeTSA": "https://freetsa.org/tsr",
    "DigiCert": "http://timestamp.digicert.com",
    "Sectigo": "http://timestamp.sectigo.com",
}


def create_self_signed(out_p12: str, name: str, password: str, email: str = "", org: str = "",
                       country: str = "TR", years: int = 3) -> str:
    """Kendinden imzalı imza sertifikası (PKCS#12 / .pfx) oluşturur."""
    from cryptography import x509
    from cryptography.hazmat.primitives import hashes, serialization
    from cryptography.hazmat.primitives.asymmetric import rsa
    from cryptography.hazmat.primitives.serialization import pkcs12
    from cryptography.x509.oid import NameOID, ExtendedKeyUsageOID

    key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
    attrs = [x509.NameAttribute(NameOID.COMMON_NAME, name), x509.NameAttribute(NameOID.COUNTRY_NAME, country)]
    if org:
        attrs.append(x509.NameAttribute(NameOID.ORGANIZATION_NAME, org))
    if email:
        attrs.append(x509.NameAttribute(NameOID.EMAIL_ADDRESS, email))
    subj = x509.Name(attrs)
    now = _dt.datetime.now(_dt.timezone.utc)
    cert = (x509.CertificateBuilder().subject_name(subj).issuer_name(subj).public_key(key.public_key())
            .serial_number(x509.random_serial_number()).not_valid_before(now - _dt.timedelta(minutes=5))
            .not_valid_after(now + _dt.timedelta(days=365 * years))
            .add_extension(x509.KeyUsage(digital_signature=True, content_commitment=True, key_encipherment=False,
                                         data_encipherment=False, key_agreement=False, key_cert_sign=True,
                                         crl_sign=False, encipher_only=False, decipher_only=False), critical=True)
            .add_extension(x509.ExtendedKeyUsage([ExtendedKeyUsageOID.EMAIL_PROTECTION,
                                                  x509.ObjectIdentifier("1.2.840.113583.1.1.5")]), critical=False)
            .add_extension(x509.BasicConstraints(ca=True, path_length=None), critical=True)
            .sign(key, hashes.SHA256()))
    data = pkcs12.serialize_key_and_certificates(name.encode(), key, cert, None,
                                                 serialization.BestAvailableEncryption(password.encode()))
    os.makedirs(os.path.dirname(os.path.abspath(out_p12)) or ".", exist_ok=True)
    with open(out_p12, "wb") as f:
        f.write(data)
    return out_p12


def certificate_info(p12: str, password: str) -> dict:
    from cryptography.hazmat.primitives.serialization import pkcs12
    _, cert, _ = pkcs12.load_key_and_certificates(open(p12, "rb").read(), password.encode())
    return {"subject": cert.subject.rfc4514_string(), "issuer": cert.issuer.rfc4514_string(),
            "valid_from": cert.not_valid_before_utc.strftime("%d.%m.%Y"),
            "valid_to": cert.not_valid_after_utc.strftime("%d.%m.%Y"), "serial": hex(cert.serial_number)}


def _signer_name(signer) -> str:
    try:
        from asn1crypto import x509 as ax
        cn = signer.signing_cert.subject.native.get("common_name")
        return cn or signer.signing_cert.subject.human_friendly
    except Exception:
        return "İmzalayan"


def _stamp_style(signer, box, text: str | None, image: str | None, show_details: bool, reason="", location=""):
    """İmza görünümünü (Türkçe karakter destekli) reportlab ile üretir."""
    import tempfile
    from pyhanko import stamp
    from reportlab.pdfgen import canvas
    from reportlab.lib.colors import HexColor
    from reportlab.lib.utils import ImageReader
    from .fonts import font_name
    w, h = box[2] - box[0], box[3] - box[1]
    fd, tmp = tempfile.mkstemp(suffix=".pdf")
    os.close(fd)
    c = canvas.Canvas(tmp, pagesize=(w, h))
    c.setStrokeColor(HexColor("#1565C0"))
    c.setFillColor(HexColor("#F3F8FE"))
    c.roundRect(0.5, 0.5, w - 1, h - 1, 4, stroke=1, fill=1)
    x = 6
    if image:
        from PIL import Image
        img = Image.open(image)
        ih = h - 10
        iw = min(w * 0.45, ih * img.width / img.height)
        ih = iw * img.height / img.width
        c.drawImage(ImageReader(img), 5, (h - ih) / 2, iw, ih, mask="auto")
        x = iw + 12
    name = _signer_name(signer)
    lines = []
    if text:
        lines = text.replace("%(signer)s", name).replace("%(ts)s", _dt.datetime.now().strftime("%d.%m.%Y %H:%M")).split("\n")
    elif show_details:
        lines = ["Dijital olarak imzalayan:", name, "Tarih: " + _dt.datetime.now().strftime("%d.%m.%Y %H:%M:%S")]
        if reason:
            lines.append("Neden: " + reason)
        if location:
            lines.append("Yer: " + location)
    else:
        lines = [name]
    avail_w = w - x - 6
    size = min(11, (h - 8) / (len(lines) * 1.25))
    for ln in lines:
        while size > 4 and c.stringWidth(ln, font_name(), size) > avail_w:
            size -= 0.25
    y = h / 2 + len(lines) * size * 1.25 / 2 - size
    for k, ln in enumerate(lines):
        c.setFillColor(HexColor("#0D47A1") if k == 1 or len(lines) == 1 else HexColor("#263238"))
        c.setFont(font_name(k == 1 or len(lines) == 1), size)
        c.drawString(x, y, ln)
        y -= size * 1.25
    c.showPage()
    c.save()
    return stamp.StaticStampStyle.from_pdf_file(tmp, border_width=0)


def _do_sign(path, out, signer, field_name, page, box, reason, location, contact, name, tsa_url,
             embed_validation, certify, md_algorithm, stamp_text, image, show_details, password=None):
    from pyhanko.pdf_utils.incremental_writer import IncrementalPdfFileWriter
    from pyhanko.sign import signers, fields, timestamps
    from pyhanko_certvalidator import ValidationContext

    with open(path, "rb") as inf:
        w = IncrementalPdfFileWriter(inf, strict=False)
        if password:
            w.encrypt(password)
        existing = [f[0] for f in fields.enumerate_sig_fields(w)]
        if field_name in existing:
            new_field = None
        else:
            new_field = fields.SigFieldSpec(sig_field_name=field_name, on_page=page,
                                            box=tuple(box) if box else None)
        if new_field is not None:
            fields.append_signature_field(w, new_field)
        vc = None
        if embed_validation:
            vc = ValidationContext(allow_fetching=True, trust_roots=[signer.signing_cert])
        meta = signers.PdfSignatureMetadata(
            field_name=field_name, reason=reason or None, location=location or None,
            contact_info=contact or None, name=name or None, md_algorithm=md_algorithm,
            embed_validation_info=embed_validation, validation_context=vc, certify=certify,
            subfilter=fields.SigSeedSubFilter.PADES)
        tsa = timestamps.HTTPTimeStamper(tsa_url) if tsa_url else None
        pdf_signer = signers.PdfSigner(meta, signer=signer, timestamper=tsa,
                                       stamp_style=_stamp_style(signer, box, stamp_text, image, show_details, reason, location) if box else None)
        with open(out, "wb") as outf:
            pdf_signer.sign_pdf(w, output=outf)
    return out


def sign_pdf(path: str, out: str, p12: str, p12_password: str, field_name: str = "Imza1", page: int = 0,
             box: tuple | None = (360, 40, 560, 110), reason: str = "", location: str = "", contact: str = "",
             name: str = "", tsa_url: str | None = None, embed_validation: bool = False, certify: bool = False,
             md_algorithm: str = "sha256", stamp_text: str | None = None, image: str | None = None,
             show_details: bool = True) -> str:
    """PFX/P12 sertifikasıyla PAdES imza. box=None ise görünmez imza."""
    from pyhanko.sign import signers
    signer = signers.SimpleSigner.load_pkcs12(p12, passphrase=p12_password.encode())
    if signer is None:
        raise ValueError("Sertifika açılamadı: parola yanlış veya dosya bozuk")
    return _do_sign(path, out, signer, field_name, page, box, reason, location, contact, name, tsa_url,
                    embed_validation, certify, md_algorithm, stamp_text, image, show_details)


def sign_pdf_pkcs11(path: str, out: str, lib_path: str, pin: str, token_label: str | None = None,
                    key_label: str | None = None, cert_label: str | None = None, **kw) -> str:
    """Akıllı kart / e-imza (PKCS#11) ile imza. lib_path örn. akisp11.dll (AKİS), eTPKCS11.dll."""
    from pyhanko.sign import pkcs11
    with pkcs11.PKCS11SigningContext(pkcs11.PKCS11SignatureConfig(
            module_path=lib_path, token_criteria=pkcs11.TokenCriteria(label=token_label) if token_label else None,
            user_pin=pin, key_label=key_label, cert_label=cert_label or key_label)) as signer:
        defaults = dict(field_name="Imza1", page=0, box=(360, 40, 560, 110), reason="", location="", contact="",
                        name="", tsa_url=None, embed_validation=False, certify=False, md_algorithm="sha256",
                        stamp_text=None, image=None, show_details=True)
        defaults.update(kw)
        return _do_sign(path, out, signer, **defaults)


def list_pkcs11_tokens(lib_path: str) -> list[str]:
    import pkcs11 as p11
    lib = p11.lib(lib_path)
    return [t.label for t in lib.get_tokens()]


def verify(path: str, trust_p12: list[tuple[str, str]] | None = None) -> list[dict]:
    """Belgedeki tüm imzaları doğrular."""
    from pyhanko.pdf_utils.reader import PdfFileReader
    from pyhanko.sign.validation import validate_pdf_signature
    from pyhanko_certvalidator import ValidationContext
    import logging
    for lg in ("pyhanko", "pyhanko_certvalidator"):
        logging.getLogger(lg).setLevel(logging.CRITICAL)
    results = []
    with open(path, "rb") as f:
        r = PdfFileReader(f, strict=False)
        for sig in r.embedded_signatures:
            roots = [sig.signer_cert]
            vc = ValidationContext(trust_roots=roots, allow_fetching=False)
            vc_untrusted = ValidationContext(allow_fetching=False)
            try:
                st = validate_pdf_signature(sig, vc_untrusted)
                trusted = st.trusted
            except Exception:
                st = validate_pdf_signature(sig, vc)
                trusted = False
            st_self = validate_pdf_signature(sig, vc)
            cert = sig.signer_cert
            ts = sig.self_reported_timestamp
            results.append({
                "field": sig.field_name,
                "signer": cert.subject.human_friendly,
                "issuer": cert.issuer.human_friendly,
                "self_signed": cert.subject == cert.issuer,
                "intact": st_self.intact,
                "valid": st_self.valid,
                "trusted": trusted,
                "coverage": str(st_self.coverage).split(".")[-1],
                "modified_after": st_self.coverage.name != "ENTIRE_FILE" if hasattr(st_self.coverage, "name") else None,
                "signing_time": ts.astimezone().strftime("%d.%m.%Y %H:%M:%S") if ts else "",
                "timestamp": bool(st_self.timestamp_validity),
                "md_algorithm": st_self.md_algorithm,
                "summary": _summary(st_self, trusted),
            })
    return results


def _summary(st, trusted) -> str:
    if not st.intact:
        return "GEÇERSİZ: İmzadan sonra belge değiştirilmiş veya imza bozuk"
    if trusted:
        return "Geçerli ve güvenilir imza"
    if st.valid:
        return "İmza bütünlüğü sağlam; sertifika güvenilir kök listesinde değil (kendinden imzalı olabilir)"
    return "İmza bütünlüğü sağlam; sertifika doğrulanamadı"


def signature_fields(path: str) -> list[dict]:
    from pyhanko.pdf_utils.reader import PdfFileReader
    from pyhanko.sign import fields
    with open(path, "rb") as f:
        r = PdfFileReader(f, strict=False)
        return [{"name": name, "signed": value is not None} for name, value, _ in fields.enumerate_sig_fields(r)]
