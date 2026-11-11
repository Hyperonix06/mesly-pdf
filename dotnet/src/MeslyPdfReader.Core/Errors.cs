namespace MeslyPdfReader.Core;

public enum PdfErrorKind { Unknown, File, Format, Password, Security, Page, Save, Unsupported, Cancelled }

/// <summary>Kullanıcıya gösterilebilir Türkçe açıklamalı PDF hatası.</summary>
public sealed class PdfException : Exception
{
    public PdfErrorKind Kind { get; }

    public PdfException(PdfErrorKind kind, string? detail = null, Exception? inner = null)
        : base(Describe(kind, detail), inner) => Kind = kind;

    public static string Describe(PdfErrorKind kind, string? detail = null)
    {
        var msg = kind switch
        {
            PdfErrorKind.File => "Dosya bulunamadı veya açılamadı.",
            PdfErrorKind.Format => "Dosya geçerli bir PDF değil ya da bozuk.",
            PdfErrorKind.Password => "Bu belge parola korumalı. Parola gerekli veya girilen parola yanlış.",
            PdfErrorKind.Security => "Belgenin güvenlik şeması desteklenmiyor.",
            PdfErrorKind.Page => "Sayfa bulunamadı veya sayfa içeriği hatalı.",
            PdfErrorKind.Save => "Belge kaydedilemedi.",
            PdfErrorKind.Unsupported => "Bu işlem bu belge için desteklenmiyor.",
            PdfErrorKind.Cancelled => "İşlem iptal edildi.",
            _ => "Bilinmeyen bir PDF hatası oluştu.",
        };
        return string.IsNullOrWhiteSpace(detail) ? msg : $"{msg} {detail}";
    }
}
