using System.IO;
using Microsoft.Win32;
using MeslyPdfReader.Data;

namespace MeslyPdfReader.App.Services;

public static class FileDialogs
{
    public const string PdfFilter = "PDF belgeleri (*.pdf)|*.pdf";
    public const string ImageFilter = "Görüntüler (*.jpg;*.jpeg;*.png;*.tif;*.tiff;*.bmp)|*.jpg;*.jpeg;*.png;*.tif;*.tiff;*.bmp";
    public const string OpenFilter = "Desteklenen dosyalar (*.pdf;*.jpg;*.jpeg;*.png;*.tif;*.tiff;*.bmp)|*.pdf;*.jpg;*.jpeg;*.png;*.tif;*.tiff;*.bmp|" + PdfFilter + "|" + ImageFilter;
    public static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".tif", ".tiff", ".bmp" };

    public static AppSettings? Settings { get; set; }

    private static string? InitialOpen => Dir(Settings?.DefaultOpenFolder) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    private static string? Dir(string? d) => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d) ? d : null;

    public static string[] OpenFiles(string filter = OpenFilter, bool multi = true, string title = "Dosya aç")
    {
        var dlg = new OpenFileDialog { Filter = filter, Multiselect = multi, Title = title, InitialDirectory = InitialOpen, CheckFileExists = true };
        if (dlg.ShowDialog() != true) return Array.Empty<string>();
        if (Settings is not null && dlg.FileNames.Length > 0) Settings.DefaultOpenFolder = Path.GetDirectoryName(dlg.FileNames[0]);
        return dlg.FileNames;
    }

    public static string? SaveFile(string defaultName, string filter = PdfFilter, string? initialDir = null, string title = "Farklı kaydet")
    {
        var dlg = new SaveFileDialog
        {
            Filter = filter, FileName = defaultName, Title = title, AddExtension = true, OverwritePrompt = true,
            InitialDirectory = Dir(initialDir) ?? Dir(Settings?.DefaultSaveFolder) ?? InitialOpen,
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    /// <summary>Tek bir görüntü dosyası seçtirir.</summary>
    public static string? OpenImage(string title = "Görsel seçin") => OpenFiles(ImageFilter, multi: false, title).FirstOrDefault();

    public static string? PickFolder(string title = "Klasör seçin", string? initial = null)
    {
        var dlg = new OpenFolderDialog { Title = title, InitialDirectory = Dir(initial) ?? Dir(Settings?.DefaultSaveFolder) ?? InitialOpen };
        return dlg.ShowDialog() == true ? dlg.FolderName : null;
    }

    public static bool IsPdf(string path) => string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase);
    public static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());
}
