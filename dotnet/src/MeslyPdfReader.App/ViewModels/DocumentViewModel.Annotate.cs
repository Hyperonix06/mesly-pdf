using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeslyPdfReader.App.Controls;
using MeslyPdfReader.App.Dialogs;
using MeslyPdfReader.Pdf;

namespace MeslyPdfReader.App.ViewModels;

/// <summary>
/// Afiş 8 — PDF Düzenleme: açıklamalar (vurgu, altı/üstü çizili, not), imza, tarih ve ad damgası, form doldurma,
/// sıfırlama, düzleştirme; görünüm komutları (sayfa paneli). Hepsi belgeye gerçek PDF nesnesi olarak yazılır ve geri alınabilir.
/// </summary>
public sealed partial class DocumentViewModel
{
    private string? _pendingStamp;

    /// <summary>Açıklama rengi (#RRGGBB).</summary>
    public string AnnotColor => _settings.AnnotColor;
    private uint AnnotRgb => uint.Parse(_settings.AnnotColor.TrimStart('#'), NumberStyles.HexNumber);

    [RelayCommand]
    private void SetAnnotColor(string? hex)
    {
        if (hex is not { Length: 7 } || !uint.TryParse(hex[1..], NumberStyles.HexNumber, null, out _)) return;
        _settings.AnnotColor = hex.ToUpperInvariant();
        OnPropertyChanged(nameof(AnnotColor));
        StatusText = "Açıklama rengi değiştirildi; yeni vurgu ve notlar bu renkte eklenir.";
    }

    /// <summary>Görüntüleyicide işaretleme aracıyla seçilen metin (sayfa başına).</summary>
    public Task<bool> AddMarkupAsync(MarkupRequest r)
    {
        var kind = r.Kind switch { ViewerTool.Underline => AnnotKind.Underline, ViewerTool.StrikeOut => AnnotKind.StrikeOut, _ => AnnotKind.Highlight };
        // Altı/üstü çizgi kırmızı ağırlıklı olsun: sarı vurgu rengi çizgi için zor okunur.
        uint rgb = kind == AnnotKind.Highlight ? AnnotRgb : AnnotRgb == 0xFFE14D ? 0xD91315u : AnnotRgb;
        var name = kind switch { AnnotKind.Underline => "Altı çizildi", AnnotKind.StrikeOut => "Üstü çizildi", _ => "Vurgulandı" };
        var text = r.Text.Length > 40 ? r.Text[..40] + "…" : r.Text;
        return EditAsync("Açıklama ekleniyor…", () => Document.AddMarkupAsync(r.Page, kind, r.Rects, rgb), $"{name}: “{text.Replace('\n', ' ')}”");
    }

    public async Task<bool> AddNoteAtAsync(int page, double x, double y, string? text = null)
    {
        text ??= NoteDialog.Ask();
        if (text is null) return false;
        return await EditAsync("Açıklama ekleniyor…", () => Document.AddNoteAsync(page, x, y, text.Trim(), AnnotRgb), "Açıklama eklendi.");
    }

    [RelayCommand]
    private async Task ShowAnnotations()
    {
        IReadOnlyList<AnnotationInfo> all = Array.Empty<AnnotationInfo>();
        if (!await RunBusyAsync("Açıklamalar okunuyor…", async (_, ct) => all = await Document.GetAllAnnotationsAsync(ct))) return;
        var r = AnnotationListDialog.Ask(all);
        if (r is null) return;
        if (r.ToDelete.Count > 0)
        {
            int n = 0;
            await EditAsync("Açıklamalar siliniyor…", async () =>
            {
                // Aynı sayfadaki dizinler kaymasın diye sondan başa
                foreach (var a in r.ToDelete.OrderByDescending(a => a.Page).ThenByDescending(a => a.Index))
                    if (await Document.RemoveAnnotationAsync(a)) n++;
            }, $"{r.ToDelete.Count} açıklama silindi.");
        }
        else if (r.GoTo is { } g) CurrentPage = g.Page;
    }

    [RelayCommand]
    private async Task RemovePageAnnotations()
    {
        var list = await Document.GetAnnotationsAsync(CurrentPage);
        if (list.Count == 0) { StatusText = $"Sayfa {CurrentPage + 1}'de açıklama yok."; return; }
        if (!MessageDialog.Confirm("Açıklamaları kaldır", $"Sayfa {CurrentPage + 1}'deki {list.Count} açıklama (vurgu, çizgi, not) kaldırılacak. Bağlantılar ve form alanları kalır. Geri Al ile geri getirilebilir.", "Kaldır", "Vazgeç")) return;
        await EditAsync("Açıklamalar kaldırılıyor…", () => Document.RemovePageAnnotationsAsync(CurrentPage), $"Sayfa {CurrentPage + 1}'deki açıklamalar kaldırıldı.");
    }

    // ─────────────── İmza, tarih, ad ───────────────

    private void BeginPlace(EditPlacement kind, string status)
    {
        Tool = ViewerTool.Edit;
        Placement = kind;
        StatusText = status;
    }

    [RelayCommand]
    private void PlaceSignature()
    {
        var img = SignatureDialog.Ask();
        if (img is null) return;
        _pendingImage = new BitmapImageSource(img) { Transparent = true };
        BeginPlace(EditPlacement.Image, "İmzanın yerleşeceği alanı sayfada çizin veya bir noktaya tıklayın. Esc ile vazgeçin.");
    }

    [RelayCommand] private void ManageSignatures() => SignatureManagerDialog.ShowDialogFor();

