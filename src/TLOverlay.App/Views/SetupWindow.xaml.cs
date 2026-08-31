using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Serilog;
using TLOverlay.App.Services;
using TLOverlay.Core.Setup;

namespace TLOverlay.App.Views;

/// <summary>
/// First-run setup: gets llama-server.exe and a GGUF model onto the machine
/// without the player opening a terminal.
///
/// Every row offers both a download and a Browse button on purpose. Downloading
/// is the happy path, but a blocked network, a corporate proxy, or a file
/// already copied from another machine all have to end somewhere other than a
/// dead end.
/// </summary>
public partial class SetupWindow : Window
{
    private readonly AppSettings _settings;
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly ModelDownloader _downloader;

    private CancellationTokenSource? _download;
    private bool _busy;

    public SetupWindow(AppSettings settings)
    {
        InitializeComponent();

        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _downloader = new ModelDownloader(_http);

        foreach (var entry in ModelCatalog.Entries)
        {
            ModelCombo.Items.Add(entry.Summary);
        }

        // Always last, and always present: a catalog entry that has moved or been
        // withdrawn upstream must not leave the user stuck.
        ModelCombo.Items.Add("ระบุ URL เอง…");

        var current = ModelCatalog.FindById(_settings.Translator.ModelId);
        ModelCombo.SelectedIndex = current is null
            ? (string.Equals(_settings.Translator.ModelId, ModelCatalog.CustomId, StringComparison.Ordinal)
                ? ModelCatalog.Entries.Count
                : 0)
            : ModelCatalog.Entries.ToList().IndexOf(current);

        GpuOption.IsChecked = _settings.Translator.GpuLayers > 0;
        CpuOption.IsChecked = !GpuOption.IsChecked;

        Closed += (_, _) => _http.Dispose();

        RefreshState();

        // Written every time the window opens: a support question about setup is
        // almost always "which paths is it actually using".
        Log.Information(
            "Setup opened. Server target {Server}; model target {Model}; data directory {Data}.",
            ServerTarget,
            ModelTarget,
            AppPaths.DataDirectory);
    }

    private bool IsCustomSelected => ModelCombo.SelectedIndex == ModelCatalog.Entries.Count;

    /// <summary>
    /// Null when the custom option is chosen and the URL is not usable yet, which
    /// is what keeps the download button from firing at nothing.
    /// </summary>
    private ModelEntry? SelectedModel =>
        IsCustomSelected
            ? ModelCatalog.TryCreateCustom(CustomUrl.Text)
            : ModelCombo.SelectedIndex >= 0 && ModelCombo.SelectedIndex < ModelCatalog.Entries.Count
                ? ModelCatalog.Entries[ModelCombo.SelectedIndex]
                : ModelCatalog.Default;

    private LlamaBackend Backend => GpuOption.IsChecked == true ? LlamaBackend.Cuda : LlamaBackend.Cpu;

    private string ServerTarget =>
        _settings.Translator.ExecutablePath
        ?? TranslatorFactory.ResolveDefault(TranslatorFactory.DefaultExecutableRelativePath);

    private string ModelTarget =>
        _settings.Translator.ModelPath
        ?? TranslatorFactory.ResolveDefault(TranslatorFactory.DefaultModelRelativePath);

    private void RefreshState()
    {
        bool hasServer = File.Exists(ServerTarget);
        bool hasModel = File.Exists(ModelTarget);

        ServerGlyph.Text = hasServer ? "✓" : "✗";
        ServerGlyph.Foreground = Swatch(hasServer);
        ServerPath.Text = hasServer ? ServerTarget : $"ยังไม่มีไฟล์ — จะบันทึกไว้ที่ {ServerTarget}";

        ModelGlyph.Text = hasModel ? "✓" : "✗";
        ModelGlyph.Foreground = Swatch(hasModel);
        ModelPath.Text = hasModel ? ModelTarget : $"ยังไม่มีไฟล์ — จะบันทึกไว้ที่ {ModelTarget}";

        DoneButton.IsEnabled = hasServer && hasModel && !_busy;
        ServerDownloadButton.IsEnabled = !_busy;
        ModelDownloadButton.IsEnabled = !_busy && SelectedModel is not null;

        LogHint.Text = $"ถ้าดาวน์โหลดไม่สำเร็จ ดูรายละเอียดได้ที่ {AppPaths.LogsDirectory}";

        OfflineUrls.Text = string.Join(
            Environment.NewLine + Environment.NewLine,
            (SelectedModel?.Url.ToString() ?? "(ยังไม่ได้ระบุ URL)") + Environment.NewLine + "  →  " + ModelTarget,
            "https://github.com/ggml-org/llama.cpp/releases/latest" + Environment.NewLine + "  →  " + ServerTarget);
    }

