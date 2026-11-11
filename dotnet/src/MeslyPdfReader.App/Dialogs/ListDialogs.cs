using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using MeslyPdfReader.App.Services;
using MeslyPdfReader.Core;
using MeslyPdfReader.Pdf;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using ListView = System.Windows.Controls.ListView;
using TextBlock = System.Windows.Controls.TextBlock;
using PageRange = MeslyPdfReader.Core.PageRange;
using TextSearch = MeslyPdfReader.Core.TextSearch;
using GridView = System.Windows.Controls.GridView;
using GridViewColumn = System.Windows.Controls.GridViewColumn;

namespace MeslyPdfReader.App.Dialogs;

public sealed class FileEntry : INotifyPropertyChanged
{
    private string _range = "";
    public required string Path { get; init; }
    public string Name => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
    public string Size => ViewModels.RecentItem.FormatSize(new FileInfo(Path).Length);
    public string Range { get => _range; set { _range = value; Changed(); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>Sıralanabilir dosya listesi (ekle, kaldır, yukarı/aşağı, sürükle-bırak).</summary>
internal sealed class FileListEditor
{
    public ObservableCollection<FileEntry> Items { get; } = new();
    public ListView List { get; }
    public FrameworkElement Root { get; }
    private readonly Func<string, bool> _accept;

    public FileListEditor(string filter, Func<string, bool> accept, bool withRange, string addTitle)
    {
        _accept = accept;
        List = new ListView { ItemsSource = Items, Height = 260, SelectionMode = SelectionMode.Extended, AllowDrop = true };
        var gv = new GridView();
        gv.Columns.Add(new GridViewColumn { Header = "Dosya", Width = withRange ? 230 : 330, DisplayMemberBinding = new Binding(nameof(FileEntry.Name)) });
        gv.Columns.Add(new GridViewColumn { Header = "Boyut", Width = 80, DisplayMemberBinding = new Binding(nameof(FileEntry.Size)) });
        if (withRange)
        {
            var tpl = new DataTemplate();
            var tb = new FrameworkElementFactory(typeof(System.Windows.Controls.TextBox));
            tb.SetBinding(System.Windows.Controls.TextBox.TextProperty, new Binding(nameof(FileEntry.Range)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            tb.SetValue(FrameworkElement.WidthProperty, 120.0);
            tb.SetValue(FrameworkElement.ToolTipProperty, "Boş = tüm sayfalar. Örnek: 1-3, 5");
            tpl.VisualTree = tb;
            gv.Columns.Add(new GridViewColumn { Header = "Sayfalar (boş = tümü)", Width = 150, CellTemplate = tpl });
        }
        List.View = gv;
        List.Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files) AddFiles(files);
        };
        List.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };

        var add = Btn("Ekle…", SymbolRegular.Add24, () => AddFiles(FileDialogs.OpenFiles(filter, true, addTitle)));
        var remove = Btn("Kaldır", SymbolRegular.Delete24, () => { foreach (var s in List.SelectedItems.Cast<FileEntry>().ToList()) Items.Remove(s); });
        var up = Btn("Yukarı", SymbolRegular.ArrowUp24, () => Move(-1));
        var down = Btn("Aşağı", SymbolRegular.ArrowDown24, () => Move(+1));
        var sort = Btn("Ada göre sırala", SymbolRegular.TextSortAscending24, () =>
        {
            var sorted = Items.OrderBy(i => i.Name, StringComparer.Create(TextSearch.Turkish, true)).ToList();
            Items.Clear(); foreach (var i in sorted) Items.Add(i);
        });
        var bar = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var b in new[] { add, remove, up, down, sort }) bar.Children.Add(b);
        var sp = new StackPanel();
        sp.Children.Add(List);
        sp.Children.Add(bar);
        sp.Children.Add(DialogBase.Hint("Dosyaları bu listeye sürükleyip bırakabilirsiniz. Sıra, sonuçtaki sırayı belirler."));
        Root = sp;
    }

    private static Button Btn(string text, SymbolRegular icon, Action a)
    {
        var b = new Button { Content = text, Icon = new SymbolIcon { Symbol = icon }, Margin = new Thickness(0, 0, 6, 0) };
        b.Click += (_, _) => a();
        return b;
    }

    public void AddFiles(IEnumerable<string> files)
    {
        foreach (var f in files)
        {
            if (!_accept(f) || !File.Exists(f)) continue;
            Items.Add(new FileEntry { Path = Path.GetFullPath(f) });
        }
    }

    private void Move(int dir)
    {
        var sel = List.SelectedItems.Cast<FileEntry>().OrderBy(i => Items.IndexOf(i) * dir).ToList();
        foreach (var item in dir > 0 ? Enumerable.Reverse(sel) : sel)
        {
            int i = Items.IndexOf(item), j = i + dir;
            if (j < 0 || j >= Items.Count) return;
            Items.Move(i, j);
        }
    }
}

public sealed record MergeRequest(IReadOnlyList<(string Path, string? Range)> Items, string? SavePath, bool OpenResult);

public sealed class MergeDialog : DialogBase
{
    public static MergeRequest? Ask(IEnumerable<string> initial)
    {
        var d = new MergeDialog();
        var editor = new FileListEditor(FileDialogs.PdfFilter, FileDialogs.IsPdf, withRange: true, "Birleştirilecek PDF'leri seçin");
        editor.AddFiles(initial);
        var save = new CheckBox { Content = "Sonucu hemen bir dosyaya kaydet", IsChecked = false, Margin = new Thickness(0, 12, 0, 0) };
        var open = new CheckBox { Content = "Birleştirilen belgeyi aç", IsChecked = true, Margin = new Thickness(0, 4, 0, 0) };
        var err = Error();
        var s = new StackPanel();
        s.Children.Add(editor.Root);
        s.Children.Add(save);
        s.Children.Add(open);
        s.Children.Add(err);
        d.Body = s;
        string? savePath = null;
        d.AddOkCancel("Birleştir", () =>
        {
            if (editor.Items.Count < 2) { ShowError(err, "En az iki PDF ekleyin."); return false; }
            foreach (var it in editor.Items.Where(i => !string.IsNullOrWhiteSpace(i.Range)))
            {
                // Aralık sözdizimini önceden doğrula (sayfa sayısı birleştirme sırasında denetlenir).
                if (!PageRange.TryParse(it.Range, 100000, out _, out var e)) { ShowError(err, $"{it.Name}: {e}"); return false; }
            }
            if (save.IsChecked == true)
            {
                savePath = FileDialogs.SaveFile("Birleştirilmiş.pdf", title: "Birleştirilen belgeyi kaydet");
                if (savePath is null) return false;
            }
            return true;
        });
        if (d.ShowDialog() != true) return null;
        return new MergeRequest(editor.Items.Select(i => (i.Path, string.IsNullOrWhiteSpace(i.Range) ? null : i.Range)).ToList(), savePath, open.IsChecked == true);
    }