    [RelayCommand]
    private void PlaceDateStamp()
    {
        _pendingStamp = DateTime.Now.ToString("dd.MM.yyyy", CultureInfo.GetCultureInfo("tr-TR"));
        BeginPlace(EditPlacement.Stamp, $"Tarihin ({_pendingStamp}) yazılacağı yere tıklayın.");
    }

    [RelayCommand]
    private void PlaceName()
    {
        var name = InputDialog.Ask("Ad Soyad", "Belgeye yazılacak ad", _settings.SignName ?? "", "Ad bir sonraki kullanım için hatırlanır.",
            v => string.IsNullOrWhiteSpace(v) ? "Ad boş olamaz." : null);
        if (name is null) return;
        _settings.SignName = name.Trim();
        _pendingStamp = name.Trim();
        BeginPlace(EditPlacement.Stamp, "Adın yazılacağı yere tıklayın.");
    }

    // ─────────────── Form ───────────────

    [RelayCommand]
    private async Task FillForm()
    {
        IReadOnlyList<FormFieldInfo> fields = Array.Empty<FormFieldInfo>();
        if (!await RunBusyAsync("Form alanları okunuyor…", async (_, _) => fields = await Document.GetFormFieldsAsync())) return;
        var fillable = fields.Where(f => f.Kind is not (FormFieldKind.PushButton or FormFieldKind.Signature or FormFieldKind.Unknown)).ToList();
        if (fillable.Count == 0)
        {
            MessageDialog.Show("Form alanı yok", "Bu belgede doldurulabilir form alanı bulunmuyor. Boş bir alana yazı eklemek için Giriş sekmesindeki “Metin Ekle”yi kullanabilirsiniz.", MessageKind.Info);
            return;
        }
        var changes = FormFillDialog.Ask(fillable);
        if (changes is null || changes.Count == 0) return;
        int n = 0;
        await EditAsync("Form dolduruluyor…", async () => n = await Document.SetFormFieldsAsync(changes), $"{changes.Count} form alanı güncellendi.");
        if (n < changes.Count) StatusText = $"{n}/{changes.Count} alan güncellendi; salt okunur veya desteklenmeyen alanlar atlandı.";
    }

    [RelayCommand]
    private async Task ResetForm()
    {
        var fields = await Document.GetFormFieldsAsync();
        var changes = new List<FormFieldChange>();
        foreach (var f in fields)
        {
            if (f.ReadOnly) continue;
            if (f.Kind == FormFieldKind.Text && f.Value.Length > 0) changes.Add(new FormFieldChange(f, Text: ""));
            else if (f.Kind == FormFieldKind.CheckBox && f.Checked) changes.Add(new FormFieldChange(f, Checked: false));
            else if (f.Kind is FormFieldKind.ComboBox or FormFieldKind.ListBox && f.SelectedOption > 0) changes.Add(new FormFieldChange(f, Option: 0));
        }
        if (fields.Count == 0) { MessageDialog.Show("Form alanı yok", "Bu belgede form alanı bulunmuyor.", MessageKind.Info); return; }
        if (changes.Count == 0) { StatusText = "Form zaten boş."; return; }
        if (!MessageDialog.Confirm("Formu sıfırla", $"{changes.Count} alanın değeri temizlenecek (metin alanları boşaltılır, onay kutuları kaldırılır, listeler ilk seçeneğe döner). Geri Al ile geri getirilebilir.", "Sıfırla", "Vazgeç")) return;
        await EditAsync("Form sıfırlanıyor…", () => Document.SetFormFieldsAsync(changes), "Form sıfırlandı.");
    }

    [RelayCommand]
    private async Task Flatten()
    {
        if (!MessageDialog.Confirm("Düzleştir", "Form alanları ve açıklamalar sayfa içeriğine dönüştürülecek: görünümleri aynen kalır ancak artık doldurulamaz veya açıklama olarak düzenlenemez. Geri Al ile geri dönebilirsiniz.", "Düzleştir", "Vazgeç")) return;
        int n = 0;
        await EditAsync("Belge düzleştiriliyor…", async () => n = await Document.FlattenAsync(), "Düzleştirildi.");
        StatusText = n == 0 ? "Düzleştirilecek form alanı veya açıklama bulunamadı." : $"{n} sayfa düzleştirildi.";
    }

    // ─────────────── Sayfa Yönetimi ve Tarama ekranları ───────────────

    /// <summary>Sayfaları yeni sıraya dizer (Sayfa Yönetimi › Sırala › Uygula). Geri alınabilir.</summary>
    public Task<bool> ReorderPagesAsync(IReadOnlyList<int> order) =>
        EditAsync("Sayfalar sıralanıyor…", () => Document.ReorderPagesAsync(order), "Sayfalar yeniden sıralandı.");

    /// <summary>Başka bir belgenin tüm sayfalarını sona ekler (Tarama › Mevcut PDF'e ekle). Geri alınabilir.</summary>
    public Task<bool> AppendDocumentAsync(PdfDocument source, string status) =>
        EditAsync("Sayfalar ekleniyor…", () => Document.ImportPagesAsync(source, null, PageCount), status);

    // ─────────────── Görünüm ───────────────

    private LeftPanelKind _lastLeftPanel = LeftPanelKind.Thumbnails;

    [RelayCommand]
    private void ToggleLeftPanel()
    {
        if (LeftPanel == LeftPanelKind.None) LeftPanel = _lastLeftPanel;
        else { _lastLeftPanel = LeftPanel; LeftPanel = LeftPanelKind.None; }
    }
}
