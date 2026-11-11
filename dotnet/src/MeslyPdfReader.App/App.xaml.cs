using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MeslyPdfReader.App.Dialogs;
using MeslyPdfReader.App.Services;
using MeslyPdfReader.App.ViewModels;
using MeslyPdfReader.App.Views;
using MeslyPdfReader.Data;
using MeslyPdfReader.Pdf;
using Microsoft.Extensions.DependencyInjection;

namespace MeslyPdfReader.App;

public partial class App : Application
{
    private static IServiceProvider? _services;
    public static IServiceProvider Services => _services ?? throw new InvalidOperationException("Hizmetler henüz başlatılmadı.");

    private SingleInstance? _single;

    protected override async void OnStartup(StartupEventArgs e)
    {
        var args = e.Args;
        string? selfTestDir = null;
        int st = Array.IndexOf(args, "--selftest");
        if (st >= 0)
        {
            selfTestDir = st + 1 < args.Length ? args[st + 1] : Path.Combine(Path.GetTempPath(), "mesly-selftest");
            args = args.Where((_, i) => i != st && i != st + 1).ToArray();
            AppPaths.OverrideRoot(Path.Combine(selfTestDir, "appdata"));
        }

        string? benchFile = null;
        int bi = Array.IndexOf(args, "--startup-benchmark");
        if (bi >= 0)
        {
            benchFile = bi + 1 < args.Length ? args[bi + 1] : Path.Combine(Path.GetTempPath(), "mesly-startup.txt");
            args = args.Where((_, i) => i != bi && i != bi + 1).ToArray();
        }

        var tr = CultureInfo.GetCultureInfo("tr-TR");
        CultureInfo.DefaultThreadCurrentCulture = tr;
        CultureInfo.DefaultThreadCurrentUICulture = tr;
        Thread.CurrentThread.CurrentCulture = tr;
        Thread.CurrentThread.CurrentUICulture = tr;
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(System.Windows.Markup.XmlLanguage.GetLanguage("tr-TR")));

        if (selfTestDir is null && benchFile is null)
        {
            _single = new SingleInstance();
            if (!_single.TryClaim(args.Where(File.Exists).ToArray()))
            {
                Shutdown(0);
                return;
            }
        }

        // Açılış ekranı: logo + “Powered by Mesly”. Ana pencere çizildikten sonra (en az ~1 sn) yumuşakça kapanır.
        SplashScreen? splash = null;
        var splashShown = System.Diagnostics.Stopwatch.StartNew();
        if (benchFile is null)
        {
            try
            {
                splash = new SplashScreen(typeof(App).Assembly, "Assets/splash.png");
                splash.Show(false, true);
                var s = splash;
                // Güvenlik: pencere hiç çizilemezse (açılış hatası) hata iletisinin önünde kalmasın.
                _ = Task.Delay(8000).ContinueWith(_ => Dispatcher.BeginInvoke(() => { try { s.Close(TimeSpan.Zero); } catch { } }));
            }
            catch { splash = null; }
        }

        AppPaths.Ensure();
        InstallCrashHandlers();
        Log.Info($"Mesly PDF Reader {typeof(App).Assembly.GetName().Version} başlatılıyor ({Environment.OSVersion}, .NET {Environment.Version})");

        AppDatabase db;
        try { db = new AppDatabase(AppPaths.Database); }
        catch (Exception ex)
        {
            // Bozuk veritabanı uygulamayı engellemesin: yedekle ve yeniden oluştur.
            Log.Error("Veritabanı açılamadı, yeniden oluşturuluyor", ex);
            try { File.Move(AppPaths.Database, AppPaths.Database + $".bozuk-{DateTime.Now:yyyyMMddHHmmss}", true); } catch { }
            db = new AppDatabase(AppPaths.Database);
        }
        var settings = new AppSettings(db);
        FileDialogs.Settings = settings;
        OcrWorkflow.Init();
        if (!settings.HardwareAcceleration) RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

        var sc = new ServiceCollection();
        sc.AddSingleton(db);
        sc.AddSingleton(settings);
        sc.AddSingleton<ThemeService>();
        sc.AddSingleton<RecoveryService>();
        sc.AddSingleton<MainViewModel>();
        sc.AddSingleton<MainWindow>();
        _services = sc.BuildServiceProvider();

        Services.GetRequiredService<ThemeService>().ApplyFromSettings();
        _ = Task.Run(() => AppPaths.CleanTemp(settings.TempRetentionDays));
        PdfEngine.Instance.UnhandledError += ex => Log.Error("PDF motoru", ex);

        var main = Services.GetRequiredService<MainWindow>();
        var vm = Services.GetRequiredService<MainViewModel>();
        MainWindow = main;
        if (benchFile is not null)
        {
            // Açılış ölçümü: süreç başlangıcından ilk çizilmiş ve boşta kalan pencereye kadar geçen süre.
            main.ContentRendered += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                var ms = (DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds;
                File.WriteAllText(benchFile, ((int)ms).ToString(CultureInfo.InvariantCulture));
                main.SkipExitPrompt = true;
                Shutdown(0);
            }, DispatcherPriority.ApplicationIdle);
        }
        if (splash is not null)
        {
            main.ContentRendered += async (_, _) =>
            {
                var wait = 1100 - splashShown.ElapsedMilliseconds;
                if (wait > 0) await Task.Delay((int)wait);
                try { splash.Close(TimeSpan.FromMilliseconds(350)); } catch { }
                main.Activate();
            };
        }
        main.Show();

        if (_single is not null)
        {
            _single.FilesReceived += files => Dispatcher.BeginInvoke(async () =>
            {
                if (main.WindowState == WindowState.Minimized) main.WindowState = WindowState.Normal;
                main.Activate();
                await vm.OpenFilesAsync(files);
            });
            _single.StartServer();
        }

        if (benchFile is not null) return;
        if (selfTestDir is not null)
        {
            var runner = new SelfTestRunner(main, vm, settings, db, selfTestDir);
            int code = await runner.RunAsync();
            Shutdown(code);
            return;
        }

        await vm.OfferRecoveryAsync();
        var files = args.Where(a => !a.StartsWith("--")).ToArray();
        if (files.Length > 0) await vm.OpenFilesAsync(files);
    }

    private void InstallCrashHandlers()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            Log.Error("Arayüz hatası", e.Exception);
            e.Handled = true; // uygulama kapanmasın
            try
            {
                Services.GetService<RecoveryService>()?.SaveAllAsync();
                MessageDialog.Show("Beklenmeyen bir hata oluştu", "İşlem tamamlanamadı, ancak uygulama çalışmaya devam ediyor. Açık belgelerinizin kurtarma kopyası alındı.",
                    MessageKind.Error, e.Exception.ToString());
            }
            catch { }
        };
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Error("Gözlemlenmeyen görev hatası", e.Exception); e.SetObserved(); };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Kritik hata", e.ExceptionObject as Exception);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { Services.GetService<RecoveryService>()?.Dispose(); } catch { }
        _single?.Dispose();
        Log.Info("Uygulama kapandı.");
        base.OnExit(e);
    }
}
