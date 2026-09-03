using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Serilog;
using TLOverlay.App.Interop;
using TLOverlay.App.Services;
using TLOverlay.App.Views;

namespace TLOverlay.App;

public partial class App : Application
{
    public static string DataDirectory => AppPaths.DataDirectory;

    /// <summary>The running version, as the updater compares it against tags.</summary>
    public static Version Version { get; } = Core.Update.AppVersion.Parse(
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString());

    protected override void OnStartup(StartupEventArgs e)
    {
        // Answered before anything else is set up, because the thing asking is
        // the updater smoke-testing a freshly downloaded build: it needs to know
        // this executable starts and is the version it claims, and it should not
        // have to wait for logging, windows or a settings file to find out.
        if (e.Args.Any(static arg => string.Equals(arg, "--version", StringComparison.OrdinalIgnoreCase)))
        {
            Console.Out.WriteLine(Version.ToString());
            Console.Out.Flush();
            Shutdown(0);
            return;
        }

        // Installed before anything that could fail, including the logger itself.
        // A crash while the app is still setting itself up is precisely the one
        // nobody can diagnose afterwards.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Report(args.ExceptionObject as Exception, "Unhandled exception.");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Report(args.Exception, "Unobserved task exception.");
            args.SetObserved();
        };

        try
        {
            Start(e);
        }
        catch (Exception ex)
        {
            // Nothing is running yet, so there is no overlay worth keeping alive.
            // Staying up would leave an invisible TLOverlay in Task Manager with
            // no window to close - which is what 0.3.0 did.
            Report(ex, "TLOverlay failed to start.");
            ShowCrashDialog(ex);
            Shutdown(1);
        }
    }

    private void Start(StartupEventArgs e)
    {
        AppPaths.EnsureCreated();

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                Path.Combine(AppPaths.LogsDirectory, "tloverlay-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7)
            .CreateLogger();

        Log.Information("TLOverlay {Version} starting.", Version);

        // Unlike --version, this one builds every window. It is the check that
        // would have caught 0.3.0, where the panel threw while its XAML was
        // still being parsed and the app could not open at all.
        if (e.Args.Any(static arg => string.Equals(arg, "--self-test", StringComparison.OrdinalIgnoreCase)))
        {
            int code = SelfTest();

            Console.Out.Flush();
            Console.Error.Flush();

            // Environment.Exit rather than Shutdown: the windows above were built
            // and never shown, and getting WPF to tear that down cleanly is not
            // what this check is for. Whether constructing them threw is the
            // whole answer, and it is already known by this line.
            Environment.Exit(code);
        }

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

        // If an update was interrupted between its two renames, this is what puts
        // the program back.
        UpdateService.CleanUpAfterUpdate();

        var settings = SettingsStore.Load(DataDirectory);

        if (!TranslatorFactory.IsReadyToTranslate(settings.Translator, settings.InstallRoot))
        {
            new SetupWindow(settings).ShowDialog();
        }

        var panel = new ControlPanelWindow();
        MainWindow = panel;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        panel.Show();
    }

    /// <summary>
    /// Builds every window without showing one, and reports whether that worked.
    /// Run against the published executable in CI, so a build that cannot open
    /// is never uploaded to a release.
    /// </summary>
    private static int SelfTest()
    {
        // Announced one step at a time, because the failure this guards against
        // can also be a hang rather than a throw - and then the last line printed
        // is the only thing that says where.
        static void Step(string what)
        {
            Console.Out.WriteLine($"self-test: {what}");
            Console.Out.Flush();
        }

        try
        {
            Step("loading settings");
            var settings = SettingsStore.Load(DataDirectory);

            Step("hotkey service");
            using var hotKeys = new GlobalHotKeyService();

            Step("update service");
            var updates = new UpdateService(settings);

            Step("SetupWindow");
            _ = new SetupWindow(settings);

            Step("SettingsWindow");
            _ = new SettingsWindow(settings, hotKeys, updates);

            Step("ControlPanelWindow");
            _ = new ControlPanelWindow();

            Step("every window was built");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Self-test failed.");
            Console.Error.WriteLine("self-test: FAILED");
            Console.Error.WriteLine(ex.ToString());
            Console.Error.Flush();
            return 1;
        }
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Report(e.Exception, "Unhandled exception on the UI thread.");
        ShowCrashDialog(e.Exception);

        // Deliberately kept alive: this fires for failures during play, and
        // taking the overlay down mid-game helps nobody. A failure while the app
        // is still starting is handled in OnStartup instead, which does exit.
        e.Handled = true;
    }

    private static void Report(Exception? exception, string message)
    {
        Log.Error(exception, "{Message}", message);

        // Serilog may not be configured yet, or may be the thing that failed, so
        // the crash file is written independently of it.
        if (exception is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(AppPaths.LogsDirectory);
            File.WriteAllText(
                CrashFilePath(),
                $"TLOverlay {Version} - {DateTimeOffset.Now:u}{Environment.NewLine}" +
                $"{message}{Environment.NewLine}{Environment.NewLine}{exception}{Environment.NewLine}");
        }
        catch (Exception writeFailure) when (writeFailure is IOException or UnauthorizedAccessException)
        {
            // Nothing left to report it to.
        }
    }

    /// <summary>
    /// Timestamped rather than fixed, so a second failure - the one that often
    /// happens while the first is being handled - does not overwrite the report
    /// that actually explains what went wrong.
    /// </summary>
    private static string CrashFilePath() => Path.Combine(
        AppPaths.LogsDirectory,
        $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.txt");

    private static void ShowCrashDialog(Exception exception)
    {
        // The type and the log location, not just the message: "Object reference
        // not set to an instance of an object." on its own tells a player nothing
        // and tells whoever has to fix it even less.
        MessageBox.Show(
            $"{exception.GetType().Name}: {exception.Message}" +
            Environment.NewLine + Environment.NewLine +
            "รายละเอียดทั้งหมดถูกบันทึกไว้ที่" + Environment.NewLine +
            AppPaths.LogsDirectory,
            "TLOverlay",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("TLOverlay exiting.");
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
