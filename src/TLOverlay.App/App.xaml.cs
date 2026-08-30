using System.IO;
using System.Windows;
using System.Windows.Threading;
using Serilog;

namespace TLOverlay.App;

public partial class App : Application
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TLOverlay");

    protected override void OnStartup(StartupEventArgs e)
    {
        Directory.CreateDirectory(DataDirectory);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                Path.Combine(DataDirectory, "logs", "tloverlay-.log"),
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
