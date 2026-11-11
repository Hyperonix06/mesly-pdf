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

        var tr = CultureInfo.GetCultureInfo("tr-TR");
        CultureInfo.DefaultThreadCurrentCulture = tr;
        CultureInfo.DefaultThreadCurrentUICulture = tr;
        Thread.CurrentThread.CurrentCulture = tr;
        Thread.CurrentThread.CurrentUICulture = tr;
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(System.Windows.Markup.XmlLanguage.GetLanguage("tr-TR")));

        if (selfTestDir is null)
        {
            _single = new SingleInstance();
            if (!_single.TryClaim(args.Where(File.Exists).ToArray()))
            {
                Shutdown(0);
                return;
            }
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
