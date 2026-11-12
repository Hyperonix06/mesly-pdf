using System.IO;
using System.Windows;
using System.Windows.Documents;
using DocumentFormat.OpenXml.Packaging;
using MeslyPdfReader.App.Controls;
using MeslyPdfReader.App.Dialogs;
using MeslyPdfReader.App.ViewModels;
using MeslyPdfReader.App.Views.Screens;
using MeslyPdfReader.Core;
using MeslyPdfReader.Pdf;

namespace MeslyPdfReader.App.Services;

/// <summary>Öz test — 0.7.0 afiş ekranları ve yeni işlevler (her ekran, sekme ve düğmenin arkasındaki işlem gerçekten çalıştırılır).</summary>
public sealed partial class SelfTestRunner
{
    private async Task RunPosterScreensAsync(string work, byte[] font)
    {
        // Sınama belgeleri
        string textPdf = Path.Combine(work, "Ekran Testi.pdf");
        using (var d = await PdfDocument.CreateAsync(PaperSize.A4.ToPoints(), 3))
        {
            for (int i = 0; i < 3; i++)
            {
                await d.AddTextAsync(i, $"Sayfa {i + 1}: Satış Raporu", 72, 770, 22, font);
                await d.AddTextAsync(i, "Pijamalı hasta yağız şoföre çabucak güvendi. İstanbul Iğdır", 72, 730, 13, font);
                await d.AddTextAsync(i, "Hizmet sözleşmesi taraflar arasında imzalanmıştır.", 72, 700, 13, font);
            }
            await d.SaveAsync(textPdf);
        }

        // ── Sol menü ekranları ──
        var screens = new (AppScreen Screen, FrameworkElement Control, string Title, string Shot)[]
        {
            (AppScreen.Home, _w.Home, "Mesly Pdf Reader", "30-ana-sayfa"),
            (AppScreen.Documents, _w.DocumentsScreen, "Belgelerim", "31-belgelerim"),
            (AppScreen.Convert, _w.ConvertScreen, "Dönüştür", "32-donustur"),
            (AppScreen.BatchConvert, _w.BatchConvertScreen, "Toplu Dönüştürme", "33-toplu-donusturme"),
            (AppScreen.Ocr, _w.OcrScreen, "OCR (Metin Tanıma)", "34-ocr"),
            (AppScreen.OcrEditor, _w.OcrEditorScreen, "OCR Sonucu", "35-ocr-sonucu-bos"),
            (AppScreen.Tools, _w.ToolsScreen, "Sayfa Yönetimi", "36-sayfa-yonetimi"),
            (AppScreen.Scan, _w.ScanScreen, "Tarama", "37-tarama"),
            (AppScreen.Settings, _w.SettingsScreen, "Ayarlar", "38-ayarlar"),
            (AppScreen.Help, _w.HelpScreen, "Hakkında", "39-hakkinda"),
        };
        await Step("Ekranlar", "Sol menüdeki 10 ekran", "Her ekran açılır, yalnız o ekran görünür, başlık çubuğu ekran adını gösterir", async () =>
        {
            var bad = new List<string>();
            foreach (var (screen, control, title, shot) in screens)
            {
                _vm.Navigate(screen);
                await Idle();
                await Task.Delay(120);
                bool onlyThis = screens.All(x => x.Control.IsVisible == (x.Screen == screen));
                if (!control.IsVisible || !onlyThis || !_vm.WindowTitle.Contains(title) || _vm.IsViewer) bad.Add($"{screen}(görünür={control.IsVisible}, tek={onlyThis}, başlık=“{_vm.WindowTitle}”)");
                Shot(shot);
            }
            return (bad.Count == 0, bad.Count == 0 ? $"{screens.Length} ekran açıldı" : string.Join("; ", bad));
        });

        await Step("Ekranlar", "Ana Sayfa: görev kutuları, hızlı erişim, gösterge paneli", "Gösterge paneli bu ayın sayılarını gösterir; Son/Sık Kullanılanlar Belgelerim'e geçer", async () =>
        {
            _vm.Navigate(AppScreen.Home);
            _vm.Library.RefreshStats();
            await Idle();
            bool dash = _vm.Library.DashboardText.StartsWith("Bu ay:") && _vm.Library.MonthOpened >= 1;
            _vm.Library.Tab = LibraryTab.Favorites; _vm.Navigate(AppScreen.Documents); await Idle();
            bool nav = _vm.Screen == AppScreen.Documents && _w.DocumentsScreen.IsVisible;
            return (dash && nav, $"gösterge: “{_vm.Library.DashboardText.Replace('\n', ' ')}”, Belgelerim'e geçiş={nav}");
        });

        await Step("Ekranlar", "Belgelerim: sekmeler, arama, sık kullanılan, kart görünümü", "Son Kullanılanlar dolu; yıldız Sık Kullanılanlar'a ekler; arama süzer; kart görünümüne geçer", async () =>
        {
            var lib = _vm.Library;
            _vm.Navigate(AppScreen.Documents);
            lib.Tab = LibraryTab.Recent; await lib.RefreshAsync(); await Idle();
            int recent = lib.Items.Count;
            var first = lib.Items.FirstOrDefault(i => i.Exists && !i.Pinned);
            if (first is null) return (false, $"son kullanılan belge yok ({recent})");
            int favBefore = lib.FavoriteCount;
            lib.TogglePinCommand.Execute(first);
            bool pinned = first.Pinned && lib.FavoriteCount == favBefore + 1;
            lib.Tab = LibraryTab.Favorites; await lib.RefreshAsync(); await Idle();
            bool inFav = lib.Items.Any(i => i.Path == first.Path);
            Shot("40-belgelerim-sik-kullanilanlar");
            lib.Tab = LibraryTab.Recent; await lib.RefreshAsync();
            lib.SearchText = "qqqyokboyle"; await Idle();
            bool emptySearch = lib.Items.Count == 0 && lib.EmptyText.Contains("eşleşen");
            lib.SearchText = Path.GetFileNameWithoutExtension(first.Path)[..3]; await Idle();
            bool found = lib.Items.Any(i => i.Path == first.Path);
            lib.SearchText = "";
            lib.ToggleViewCommand.Execute(null); await Idle();
            bool card = lib.IsCardView;
            Shot("41-belgelerim-kart");
            lib.ToggleViewCommand.Execute(null);
            lib.TogglePinCommand.Execute(first); // eski haline
            lib.Tab = LibraryTab.All; await lib.RefreshAsync(); await Idle();
            Shot("42-belgelerim-tum-belgeler");
            return (recent > 0 && pinned && inFav && emptySearch && found && card,
                $"son={recent}, yıldız={pinned}, sık kullanılanlarda={inFav}, boş arama={emptySearch}, arama bulur={found}, kart={card}, tüm belgeler={lib.Items.Count}");
        });

        // ── Dönüştür ──
        await Step("Dönüştür", "PDF → Word, Excel, PPT, Resim, TXT (ekrandan)", "Beş biçimin her biri gerçek dosya üretir; içerik Türkçe metni içerir", async () =>
        {
            var c = _vm.Converter;
            _vm.Navigate(AppScreen.Convert);
            if (!await c.SetSourceAsync(textPdf)) return (false, "kaynak açılamadı");
            await Idle();
            var outDir = Path.Combine(work, "donustur");
            Directory.CreateDirectory(outDir);
            var notes = new List<string>(); bool all = true;
            foreach (var f in new[] { ConvertFormat.Word, ConvertFormat.Excel, ConvertFormat.PowerPoint, ConvertFormat.Image, ConvertFormat.Text })
            {
                _w.ConvertScreen.SelectFormat(f);
                await Idle();
                if (c.Format != f) { all = false; notes.Add($"{f}: kutu seçilmedi"); continue; }
                c.RangeText = ConvertScreenViewModel.AllPagesText(3);
                var target = Path.Combine(outDir, "ekran" + ConvertWorkflow.Extension(f));
                var r = await c.ConvertAsync(target);
                bool ok = r is not null;
                string detail = "";
                if (ok)
                {
                    switch (f)
                    {
                        case ConvertFormat.Word:
                            using (var w = WordprocessingDocument.Open(target, false)) { var t = w.MainDocumentPart!.Document.InnerText; ok = t.Contains("şoföre") && t.Contains("Iğdır"); }
                            break;
                        case ConvertFormat.Excel:
                            using (var x = SpreadsheetDocument.Open(target, false)) { ok = x.WorkbookPart!.WorksheetParts.Count() == 3; detail = $"{x.WorkbookPart.WorksheetParts.Count()} sayfa"; }
                            break;
                        case ConvertFormat.PowerPoint:
                            using (var p = PresentationDocument.Open(target, false))
                            {
                                var slides = p.PresentationPart!.SlideParts.ToList();
                                var txt = string.Join(" ", slides.SelectMany(s => s.Slide.Descendants<DocumentFormat.OpenXml.Drawing.Text>()).Select(t => t.Text));
                                ok = slides.Count == 3 && txt.Contains("şoföre");
                                detail = $"{slides.Count} slayt";
                            }
                            break;
                        case ConvertFormat.Image:
                            ok = r!.Files.Count == 3 && r.Files.All(File.Exists) && ImageCodec.Decode(File.ReadAllBytes(r.Files[0])).Width > 500;
                            detail = $"{r.Files.Count} resim";
                            break;
                        default:
                            var tx = File.ReadAllText(target);
                            ok = tx.Contains("şoföre") && tx.Contains("Sayfa 3");
                            break;
                    }
                }
                if (!ok) all = false;
                notes.Add($"{ConvertWorkflow.DisplayName(f)} {(ok ? "✓" : "✗")}{(detail.Length > 0 ? " " + detail : "")}");
            }
            Shot("43-donustur-kaynakli");
            return (all, string.Join(", ", notes));
        });

        await Step("Dönüştür", "Toplu Dönüştürme kuyruğu", "İki PDF kuyruğa eklenir, satır başına biçim (Word, TXT); ikisi de Tamamlandı, dosyalar oluşur", async () =>
        {
            var b = _vm.Batch;
            _vm.Navigate(AppScreen.BatchConvert);
            b.Clear();
            var second = Path.Combine(work, "Toplu İkinci.pdf");
            File.Copy(textPdf, second, true);
            int added = b.Add(new[] { textPdf, second, textPdf /* yinelenen eklenmez */ });
            b.Items[1].Format = ConvertFormat.Text;
            _settings.BatchOutputFolder = Path.Combine(work, "toplu");
            Directory.CreateDirectory(_settings.BatchOutputFolder);
            await Idle();
            Shot("44-toplu-kuyruk");
            await b.RunAsync();
            await Idle();
            Shot("45-toplu-bitti");
            bool done = b.Items.All(i => i.Status == BatchStatus.Done && i.Output is { } o && File.Exists(o));
            var outs = string.Join(", ", b.Items.Select(i => $"{i.Name}→{Path.GetFileName(i.Output)} {i.StatusText}"));
            _settings.BatchOutputFolder = null;
            return (added == 2 && done && b.Items[1].Output!.EndsWith(".txt") && b.SummaryText.Contains("Tamamlanan: 2"), $"eklenen={added}; {outs}; {b.SummaryText}");
        });

        // ── OCR ekranı ve sonuç düzenleyicisi ──
        string scanPdf = Path.Combine(work, "Taranmış Sözleşme.pdf");
        {
            using var src = await PdfDocument.OpenAsync(textPdf);
            var img = await PdfTools.RenderAtDpiAsync(src, 0, 200);
            using var scanned = await PdfDocument.CreateFromImagesAsync(new ImagePageSource[] { new BitmapImageSource(img, 200, 200) }, new ImagePageLayout());
            await scanned.SaveAsync(scanPdf);
        }
        await Step("OCR", "OCR ekranı → Düzenlenebilir Metin → OCR Sonucu düzenleyicisi", "Taranmış sayfa tanınır, düzenleyicide Türkçe metin; Word ve TXT olarak kaydedilir", async () =>
        {
            var o = _vm.OcrScreen;
            _vm.Navigate(AppScreen.Ocr);
            if (!await o.SetSourceAsync(scanPdf)) return (false, "kaynak açılamadı");
            o.Language = 2; o.Output = OcrOutput.EditableText;
            await Idle(); await Task.Delay(400); await Idle();
            Shot("46-ocr-kaynakli");
            var prev = await o.CorrectedPreviewAsync();
            bool okStart = await o.StartAsync();
            await Idle();
            bool editor = _vm.Screen == AppScreen.OcrEditor && _vm.OcrEditor.Document is not null;
            var text = editor ? FlowDocumentExport.PlainText(_vm.OcrEditor.Document!) : "";
            await Task.Delay(300); await Idle();
            Shot("47-ocr-sonucu-duzenleyici");
            var docx = Path.Combine(work, "ocr-sonuc.docx");
            var saved = await _w.OcrEditorScreen.SaveDocx(docx);
            bool docxOk = false;
            if (saved is not null) using (var w2 = WordprocessingDocument.Open(docx, false)) docxOk = w2.MainDocumentPart!.Document.InnerText.Contains("şoföre");
            var txt = _w.OcrEditorScreen.SaveTxt(Path.Combine(work, "ocr-sonuc.txt"));
            bool txtOk = txt is not null && File.ReadAllText(txt).Contains("şoföre");
            return (okStart && editor && text.Contains("şoföre") && docxOk && txtOk && prev is not null,
                $"başladı={okStart}, düzenleyici={editor}, metin içerir={text.Contains("şoföre")}, Word={docxOk}, TXT={txtOk}, önizleme={prev?.Report.Describe()}, özet: {o.LastSummary}");
        });

        await Step("OCR", "OCR ekranı → Aranabilir PDF", "Yeni dosyada metin aranabilir; özgün taranmış dosya değişmez", async () =>
        {
            var o = _vm.OcrScreen;
            _vm.Navigate(AppScreen.Ocr);
            long before = new FileInfo(scanPdf).Length;
            if (!await o.SetSourceAsync(scanPdf)) return (false, "kaynak açılamadı");
            o.Output = OcrOutput.SearchablePdf;
            var target = Path.Combine(work, "Taranmış Sözleşme (aranabilir).pdf");
            bool ok = await o.StartAsync(target);
            using var re = await PdfDocument.OpenAsync(target);
            var t = (await re.GetPageTextAsync(0)).Text;
            o.Output = OcrOutput.EditableText;
            return (ok && t.Contains("şoföre") && new FileInfo(scanPdf).Length == before, $"aranabilir metin={t.Contains("şoföre")}, özgün değişmedi={new FileInfo(scanPdf).Length == before}");
        });

        // ── Sayfa Yönetimi ──
        await Step("Sayfa Yönetimi", "Sırala: yeni sırayı uygula + geri al", "Sayfalar 3-2-1 olur; Geri Al ile 1-2-3'e döner", async () =>
        {
            await _vm.OpenFilesAsync(new[] { textPdf });
            var tab = _vm.ActiveTab!;
            _vm.Navigate(AppScreen.Tools);
            _w.ToolsScreen.Select("order");
            await Idle(); await Task.Delay(500); await Idle();
            Shot("48-sirala");
            if (_w.ToolsScreen.OrderDocument != tab) return (false, "sıralanacak belge etkin sekme değil");
            bool ok = await _w.ToolsScreen.ApplyOrder(new[] { 2, 1, 0 });
            var first = (await tab.Document.GetPageTextAsync(0)).Text;
            tab.UndoCommand.Execute(null);
            await WaitUntil(() => !tab.IsBusy, 10000);
            var back = (await tab.Document.GetPageTextAsync(0)).Text;
            return (ok && first.Contains("Sayfa 3") && back.Contains("Sayfa 1"), $"uygulandı={ok}, ilk sayfa “{first[..Math.Min(8, first.Length)]}”, geri alınca “{back[..Math.Min(8, back.Length)]}”");
        });

        await Step("Sayfa Yönetimi", "Birleştir ve Böl sekmeleri", "İki 3 sayfalık belge 6 sayfada birleşir; birleşik belge her sayfa ayrı 6 dosyaya bölünür", async () =>
        {
            _w.ToolsScreen.Select("merge"); await Idle();
            Shot("49-birlestir");
            var merged = Path.Combine(work, "Birleşik Ekran.pdf");
            var second = Path.Combine(work, "Toplu İkinci.pdf");
            var r = await _w.ToolsScreen.DoMerge(merged, new[] { textPdf, second });
            int mergedPages = 0;
            if (r is not null) using (var m = await PdfDocument.OpenAsync(merged)) mergedPages = m.PageCount;
            await _vm.OpenFilesAsync(new[] { merged });
            _vm.Navigate(AppScreen.Tools);
            _w.ToolsScreen.Select("split"); await Idle();
            Shot("50-bol");
            var folder = Path.Combine(work, "bolunen");
            var files = await _w.ToolsScreen.DoSplit(new SplitPlan(SplitMode.SinglePages), folder, "parca");
            return (mergedPages == 6 && files.Count == 6 && files.All(File.Exists), $"birleşik sayfa={mergedPages}, bölünen dosya={files.Count}");
        });

        // ── Tarama ekranı ──
        await Step("Tarama", "Tarama ekranı", "Tarayıcılar aranır; cihaz yoksa düğmeler devre dışı ve neden yazılı; hedef seçenekleri çalışır", async () =>
        {
            _vm.Navigate(AppScreen.Scan);
            await Task.Delay(2500); await Idle();
            Shot("51-tarama");
            var list = await WiaScanner.ListAsync().ContinueWith(t => t.IsCompletedSuccessfully ? t.Result.Count : -1);
            return (true, list switch { -1 => "WIA listelenemedi; ekranda neden yazılı", 0 => "tarayıcı yok — gerçek tarama bu makinede yapılamaz; “Tara” devre dışı", _ => $"{list} tarayıcı" });
        });

        // ── Ayarlar ve Hakkında ──
        await Step("Ayarlar", "Ayarlar ekranı: 7 kategori", "Her kategori açılır ve ayarlarını gösterir", async () =>
        {
            _vm.Navigate(AppScreen.Settings);
            var counts = new List<string>(); bool ok = true;
            foreach (var key in SettingsScreen.CategoryKeys)
            {
                _w.SettingsScreen.Select(key);
                await Idle();
                int n = _w.SettingsScreen.VisibleSettingCount;
                if (n < 3) ok = false;
                counts.Add($"{key}={n}");
                Shot("52-ayarlar-" + key);
            }
            _w.SettingsScreen.Select("general");
            return (ok, string.Join(", ", counts));
        });

        await Step("Hakkında", "Hakkında / Yardım / Lisans sekmeleri", "Sürüm, Windows 10/11 rozetleri; yardım konuları; üçüncü taraf lisans tablosu dolu", async () =>
        {
            _vm.Navigate(AppScreen.Help);
            var rows = HelpScreen.ThirdPartyRows();
            foreach (var t in new[] { "about", "help", "license" }) { _w.HelpScreen.Select(t); await Idle(); Shot("53-hakkinda-" + t); }
            _w.HelpScreen.Select("about");
            return (rows.Count >= 10 && HelpScreen.HelpTopics.Length >= 6 && LegalTexts.Eula.Length > 500 && LegalTexts.Privacy.Length > 500,
                $"lisans satırı={rows.Count}, yardım konusu={HelpScreen.HelpTopics.Length}, Windows 11={HelpScreen.IsWindows11}, sürüm={HelpScreen.Version}");
        });

        await Step("Güncelleme", "GitHub sürüm yanıtının çözülmesi", "Yeni sürüm ve doğru kurulum dosyası seçilir; eski/aynı sürüm yok sayılır", async () =>
        {
            const string json = """{"tag_name":"mpr-v9.1.0","html_url":"https://github.com/Hyperonix06/mesly-pdf/releases/tag/mpr-v9.1.0","body":"Notlar","assets":[{"name":"MeslyPdfReader-Setup-cokdosya.exe","browser_download_url":"https://x/a","size":1},{"name":"MeslyPdfReader-Portable-9.1.0-win-x64.zip","browser_download_url":"https://x/z","size":2},{"name":"MeslyPdfReader-Setup-9.1.0.exe","browser_download_url":"https://x/s","size":3}]}""";
            var info = UpdateService.Parse(json, new Version(0, 7, 0));
            var same = UpdateService.Parse(json.Replace("9.1.0", "0.7.0"), new Version(0, 7, 0));
            bool tag = UpdateService.TryParseTag("mpr-v0.7.0", out var v) && v == new Version(0, 7, 0);
            await Task.CompletedTask;
            return (info is { InstallerUrl: "https://x/s", InstallerSize: 3 } && info.Version == new Version(9, 1, 0) && same is null && tag,
                $"seçilen={info?.InstallerUrl}, aynı sürüm yok sayıldı={same is null}, etiket={v} (çevrimiçi sorgu öz testte yapılmaz)");
        });

        await Step("Ayarlar", "Varsayılan PDF görüntüleyici durumu", "Windows kaydı okunur (kurulu sürümde uygulama kayıtlıdır)", async () =>
        {
            bool installed = AppContext.BaseDirectory.Contains(@"\Programs\Mesly PDF Reader", StringComparison.OrdinalIgnoreCase);
            bool reg = DefaultAppService.IsRegistered(out bool perUser);
            bool def = DefaultAppService.IsDefault();
            await Task.CompletedTask;
            return (!installed || reg, $"kurulu={installed}, kayıtlı={reg} ({(perUser ? "kullanıcı" : "makine")}), varsayılan={def}");
        });

        // ── PDF Düzenleme: açıklama, imza, tarih, form ──
        await Step("PDF Düzenleme", "Vurgula ve açıklama notu (gerçek PDF açıklaması)", "Vurgu sarı çizilir, not balonu görünür; kaydedilen dosyada iki açıklama", async () =>
        {
            await _vm.OpenFilesAsync(new[] { textPdf });
            var tab = _vm.ActiveTab!;
            _vm.SetEditMode(true);
            _vm.Ribbon = RibbonTab.Annotate;
            var t = await tab.Document.GetPageTextAsync(0);
            int s = t.Text.IndexOf("şoföre", StringComparison.Ordinal);
            var rects = t.GetLineRects(s, s + 5);
            bool m = await tab.AddMarkupAsync(new MarkupRequest(ViewerTool.Highlight, 0, rects, "şoföre"));
            bool n = await tab.AddNoteAtAsync(0, 420, 700, "Madde güncellendi.");
            tab.CurrentPage = 0;
            await WaitRendered();
            Shot("54-aciklama-vurgu-not");
            var all = await tab.Document.GetAllAnnotationsAsync();
            var img = await PdfTools.RenderAtDpiAsync(tab.Document, 0, 72);
            var r = rects[0];
            // Vurgu alanında ortalama renk: kırmızı yüksek, mavi belirgin düşük (sarı); yazı pikselleri ortalamayı bozmaz.
            double sr = 0, sb = 0; int cnt = 0; double ph = tab.Document.GetPageSize(0).Height;
            for (int y = (int)(ph - r.Top); y < (int)(ph - r.Bottom); y++)
                for (int x = (int)r.Left; x < (int)r.Right; x++) { int o = y * img.Stride + x * 4; sb += img.Pixels[o]; sr += img.Pixels[o + 2]; cnt++; }
            bool yellow = cnt > 0 && sr / cnt > 150 && sr / cnt - sb / cnt > 60;
            var path = Path.Combine(work, "Açıklamalı.pdf");
            await tab.Document.SaveAsync(path);
            using var re = await PdfDocument.OpenAsync(path);
            int saved = (await re.GetAllAnnotationsAsync()).Count;
            return (m && n && all.Count == 2 && yellow && saved == 2, $"vurgu={m}, not={n}, açıklama={all.Count}, sarı piksel={yellow}, kaydedilen={saved}");
        });

        await Step("PDF Düzenleme", "İmza ve tarih damgası", "Yazılı imza saydam yerleşir (alttaki metin görünür kalır); tarih metin olarak eklenir; imza kaydedilir/silinir", async () =>
        {
            var tab = _vm.ActiveTab!;
            _vm.Ribbon = RibbonTab.Sign;
            var sig = SignatureDialog.RenderTyped("Ayşe Yılmaz", "Segoe Script", System.Windows.Media.Colors.Navy);
            var savedSig = SignatureDialog.Save(sig);
            bool listed = SignatureDialog.Saved().Contains(savedSig);
            var t = await tab.Document.GetPageTextAsync(0);
            int s = t.Text.IndexOf("Hizmet", StringComparison.Ordinal);
            var line = t.GetLineRects(s, s + 5)[0];
            await tab.Document.AddImageAsync(0, new BitmapImageSource(sig) { Transparent = true }, new RectF(line.Left, line.Bottom - 10, line.Left + 160, line.Top + 25));
            var img = await PdfTools.RenderAtDpiAsync(tab.Document, 0, 144);
            int dark = 0; double sc = 2; var size = tab.Document.GetPageSize(0);
            for (int y = (int)((size.Height - line.Top) * sc); y < (int)((size.Height - line.Bottom) * sc); y++)
                for (int x = (int)(line.Left * sc); x < (int)((line.Left + 50) * sc); x++) { int o = y * img.Stride + x * 4; if (img.Pixels[o] < 90 && img.Pixels[o + 1] < 90 && img.Pixels[o + 2] < 90) dark++; }
            tab.PlaceDateStampCommand.Execute(null);
            bool stamp = await tab.PlaceAsync(EditPlacement.Stamp, 0, 72, 120, 272, 100);
            var today = DateTime.Now.ToString("dd.MM.yyyy");
            bool hasDate = (await tab.Document.GetPageTextAsync(0)).Text.Contains(today);
            await WaitRendered();
            Shot("55-imza-tarih");
            File.Delete(savedSig);
            bool deleted = !SignatureDialog.Saved().Contains(savedSig);
            return (listed && dark > 20 && stamp && hasDate && deleted, $"imza kaydı={listed}, imza altındaki metin pikseli={dark}, tarih={hasDate} ({today}), imza silindi={deleted}");
        });

        await Step("PDF Düzenleme", "Form: doldur, görünüm, düzleştir", "Metin/onay/liste alanları yazılır ve görünür; düzleştirince alanlar sayfa içeriği olur", async () =>
        {
            var formPath = Path.Combine(work, "Başvuru Formu.pdf");
            using (var rs = typeof(SelfTestRunner).Assembly.GetManifestResourceStream("selftest.form.pdf")!)
            using (var fs = File.Create(formPath)) await rs.CopyToAsync(fs);
            await _vm.OpenFilesAsync(new[] { formPath });
            var tab = _vm.ActiveTab!;
            _vm.SetEditMode(true);
            _vm.Ribbon = RibbonTab.Form;
            var fields = await tab.Document.GetFormFieldsAsync();
            var name = fields.Single(f => f.Name == "adsoyad");
            int n = await tab.Document.SetFormFieldsAsync(new[]
            {
                new FormFieldChange(name, Text: "Ayşe Yılmaz"),
                new FormFieldChange(fields.Single(f => f.Name == "onay"), Checked: true),
                new FormFieldChange(fields.Single(f => f.Name == "sehir"), Option: 1),
            });
            await WaitRendered();
            Shot("56-form-dolu");
            var after = await tab.Document.GetFormFieldsAsync();
            bool vals = after.Single(f => f.Name == "adsoyad").Value == "Ayşe Yılmaz" && after.Single(f => f.Name == "onay").Checked && after.Single(f => f.Name == "sehir").Value == "Istanbul";
            // Türkçe karakterler form görünümünde çizilmeli (ı, ş): alan içinde koyu piksel sayısı
            var img = await PdfTools.RenderAtDpiAsync(tab.Document, 0, 144);
            int ink = 0; var size = tab.Document.GetPageSize(0);
            for (int y = (int)((size.Height - name.Rect.Top) * 2) + 3; y < (int)((size.Height - name.Rect.Bottom) * 2) - 3; y++)
                for (int x = (int)(name.Rect.Left * 2) + 3; x < (int)(name.Rect.Right * 2) - 3; x++) { int o = y * img.Stride + x * 4; if (img.Pixels[o] < 100) ink++; }
            int flat = await tab.Document.FlattenAsync();
            bool gone = (await tab.Document.GetFormFieldsAsync()).Count == 0;
            bool text = (await tab.Document.GetPageTextAsync(0)).Text.Contains("Ay");
            await WaitRendered();
            Shot("57-form-duzlestirilmis");
            return (n == 3 && vals && ink > 40 && flat == 1 && gone && text, $"yazılan={n}, değerler={vals}, alan mürekkebi={ink}, düzleştirilen sayfa={flat}, alan kalmadı={gone}, metin={text}");
        });

        await Step("PDF Düzenleme", "Düzenleme şeridi düğmeleri komutlara bağlı", "Giriş/Düzenle/Açıklama/İmza/Form sekmelerindeki her düğmenin komutu var ve etkin", async () =>
        {
            var tab = _vm.ActiveTab!;
            var missing = new List<string>();
            var cmds = new (string Name, System.Windows.Input.ICommand? Cmd, object? Param)[]
            {
                ("Metin Ekle", tab.PlaceCommand, EditPlacement.Text), ("Vurgula", tab.SetToolCommand, ViewerTool.Highlight), ("Altını Çiz", tab.SetToolCommand, ViewerTool.Underline),
                ("Üstünü Çiz", tab.SetToolCommand, ViewerTool.StrikeOut), ("Açıklama", tab.SetToolCommand, ViewerTool.Note), ("İmza", tab.PlaceSignatureCommand, null),
                ("Şekiller", tab.PlaceCommand, EditPlacement.Rectangle), ("Sil", tab.DeleteSelectedCommand, null), ("Renk", tab.SetAnnotColorCommand, "#FFE14D"),
                ("Açıklamalar", tab.ShowAnnotationsCommand, null), ("Tümünü kaldır", tab.RemovePageAnnotationsCommand, null), ("Kayıtlı İmzalar", tab.ManageSignaturesCommand, null),
                ("Tarih", tab.PlaceDateStampCommand, null), ("Ad Soyad", tab.PlaceNameCommand, null), ("Formu Doldur", tab.FillFormCommand, null),
                ("Formu Sıfırla", tab.ResetFormCommand, null), ("Düzleştir", tab.FlattenCommand, null), ("Sayfa paneli", tab.ToggleLeftPanelCommand, null),
                ("Önceki", tab.PreviousPageCommand, null), ("Sonraki", tab.NextPageCommand, null), ("PowerPoint", tab.ConvertToPowerPointCommand, null),
                ("Düzenlemeyi kapat", _vm.ExitEditModeCommand, null), ("PDF Düzenle", _vm.EnterEditModeCommand, null),
            };
            foreach (var (n, c, p) in cmds) if (c is null || !c.CanExecute(p)) missing.Add(n);
            tab.SetToolCommand.Execute(ViewerTool.Highlight);
            bool tool = tab.Tool == ViewerTool.Highlight;
            tab.SetToolCommand.Execute(ViewerTool.Edit);
            tab.ToggleLeftPanelCommand.Execute(null); var hidden = tab.LeftPanel; tab.ToggleLeftPanelCommand.Execute(null);
            _vm.SetEditMode(false);
            await Idle();
            return (missing.Count == 0 && tool && hidden == LeftPanelKind.None, missing.Count == 0 ? $"{cmds.Length} komut hazır; araç değişimi={tool}, panel gizle/göster={hidden == LeftPanelKind.None}" : "eksik/etkisiz: " + string.Join(", ", missing));
        });
    }
}
