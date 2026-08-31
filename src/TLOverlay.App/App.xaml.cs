using System.IO;
using System.Windows;
using System.Windows.Threading;
using Serilog;
using TLOverlay.App.Services;
using TLOverlay.App.Views;

namespace TLOverlay.App;

public partial class App : Application
{
    public static string DataDirectory => AppPaths.DataDirectory;

    protected override void OnStartup(StartupEventArgs e)
    {
        AppPaths.EnsureCreated();

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                Path.Combine(AppPaths.LogsDirectory, "tloverlay-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7)
            .CreateLogger();

        Log.Information("TLOverlay starting.");

        // An unhandled exception on the UI thread would otherwise take down the
        // overlay mid-game with no trace of why.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error(args.ExceptionObject as Exception, "Unhandled exception.");

        base.OnStartup(e);

        // The setup window is shown modally before anything else exists, so the
        // app must not treat closing it as "the last window closed".
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        if (AppPaths.IsRunningFromArchive)
        {
            // Everything written here vanishes when the archiver cleans up, so
            // say so before the player spends an hour downloading a model into it.
            MessageBox.Show(
                "ดูเหมือนคุณกำลังเปิดโปรแกรมจากในไฟล์ ZIP/RAR โดยตรง\n\n" +
                "กรุณาแตกไฟล์ออกมาไว้ในโฟลเดอร์จริงก่อน แล้วค่อยเปิด TLOverlay.exe อีกครั้ง\n" +
                "ไม่อย่างนั้นไฟล์ที่โปรแกรมสร้างจะถูกลบทิ้งเมื่อปิดโปรแกรม",
                "TLOverlay",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        var settings = SettingsStore.Load(DataDirectory);

        if (!TranslatorFactory.IsModelInstalled(settings.Translator))
        {
            new SetupWindow(settings).ShowDialog();
        }

        var panel = new ControlPanelWindow();
        MainWindow = panel;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        panel.Show();
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unhandled exception on the UI thread.");
        MessageBox.Show(
            e.Exception.Message,
            "TLOverlay",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("TLOverlay exiting.");
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