    private MergeDialog() : base("Belgeleri birleştir", 600) { }
}

public sealed record ImagesToPdfRequest(IReadOnlyList<string> Files, ImagePageLayout Layout, string? SavePath);

public sealed class ImagesToPdfDialog : DialogBase
{
    public static ImagesToPdfRequest? Ask(IEnumerable<string> files)
    {
        var d = new ImagesToPdfDialog();
        var editor = new FileListEditor(FileDialogs.ImageFilter, FileDialogs.IsImage, withRange: false, "Görüntüleri seçin");
        editor.AddFiles(files);
        var sizes = new List<string> { "Görüntü boyutunda (DPI'ya göre)" };
        sizes.AddRange(PaperSize.Standard.Select(p => p.ToString()));
        var paper = Form.Combo(sizes, 1);
        var margin = Form.Number(10, 0, 100, 1);
        var auto = new CheckBox { Content = "Yatay görüntüler için sayfayı yatay çevir", IsChecked = true, Margin = new Thickness(0, 10, 0, 0) };
        var save = new CheckBox { Content = "Oluşturulan PDF'yi hemen kaydet", Margin = new Thickness(0, 6, 0, 0) };
        var err = Error();
        var s = new StackPanel();
        s.Children.Add(editor.Root);
        s.Children.Add(Form.TwoColumns(Form.Labeled("Sayfa boyutu", paper, 12), Form.Labeled("Kenar boşluğu (mm)", margin, 12)));
        s.Children.Add(auto);
        s.Children.Add(save);
        s.Children.Add(Hint("JPEG dosyaları yeniden sıkıştırılmadan, kalite kaybı olmadan gömülür. Çok sayfalı TIFF'lerin tüm sayfaları eklenir."));
        s.Children.Add(err);
        d.Body = s;
        string? savePath = null;
        d.AddOkCancel("PDF oluştur", () =>
        {
            if (editor.Items.Count == 0) { ShowError(err, "En az bir görüntü ekleyin."); return false; }
            if (save.IsChecked == true)
            {
                savePath = FileDialogs.SaveFile(Path.GetFileNameWithoutExtension(editor.Items[0].Name) + ".pdf", title: "PDF'yi kaydet");
                if (savePath is null) return false;
            }
            return true;
        });
        if (d.ShowDialog() != true) return null;
        var layout = paper.SelectedIndex == 0
            ? new ImagePageLayout(null, PaperSize.MmToPt(Form.Val(margin)), false)
            : new ImagePageLayout(PaperSize.Standard[paper.SelectedIndex - 1].ToPoints(), PaperSize.MmToPt(Form.Val(margin)), auto.IsChecked == true);
        return new ImagesToPdfRequest(editor.Items.Select(i => i.Path).ToList(), layout, savePath);
    }

