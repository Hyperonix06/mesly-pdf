using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeslyPdfReader.App.Controls;
using MeslyPdfReader.App.Dialogs;
using MeslyPdfReader.App.Services;
using MeslyPdfReader.Core;
using MeslyPdfReader.Pdf;

namespace MeslyPdfReader.App.ViewModels;

/// <summary>Aşama 3: içerik düzenleme (Düzenle aracı, özellikler paneli, şerit komutları).</summary>
public sealed partial class DocumentViewModel : IPdfEditHost
{
    [ObservableProperty] private EditPlacement _placement;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasEditSelection), nameof(IsTextSelection), nameof(IsObjectSelection), nameof(IsImageSelection),
        nameof(IsShapeSelection), nameof(IsMultiSelection), nameof(SelectionTitle), nameof(SelectionNote))]
    private EditSelection? _editSelection;

    // Metin biçimi (seçili paragraf veya yeni metin için)
    [ObservableProperty] private string _editFontFamily = "Arial";
    [ObservableProperty] private double _editFontSize = 12;
    [ObservableProperty] private string _editColor = "#000000";
    [ObservableProperty] private bool _editBold;
    [ObservableProperty] private bool _editItalic;
    [ObservableProperty] private bool _editUnderline;
    [ObservableProperty] private int _editAlign; // 0 sol, 1 orta, 2 sağ
    [ObservableProperty] private double _editLineSpacing = 1.2;
    // Konum/boyut (mm, sol-alt köşe sayfa koordinatında)
    [ObservableProperty] private double _objX, _objY, _objW, _objH;
    [ObservableProperty] private string _shapeFill = "";
    [ObservableProperty] private string _shapeStroke = "#000000";
    [ObservableProperty] private double _shapeStrokeWidth = 1.5;

    public ObservableCollection<string> FontFamilies { get; } = new();
    public static IReadOnlyList<string> AlignNames { get; } = new[] { "Sola", "Ortaya", "Sağa" };

    public bool HasEditSelection => EditSelection is not null;
    public bool IsTextSelection => EditSelection?.Single?.IsText == true;
    public bool IsObjectSelection => EditSelection?.Single is { IsText: false };
    public bool IsImageSelection => EditSelection?.Single?.Object?.Kind == PageObjectKind.Image;
    public bool IsShapeSelection => EditSelection?.Single?.Object?.Kind == PageObjectKind.Path;
    public bool IsMultiSelection => EditSelection?.Items.Count > 1;
    public bool IsEditTool => Tool == ViewerTool.Edit;

    public string SelectionTitle => EditSelection switch
    {
        null => "Seçim yok",
        { Items.Count: > 1 } s => $"{s.Items.Count} öğe seçili (sayfa {s.Page + 1})",
        { Single: { } i } s => $"{i.KindName} (sayfa {s.Page + 1})",
        _ => "",
    };

    public string SelectionNote => EditSelection?.Single switch
    {
        null => "Bir metne, görsele veya şekle tıklayın. Ctrl ile çoklu seçim; sürükleyerek taşıyın, metne çift tıklayarak düzenleyin.",
        { Object.Kind: PageObjectKind.Form } => "Form nesnesi (gruplanmış içerik): taşınabilir ve boyutlandırılabilir; içindeki metin doğrudan düzenlenemez.",
        { Object.Kind: PageObjectKind.Shading } => "Gölgelendirme (renk geçişi) nesnesi: taşınabilir veya silinebilir.",
        { Paragraph: { } p } when p.ObjectIndices.Count > 0 && _invisibleText => "Görünmez metin (OCR metin katmanı). Değiştirilirse görünür metin olarak yazılır.",
        { Paragraph: { } p } => $"Özgün yazı tipi: {p.FontName ?? "bilinmiyor"} · {p.FontSize:0.#} pt",
        _ => "",
    };
    private bool _invisibleText;

    partial void OnToolChanged(ViewerTool value)
    {
        OnPropertyChanged(nameof(IsEditTool));
        _bannerCheckRev = -1;
        _ = UpdateScanBannerAsync();
        if (value == ViewerTool.Edit)
        {
            if (ReferenceEquals(Main.ActiveTab, this) && Main.Ribbon != RibbonTab.Edit) Main.Ribbon = RibbonTab.Edit;
            ShowProperties = true;
            _ = LoadFontsAsync();
        }
    }

    private async Task LoadFontsAsync()
    {
        if (FontFamilies.Count > 0) return;
        try
        {
            foreach (var f in await FontResolver.Instance.GetFamiliesAsync()) FontFamilies.Add(f);
            if (!FontFamilies.Contains(EditFontFamily) && FontFamilies.Count > 0) EditFontFamily = FontFamilies.FirstOrDefault(f => f == "Arial") ?? FontFamilies[0];
        }
        catch (Exception ex) { Log.Warn($"Yazı tipleri listelenemedi: {ex.Message}"); }
    }

    // ─────────────── IPdfEditHost ───────────────

    public void NotifyEditSelection(EditSelection? selection)
    {
        _invisibleText = false;
        EditSelection = selection;
        if (selection?.Single is not { } item) return;
        var b = item.Bounds;
        ObjX = Mm(b.Left); ObjY = Mm(b.Bottom); ObjW = Mm(b.Width); ObjH = Mm(b.Height);
        if (item.Paragraph is { } p)
        {
            var fam = TextLayout.ParseFontName(p.FontName).Family;
            var match = FontFamilies.FirstOrDefault(f => FontMatching.Key(f) == FontMatching.Key(fam));
            EditFontFamily = match ?? (fam.Length > 0 ? fam : EditFontFamily);
            EditFontSize = Math.Round(p.FontSize, 1);
            EditColor = Hex(p.Rgb);
            EditBold = p.Bold; EditItalic = p.Italic;
            EditAlign = (int)p.Align;
            EditLineSpacing = p.FontSize > 0 ? Math.Round(p.LineHeight / p.FontSize, 2) : 1.2;
            _ = CheckInvisibleAsync(selection.Page, p);
        }
        else if (item.Object is { } o)
        {
            ShapeFill = o.FillRgb is var f && o.Kind == PageObjectKind.Path ? Hex(f) : "";
            ShapeStroke = Hex(o.StrokeRgb);
        }
    }

    private async Task CheckInvisibleAsync(int page, TextParagraph p)
    {
        try
        {
            var objs = await Document.GetPageObjectsAsync(page);
            _invisibleText = p.ObjectIndices.All(i => i < objs.Count && objs[i].Invisible);
            OnPropertyChanged(nameof(SelectionNote));
        }
        catch { }
    }

    public string? UiFontFamily(TextParagraph? paragraph)
    {
        if (paragraph is null) return EditFontFamily;
        var fam = TextLayout.ParseFontName(paragraph.FontName).Family;
        return FontFamilies.FirstOrDefault(f => FontMatching.Key(f) == FontMatching.Key(fam)) ?? (fam.Length > 0 ? fam : null);
    }

    public Task<bool> MoveAsync(int page, IReadOnlyList<int> indices, double dx, double dy) =>
        EditAsync("Taşınıyor…", () => Document.MoveObjectsAsync(page, indices, dx, dy), $"{indices.Count} nesne taşındı ({Mm(dx):0.#} × {Mm(dy):0.#} mm).");

    public Task<bool> DeleteAsync(int page, IReadOnlyList<int> indices) =>
        EditAsync("Siliniyor…", () => Document.DeleteObjectsAsync(page, indices), $"{indices.Count} nesne sayfadan silindi. Geri almak için Ctrl+Z.");

    public Task<bool> ResizeAsync(int page, EditItem item, RectF target)
    {
        if (item.Paragraph is { } p)
        {
            // Paragraf genişliğini değiştir → metin yeni genişliğe göre yeniden akar (ölçekleme yok).
            var moved = p with { Bounds = new RectF(target.Left, p.Bounds.Bottom, target.Right, p.Bounds.Top) };
            return ReplaceWithStyleAsync(page, moved, p.Text, CurrentStyleFrom(p), target.Width, "Paragraf yeniden akıtıldı.");
        }
        bool keep = item.Object?.Kind == PageObjectKind.Image && System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift) == false;
        return EditAsync("Boyutlandırılıyor…", () => Document.ResizeObjectAsync(page, item.Indices[0], target, keepAspect: keep),
            keep ? "Görsel orantılı olarak boyutlandırıldı (serbest boyut için Shift)." : "Nesne boyutlandırıldı.");
    }

    public Task<bool> ReplaceTextAsync(int page, TextParagraph paragraph, string text) =>
        ReplaceWithStyleAsync(page, paragraph, text, CurrentStyleFrom(paragraph), null, "Metin değiştirildi.");

    public async Task<bool> AddTextAsync(int page, double left, double top, double width, string text)
    {
        var (style, note) = await BuildStyleAsync(null, text, EditFontFamily);
        return await EditAsync("Metin ekleniyor…", () => Document.AddTextBlockAsync(page, left, top, width, text, style), Status("Metin eklendi.", note));
    }

    public async Task<bool> PlaceAsync(EditPlacement kind, int page, double x1, double y1, double x2, double y2)
    {
        var rect = new RectF(x1, y1, x2, y2);
        switch (kind)
        {
            case EditPlacement.Rectangle or EditPlacement.Ellipse or EditPlacement.Line or EditPlacement.Arrow:
                {
                    var shape = kind switch { EditPlacement.Rectangle => ShapeKind.Rectangle, EditPlacement.Ellipse => ShapeKind.Ellipse, EditPlacement.Line => ShapeKind.Line, _ => ShapeKind.Arrow };
                    var style = new ShapeStyle(ParseRgb(ShapeStroke) ?? 0, Math.Max(0, ShapeStrokeWidth), ParseRgb(ShapeFill));
                    var r = shape is ShapeKind.Line or ShapeKind.Arrow ? rect : rect.Normalize();
                    return await EditAsync("Şekil ekleniyor…", () => Document.AddShapeAsync(page, shape, r, style), "Şekil eklendi.");
                }
            case EditPlacement.Image:
                {
                    var img = _pendingImage;
                    _pendingImage = null;
                    if (img is null) return false;
                    return await EditAsync("Görsel ekleniyor…", () => Document.AddImageAsync(page, img, rect.Normalize()), "Görsel eklendi.");
                }
            case EditPlacement.Link:
                {
                    var url = LinkDialog.Ask();
                    if (url is null) return false;
                    return await EditAsync("Bağlantı ekleniyor…", () => Document.AddLinkAsync(page, rect.Normalize(), url), $"Bağlantı eklendi: {url}");
                }
        }
        return false;
    }

    public async Task<bool> RemoveLinkAtAsync(int page, double x, double y)
    {
        bool removed = false;
        var ok = await EditAsync("Bağlantı kaldırılıyor…", async () =>
        {
            removed = await Document.RemoveLinkAtAsync(page, x, y);
            if (!removed) throw new PdfException(PdfErrorKind.Unsupported, "Tıklanan noktada bağlantı yok.");
        }, "Bağlantı kaldırıldı.");
        Placement = EditPlacement.None;
        return ok && removed;
    }

    // ─────────────── Biçim ───────────────

    private TextStyle CurrentStyleFrom(TextParagraph p) =>
        new(Array.Empty<byte>(), EditFontSize > 0 ? EditFontSize : p.FontSize, ParseRgb(EditColor) ?? p.Rgb, (TextAlign)Math.Clamp(EditAlign, 0, 2), EditUnderline, EditLineSpacing);

    private async Task<(TextStyle Style, string? Note)> BuildStyleAsync(TextParagraph? p, string text, string? family, TextStyle? baseStyle = null)
    {
        var (data, choice) = await FontResolver.Instance.ResolveAsync(p?.FontName, text, EditBold, EditItalic, family);
        var s = baseStyle ?? new TextStyle(data, EditFontSize, ParseRgb(EditColor) ?? 0, (TextAlign)Math.Clamp(EditAlign, 0, 2), EditUnderline, EditLineSpacing);
        string? note = choice.Note;
        if (choice.Font is { } f && note is null && (f.Bold != EditBold || f.Italic != EditItalic))
            note = $"“{f.Family}” için istenen kalın/eğik biçim kurulu değil; normal biçim kullanıldı.";
        return (s with { FontData = data }, note);
    }

    private async Task<bool> ReplaceWithStyleAsync(int page, TextParagraph p, string text, TextStyle style, double? width, string status)
    {
        var fam = string.IsNullOrWhiteSpace(EditFontFamily) ? null : EditFontFamily;
        var (s, note) = await BuildStyleAsync(p, text, fam, style);
        return await EditAsync("Metin yazılıyor…", () => Document.ReplaceParagraphAsync(page, p, text, s, width), Status(status, note));
    }

    private static string Status(string s, string? note) => note is null ? s : $"{s} Not: {note}";

    [RelayCommand]
    private async Task ApplyTextStyle()
    {
        if (EditSelection?.Single?.Paragraph is not { } p) return;
        await ReplaceWithStyleAsync(EditSelection.Page, p, p.Text, CurrentStyleFrom(p), null, "Metin biçimi uygulandı.");
    }

    [RelayCommand]
    private async Task ApplyGeometry()
    {
        if (EditSelection?.Single is not { } item) return;
        var target = new RectF(Pt(ObjX), Pt(ObjY), Pt(ObjX) + Math.Max(1, Pt(ObjW)), Pt(ObjY) + Math.Max(1, Pt(ObjH)));
        var b = item.Bounds;
        bool sizeChanged = Math.Abs(target.Width - b.Width) > 0.3 || Math.Abs(target.Height - b.Height) > 0.3;
        if (item.IsText || !sizeChanged)
            await MoveAsync(EditSelection.Page, item.Indices, target.Left - b.Left, target.Bottom - b.Bottom);
        else
            await EditAsync("Boyutlandırılıyor…", () => Document.ResizeObjectAsync(EditSelection.Page, item.Indices[0], target), "Konum ve boyut uygulandı.");
    }

    [RelayCommand]
    private async Task ApplyShapeStyle()
    {
        if (EditSelection is not { } s || !IsShapeSelection) return;
        var idx = s.Indices;
        var fill = ParseRgb(ShapeFill);
        var stroke = ParseRgb(ShapeStroke);
        await EditAsync("Renkler uygulanıyor…", async () =>
        {
            if (fill is { } f) await Document.SetFillColorAsync(s.Page, idx, f);
            if (stroke is { } st) await Document.SetStrokeAsync(s.Page, idx, st, ShapeStrokeWidth);
        }, "Şekil renkleri uygulandı.");
    }

    [RelayCommand]
    private async Task SetTextColor()
    {
        if (EditSelection is not { } s || ParseRgb(EditColor) is not { } rgb) return;
        await EditAsync("Renk uygulanıyor…", () => Document.SetFillColorAsync(s.Page, s.Indices, rgb), "Renk uygulandı (metin yeniden yazılmadı).");
    }

    // ─────────────── Şerit komutları ───────────────

    private ImagePageSource? _pendingImage;

    [RelayCommand]
    private void ToggleEditTool() => Tool = Tool == ViewerTool.Edit ? ViewerTool.Select : ViewerTool.Edit;

    [RelayCommand]
    private void Place(EditPlacement kind)
    {
        if (kind == EditPlacement.Image)
        {
            var file = FileDialogs.OpenImage();
            if (file is null) return;
            try { _pendingImage = ImageCodec.LoadForPdf(file)[0]; }
            catch (Exception ex) { ShowError("Görsel açma", ex); return; }
        }
        Tool = ViewerTool.Edit;
        Placement = Placement == kind ? EditPlacement.None : kind;
        StatusText = kind switch
        {
            EditPlacement.Text => "Metin kutusu için sayfaya tıklayın veya bir alan çizin.",
            EditPlacement.Image => "Görselin yerleşeceği alanı sayfada sürükleyerek çizin (tıklarsanız 200 pt genişlikte yerleşir).",
            EditPlacement.Link => "Bağlantının tıklanabilir alanını sayfada sürükleyerek çizin.",
            EditPlacement.RemoveLink => "Kaldırmak istediğiniz bağlantıya tıklayın.",
            _ => "Şekil için sayfada sürükleyin. Esc ile vazgeçebilirsiniz.",
        };
    }

    [RelayCommand]
    private void DeleteSelected()
    {
        if (EditSelection is not { } s) return;
        Viewer?.ClearEditSelection();
        _ = DeleteAsync(s.Page, s.Indices);
    }

    [RelayCommand]
    private void EditSelectedText() { if (Viewer?.BeginEditSelectedText() != true) StatusText = "Önce bir metin paragrafı seçin."; }

    [RelayCommand]
    private async Task AlignSelected(AlignMode mode)
    {
        if (EditSelection is not { Items.Count: > 1 } s) { StatusText = "Hizalamak için Ctrl ile en az iki öğe seçin."; return; }
        await EditAsync("Hizalanıyor…", () => AlignItemsAsync(s, mode), "Öğeler hizalandı.");
    }

    /// <summary>Paragraflar birden çok nesne içerebildiği için her öğe bütün olarak kaydırılır.</summary>
    private async Task AlignItemsAsync(EditSelection s, AlignMode mode)
    {
        var all = s.Bounds;
        foreach (var it in s.Items)
        {
            var b = it.Bounds;
            double dx = mode switch { AlignMode.Left => all.Left - b.Left, AlignMode.Right => all.Right - b.Right, AlignMode.CenterH => (all.Left + all.Right - b.Left - b.Right) / 2, _ => 0 };
            double dy = mode switch { AlignMode.Top => all.Top - b.Top, AlignMode.Bottom => all.Bottom - b.Bottom, AlignMode.Middle => (all.Bottom + all.Top - b.Bottom - b.Top) / 2, _ => 0 };
            if (Math.Abs(dx) > 0.01 || Math.Abs(dy) > 0.01) await Document.MoveObjectsAsync(s.Page, it.Indices, dx, dy);
        }
    }

    [RelayCommand]
    private async Task ReplaceSelectedImage()
    {
        if (EditSelection?.Single is not { Object.Kind: PageObjectKind.Image } item) { StatusText = "Önce bir görsel seçin."; return; }
        var file = FileDialogs.OpenImage();
        if (file is null) return;
        ImagePageSource img;
        try { img = ImageCodec.LoadForPdf(file)[0]; } catch (Exception ex) { ShowError("Görsel açma", ex); return; }
        await EditAsync("Görsel değiştiriliyor…", () => Document.ReplaceImageAsync(EditSelection.Page, item.Indices[0], img), "Görsel değiştirildi.");
    }

    [RelayCommand]
    private async Task AddWatermark()
    {
        var r = WatermarkDialog.Ask(PageCount, CurrentPage);
        if (r is null) return;
        var (style, note) = await BuildStyleAsync(null, r.Value.Text, r.Value.Family, new TextStyle(Array.Empty<byte>(), r.Value.Size, r.Value.Rgb));
        var s = new WatermarkSettings(r.Value.Text, style, r.Value.Angle, r.Value.Opacity);
        await EditAsync("Filigran ekleniyor…", () => Document.AddWatermarkAsync(r.Value.Pages, s), Status($"{r.Value.Pages.Count} sayfaya filigran eklendi.", note));
    }

    [RelayCommand]
    private async Task RemoveWatermarks()
    {
        int n = 0;
        if (await EditAsync("Filigranlar kaldırılıyor…", async () => n = await Document.RemoveWatermarksAsync(Enumerable.Range(0, PageCount)), ""))
            StatusText = n > 0 ? $"{n} filigran kaldırıldı." : "Kaldırılacak filigran bulunamadı (yalnızca Mesly PDF Reader ile eklenen filigranlar tanınır).";
    }

    [RelayCommand]
    private Task AddHeaderFooter() => HeaderFooterCore(pageNumbersOnly: false);
    [RelayCommand]
    private Task AddPageNumbers() => HeaderFooterCore(pageNumbersOnly: true);

    private async Task HeaderFooterCore(bool pageNumbersOnly)
    {
        var r = HeaderFooterDialog.Ask(PageCount, pageNumbersOnly);
        if (r is null) return;
        var sample = string.Concat(r.Value.Items.Select(i => i.Template)) + "0123456789/ " + Path.GetFileName(FilePath ?? "");
        var (style, note) = await BuildStyleAsync(null, sample, r.Value.Family, new TextStyle(Array.Empty<byte>(), r.Value.Size, r.Value.Rgb));
        var s = new HeaderFooterSettings(r.Value.Items, style, r.Value.Margin, r.Value.Start, Path.GetFileName(FilePath ?? Title));
        await EditAsync("Üst/altbilgi ekleniyor…", () => Document.AddHeaderFooterAsync(r.Value.Pages, s), Status($"{r.Value.Pages.Count} sayfaya eklendi.", note));
    }

    [RelayCommand]
    private async Task RemoveHeaderFooter()
    {
        int n = 0;
        if (await EditAsync("Üst/altbilgiler kaldırılıyor…", async () => n = await Document.RemoveHeaderFooterAsync(Enumerable.Range(0, PageCount)), ""))
            StatusText = n > 0 ? $"{n} üst/altbilgi öğesi kaldırıldı." : "Kaldırılacak üst/altbilgi bulunamadı (yalnızca Mesly PDF Reader ile eklenenler tanınır).";
    }

    [RelayCommand]
    private async Task SetBackground()
    {
        var r = BackgroundDialog.Ask(PageCount, CurrentPage);
        if (r is null) return;
        await EditAsync("Arka plan uygulanıyor…", () => Document.SetBackgroundAsync(r.Value.Pages, r.Value.Rgb), $"{r.Value.Pages.Count} sayfaya arka plan rengi uygulandı.");
    }

    [RelayCommand]
    private async Task RemoveBackground()
    {
        int n = 0;
        if (await EditAsync("Arka plan kaldırılıyor…", async () => n = await Document.RemoveBackgroundAsync(Enumerable.Range(0, PageCount)), ""))
            StatusText = n > 0 ? $"{n} sayfanın arka planı kaldırıldı." : "Mesly PDF Reader ile eklenmiş arka plan bulunamadı.";
    }

    // ─────────────── Yardımcılar ───────────────

    private static double Mm(double pt) => Math.Round(pt / 72.0 * 25.4, 1);
    private static double Pt(double mm) => mm / 25.4 * 72.0;
    public static string Hex(uint rgb) => $"#{rgb & 0xFFFFFF:X6}";

    public static uint? ParseRgb(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim().TrimStart('#');
        if (s.Length == 3) s = string.Concat(s.Select(c => $"{c}{c}"));
        return s.Length == 6 && uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : null;
    }
}