    /// <summary>Green for present, amber for missing, both readable on the light surface.</summary>
    private System.Windows.Media.Brush Swatch(bool satisfied) =>
        (System.Windows.Media.Brush)FindResource(satisfied ? "SuccessBrush" : "WarningBrush");

    private void OnModelSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        CustomUrl.Visibility = IsCustomSelected ? Visibility.Visible : Visibility.Collapsed;

        var entry = SelectedModel;

        // Licence belongs where the choice is made. Finding out a model is
        // non-commercial after two gigabytes have downloaded is too late.
        LicenseText.Text = entry is null
            ? "วาง URL ของไฟล์ .gguf แล้วกดดาวน์โหลด"
            : entry.CommercialUseAllowed
                ? $"สัญญาอนุญาต: {entry.License}"
                : $"สัญญาอนุญาต: {entry.License} — ใช้เชิงพาณิชย์ไม่ได้";

        if (IsLoaded)
        {
            RefreshState();
        }
    }

    private void OnCustomUrlChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded)
        {
            RefreshState();
        }
    }

    private async void OnDownloadServerClick(object sender, RoutedEventArgs e)
    {
        await RunAsync("llama-server download", async (progress, token) =>
        {
            Status("กำลังหาไฟล์ล่าสุดของ llama.cpp…");

            var asset = await new LlamaReleaseResolver(_http).ResolveLatestAsync(Backend, token);

            string runtimeDirectory = Path.GetDirectoryName(ServerTarget)!;
            string archivePath = Path.Combine(runtimeDirectory, asset.Name);

            Status($"กำลังดาวน์โหลด {asset.Name}");
            await _downloader.DownloadAsync(asset.DownloadUrl, archivePath, FileSignature.Zip, progress, token);

            Status("กำลังแตกไฟล์…");
            string server = RuntimeInstaller.InstallFromArchive(archivePath, runtimeDirectory);

            File.Delete(archivePath);

            _settings.Translator.ExecutablePath = server;
            Status("เซิร์ฟเวอร์แปลภาษาพร้อมแล้ว");
        });
    }

    private async void OnDownloadModelClick(object sender, RoutedEventArgs e)
    {
        var entry = SelectedModel;

        if (entry is null)
        {
            Status("วาง URL ของไฟล์ .gguf ให้ครบก่อน (ต้องขึ้นต้นด้วย https://)");
            return;
        }

        await RunAsync($"model download ({entry.Id})", async (progress, token) =>
        {
            Status($"กำลังดาวน์โหลด {entry.DisplayName}");

            await _downloader.DownloadAsync(entry.Url, ModelTarget, FileSignature.Gguf, progress, token);

            _settings.Translator.ModelPath = ModelTarget;
            _settings.Translator.ModelId = entry.Id;
            Status("โมเดลพร้อมแล้ว");
        });
    }

    /// <summary>
    /// Runs one download, wiring up progress, cancellation and error reporting.
    /// Shared so the two rows cannot drift apart in how they behave.
    ///
    /// Every exit path has to say something. This is the first screen a user ever
    /// sees, and a button that appears to do nothing is the worst outcome it can
    /// produce - worse than an ugly error, because there is nothing to report.
    /// </summary>
    private async Task RunAsync(string what, Func<IProgress<DownloadProgress>, CancellationToken, Task> work)
    {
        if (_busy)
        {
            Status("กำลังดาวน์โหลดอยู่แล้ว — รอให้เสร็จ หรือกดยกเลิกก่อน");
            return;
        }

        // Logged before anything can throw, so the log file always answers the
        // first question: did the click even arrive?
        Log.Information("Setup: starting {What}.", what);

        _busy = true;

        var cancellation = new CancellationTokenSource();
        _download = cancellation;

        ProgressPanel.Visibility = Visibility.Visible;
        CancelButton.Visibility = Visibility.Visible;
        DownloadBar.Value = 0;
        ProgressLeft.Text = string.Empty;
        ProgressRight.Text = string.Empty;
        RefreshState();

        var progress = new Progress<DownloadProgress>(Report);

        try
        {
            await work(progress, cancellation.Token);
            SaveSettings();
            Log.Information("Setup: {What} completed.", what);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Status("ยกเลิกแล้ว — ไฟล์ที่โหลดไปบางส่วนยังอยู่ กดดาวน์โหลดอีกครั้งเพื่อโหลดต่อ");
        }
        catch (ModelDownloadException ex)
        {
            Status(ex.Message);
            Log.Warning(ex, "Setup: {What} failed.", what);
        }
        catch (HttpRequestException ex)
        {
            // No network, DNS failure, a proxy in the way, a TLS problem. This
            // used to escape uncaught and surface as a bare message box, which
            // read to the user as the button doing nothing.
            Status($"เชื่อมต่ออินเทอร์เน็ตไม่ได้: {ex.Message}");
            Log.Error(ex, "Setup: {What} could not reach the network.", what);
        }
        catch (TaskCanceledException ex)
        {
            // A cancellation nobody asked for is a timeout, and saying "cancelled"
            // would send the user looking for a button they never pressed.
            Status("หมดเวลารอเซิร์ฟเวอร์ — ลองใหม่อีกครั้ง");
            Log.Error(ex, "Setup: {What} timed out.", what);
        }
        catch (UnauthorizedAccessException ex)
        {
            Status($"ไม่มีสิทธิ์เขียนไฟล์: {ex.Message}");
            Log.Error(ex, "Setup: {What} was denied write access.", what);
        }
        catch (IOException ex)
        {
            Status($"เขียนไฟล์ไม่สำเร็จ: {ex.Message}");
            Log.Error(ex, "Setup: {What} could not write to disk.", what);
        }
        catch (Exception ex)
        {
            Status($"ผิดพลาด ({ex.GetType().Name}): {ex.Message}");
            Log.Error(ex, "Setup: {What} failed unexpectedly.", what);
        }
        finally
        {
            _busy = false;
            _download = null;
            cancellation.Dispose();

            CancelButton.Visibility = Visibility.Collapsed;
            RefreshState();
        }
    }

    private void Report(DownloadProgress progress)
    {
        DownloadBar.IsIndeterminate = progress.Fraction is null;
        DownloadBar.Value = progress.Fraction ?? 0;

        string done = FormatBytes(progress.BytesCompleted);
        ProgressLeft.Text = progress.TotalBytes is { } total
            ? $"{done} / {FormatBytes(total)}"
            : done;

        string speed = $"{progress.BytesPerSecond / 1024 / 1024:F1} MB/s";
        ProgressRight.Text = progress.Remaining is { } remaining
            ? $"{speed} · {FormatDuration(remaining)}"
            : speed;
    }

    internal static string FormatBytes(long bytes)
    {
        const double Mb = 1024 * 1024;
        const double Gb = Mb * 1024;

        return bytes >= Gb
            ? $"{bytes / Gb:F2} GB"
            : $"{bytes / Mb:F0} MB";
    }

    internal static string FormatDuration(TimeSpan span)
    {
        if (span.TotalSeconds < 60)
        {
            return $"เหลือประมาณ {Math.Ceiling(span.TotalSeconds):F0} วินาที";
        }

        return $"เหลือประมาณ {(int)span.TotalMinutes} นาที {span.Seconds} วินาที";
    }

    private void OnBrowseServerClick(object sender, RoutedEventArgs e)
    {
        string? chosen = Browse("เลือก llama-server.exe", "llama-server.exe|llama-server.exe|โปรแกรม (*.exe)|*.exe");

        if (chosen is not null)
        {
            _settings.Translator.ExecutablePath = chosen;
            SaveSettings();
            Status("ใช้เซิร์ฟเวอร์ที่เลือกไว้แล้ว");
            RefreshState();
        }
    }

    private async void OnBrowseModelClick(object sender, RoutedEventArgs e)
    {
        string? chosen = Browse("เลือกไฟล์โมเดล", "โมเดล GGUF (*.gguf)|*.gguf|ทุกไฟล์ (*.*)|*.*");

        if (chosen is null)
        {
            return;
        }

        // Check the magic bytes here too. A file that is not really a model would
        // otherwise fail much later, inside llama-server, with an error the player
        // cannot act on.
        if (!await ModelDownloader.HasSignatureAsync(chosen, FileSignature.Gguf))
        {
            Status("ไฟล์นี้ไม่ใช่โมเดล GGUF — เลือกไฟล์ .gguf ที่โหลดมาครบแล้ว");
            return;
        }

        _settings.Translator.ModelPath = chosen;
        SaveSettings();
        Status("ใช้โมเดลที่เลือกไว้แล้ว");
        RefreshState();
    }

    private static string? Browse(string title, string filter)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = filter,
            CheckFileExists = true,
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private void OnCancelDownloadClick(object sender, RoutedEventArgs e) => _download?.Cancel();

    private void OnSkipClick(object sender, RoutedEventArgs e)
    {
        SaveSettings();
        DialogResult = false;
        Close();
    }

    private void OnDoneClick(object sender, RoutedEventArgs e)
    {
        _settings.Translator.GpuLayers = Backend == LlamaBackend.Cuda ? 99 : 0;
        SaveSettings();
        DialogResult = true;
        Close();
    }

    private void SaveSettings() => SettingsStore.Save(App.DataDirectory, _settings);

    private void Status(string message) => StatusText.Text = message;
}