    private ImagesToPdfDialog() : base("Görüntülerden PDF oluştur", 600) { }
}

public sealed class RecoveryDialog : DialogBase
{
    public static IReadOnlyList<RecoveryEntry>? Ask(IReadOnlyList<RecoveryEntry> entries)
    {
        var d = new RecoveryDialog();
        var checks = new List<(CheckBox Box, RecoveryEntry Entry)>();
        var s = new StackPanel();
        s.Children.Add(Paragraph("Mesly PDF Reader beklenmedik şekilde kapanmış. Aşağıdaki belgelerin kaydedilmemiş değişiklikleri kurtarılabilir:"));
        foreach (var e in entries)
        {
            var cb = new CheckBox { IsChecked = true, Margin = new Thickness(0, 10, 0, 0) };
            var t = new StackPanel();
            t.Children.Add(new TextBlock { Text = e.DisplayName, FontWeight = FontWeights.SemiBold });
            t.Children.Add(Hint($"{e.PageCount} sayfa · {e.SavedAt:dd.MM.yyyy HH:mm} · {e.OriginalPath ?? "kaydedilmemiş yeni belge"}"));
            cb.Content = t;
            checks.Add((cb, e));
            s.Children.Add(cb);
        }
        s.Children.Add(Hint("Seçilmeyen kurtarma kopyaları silinir. Kurtarılan belgeler kaydedilene kadar özgün dosyalarınız değişmez."));
        d.Body = s;
        d.AddButton("Hiçbirini kurtarma", () => d.DialogResult = false, isCancel: true);
        d.AddButton("Seçilenleri kurtar", () => d.DialogResult = true, primary: true, isDefault: true);
        return d.ShowDialog() == true ? checks.Where(c => c.Box.IsChecked == true).Select(c => c.Entry).ToList() : null;
    }

    private RecoveryDialog() : base("Belge kurtarma", 540) { }
}
