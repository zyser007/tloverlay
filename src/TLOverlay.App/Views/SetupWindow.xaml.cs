using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Serilog;
using TLOverlay.App.Services;
using TLOverlay.Core.Setup;
using TLOverlay.Core.Translation;

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

    /// <summary>
    /// True while the stored choice is being put on screen. Setting IsChecked
    /// raises Checked, and without this the load would immediately write back
    /// what it just read.
    /// </summary>
    private bool _loadingEngine;

    public SetupWindow(AppSettings settings)
    {
        InitializeComponent();

        WindowSizing.ClampToWorkArea(this);

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

        LoadEngineChoice();

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

    /// <summary>Folder holding runtime\ and models\.</summary>
    private string InstallRoot => _settings.InstallRoot ?? AppPaths.DataDirectory;

    private string ServerTarget =>
        _settings.Translator.ExecutablePath
        ?? TranslatorFactory.ResolveDefault(TranslatorFactory.DefaultExecutableRelativePath, InstallRoot);

    private string ModelTarget =>
        _settings.Translator.ModelPath
        ?? TranslatorFactory.ResolveDefault(TranslatorFactory.DefaultModelRelativePath, InstallRoot);

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

        bool localReady = hasServer && hasModel;

        // A cloud engine has no files to wait for, so the way out of this screen
        // must not be gated on a model the player deliberately did not download.
        DoneButton.IsEnabled = !_busy && (SelectedEngine == TranslationBackend.Local
            ? localReady
            : TranslatorFactory.IsReadyToTranslate(_settings.Translator, _settings.InstallRoot));
        ServerDownloadButton.IsEnabled = !_busy;
        ModelDownloadButton.IsEnabled = !_busy && SelectedModel is not null;

        // Nothing to delete is not the same as being unable to: the button is
        // there either way, and simply has nothing to act on.
        ServerDeleteButton.IsEnabled = !_busy && hasServer;
        ModelDeleteButton.IsEnabled = !_busy && hasModel;

        InstallPathText.Text = InstallRoot;

        long? free = InstallLocation.FreeSpaceBytes(InstallRoot);
        long needed = SelectedModel?.ApproximateBytes ?? 0;

        // Saying "not enough room" before the download beats failing at the end
        // of one, which is where this used to be discovered.
        FreeSpaceText.Text = free is null
            ? "ไม่ทราบพื้นที่ว่างของไดรฟ์นี้"
            : needed > 0 && free < needed + (512L * 1024 * 1024)
                ? $"พื้นที่ว่าง {FormatBytes(free.Value)} — อาจไม่พอสำหรับโมเดลที่เลือก ({FormatBytes(needed)})"
                : $"พื้นที่ว่าง {FormatBytes(free.Value)}";

        UpdateMemoryAdvice();

        LogHint.Text = $"ถ้าดาวน์โหลดไม่สำเร็จ ดูรายละเอียดได้ที่ {AppPaths.LogsDirectory}";

        OfflineUrls.Text = string.Join(
            Environment.NewLine + Environment.NewLine,
            (SelectedModel?.Url.ToString() ?? "(ยังไม่ได้ระบุ URL)") + Environment.NewLine + "  →  " + ModelTarget,
            "https://github.com/ggml-org/llama.cpp/releases/latest" + Environment.NewLine + "  →  " + ServerTarget);
    }

    /// <summary>
    /// Says whether the selected model fits this machine.
    ///
    /// Sizing a model is the one decision here a player cannot undo cheaply -
    /// getting it wrong costs a multi-gigabyte download and then a game that
    /// stutters - so the machine's own number goes next to the model's, at the
    /// moment of choosing.
    /// </summary>
    private void UpdateMemoryAdvice()
    {
        ModelEntry? model = SelectedModel;

        if (model is null || model.ApproximateRamBytes == 0)
        {
            ModelRamText.Text = string.Empty;
            return;
        }

        string needs = $"ใช้แรมประมาณ {MemoryReadout.Format(model.ApproximateRamBytes)} ขณะแปล";
        long machine = MemoryReadout.MachineBytes;

        if (machine == 0)
        {
            ModelRamText.Text = needs;
            return;
        }

        // The game is the other tenant, and it is usually the larger one. Four
        // gigabytes of headroom is a modest game plus Windows itself.
        const long Headroom = 4L * 1024 * 1024 * 1024;

        ModelRamText.Text = machine < model.ApproximateRamBytes + Headroom
            ? $"{needs} — เครื่องนี้มีแรม {MemoryReadout.Format(machine)} อาจไม่พอเมื่อเปิดเกมพร้อมกัน ลองเลือกรุ่นเล็กลง"
            : $"{needs} · เครื่องนี้มีแรม {MemoryReadout.Format(machine)}";
    }

    /// <summary>
    /// The engine the radio buttons are on. Named for the engine rather than
    /// "backend", which in this window already means CPU-versus-CUDA.
    /// </summary>
    private TranslationBackend SelectedEngine =>
        GoogleEngineOption.IsChecked == true ? TranslationBackend.Google
        : OpenAiEngineOption.IsChecked == true ? TranslationBackend.OpenAi
        : TranslationBackend.Local;

    /// <summary>
    /// Puts the stored choice on screen. Keys come back decrypted so the boxes
    /// can be pre-filled - a player who opens this screen to change a model
    /// should not have to paste their key again to keep it.
    /// </summary>
    private void LoadEngineChoice()
    {
        _loadingEngine = true;

        try
        {
            LoadEngineChoiceCore();
        }
        finally
        {
            _loadingEngine = false;
        }

        ApplyEngineVisibility();
    }

    private void LoadEngineChoiceCore()
    {
        LocalEngineOption.IsChecked = _settings.Translator.Backend == TranslationBackend.Local;
        GoogleEngineOption.IsChecked = _settings.Translator.Backend == TranslationBackend.Google;
        OpenAiEngineOption.IsChecked = _settings.Translator.Backend == TranslationBackend.OpenAi;

        GoogleKeyBox.Password = SecretStore.Unprotect(_settings.Translator.GoogleApiKeyProtected) ?? string.Empty;
        OpenAiKeyBox.Password = SecretStore.Unprotect(_settings.Translator.OpenAiApiKeyProtected) ?? string.Empty;
        OpenAiModelBox.Text = _settings.Translator.OpenAiModel;
        OpenAiBaseUrlBox.Text = _settings.Translator.OpenAiBaseUrl;
    }

    private void ApplyEngineVisibility()
    {
        TranslationBackend backend = SelectedEngine;

        GooglePanel.Visibility = backend == TranslationBackend.Google ? Visibility.Visible : Visibility.Collapsed;
        OpenAiPanel.Visibility = backend == TranslationBackend.OpenAi ? Visibility.Visible : Visibility.Collapsed;
        CloudTestPanel.Visibility = backend == TranslationBackend.Local ? Visibility.Collapsed : Visibility.Visible;

        // The download rows stay visible for the local engine only. Leaving them
        // on screen for a cloud engine would invite a two-gigabyte download that
        // nothing is going to use.
        LocalModelSection.Visibility = backend == TranslationBackend.Local ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnEngineChanged(object sender, RoutedEventArgs e)
    {
        // Checked fires during InitializeComponent, before the panels it shows
        // and hides have been created.
        if (_loadingEngine || GooglePanel is null || LocalModelSection is null)
        {
            return;
        }

        ApplyEngineVisibility();
        SaveEngineChoice();
    }

    private void OnSecretChanged(object sender, RoutedEventArgs e) => SaveIfReady();

    private void OnSecretTextChanged(object sender, TextChangedEventArgs e) => SaveIfReady();

    private void SaveIfReady()
    {
        if (!_loadingEngine && OpenAiModelBox is not null && OpenAiBaseUrlBox is not null)
        {
            SaveEngineChoice();
        }
    }

    private void SaveEngineChoice()
    {
        _settings.Translator.Backend = SelectedEngine;
        _settings.Translator.GoogleApiKeyProtected = SecretStore.Protect(GoogleKeyBox.Password);
        _settings.Translator.OpenAiApiKeyProtected = SecretStore.Protect(OpenAiKeyBox.Password);
        _settings.Translator.OpenAiModel = string.IsNullOrWhiteSpace(OpenAiModelBox.Text)
            ? "gpt-4o-mini"
            : OpenAiModelBox.Text.Trim();
        _settings.Translator.OpenAiBaseUrl = string.IsNullOrWhiteSpace(OpenAiBaseUrlBox.Text)
            ? "https://api.openai.com/v1/"
            : OpenAiBaseUrlBox.Text.Trim();

        SaveSettings();
        RefreshState();
    }

    /// <summary>
    /// Translates one sentence and shows the result.
    ///
    /// The single most useful control on this screen for a cloud engine: a wrong
    /// key, a wrong model name or a blocked network otherwise shows up much later
    /// as an overlay that silently never says anything, in the middle of a game.
    /// </summary>
    private async void OnTestCloudClick(object sender, RoutedEventArgs e)
    {
        const string Sample = "The gate will not open until the seal is broken.";

        SaveEngineChoice();

        TestCloudButton.IsEnabled = false;
        CloudTestResult.Text = "กำลังทดสอบ…";

        try
        {
            await using ITranslator translator = SelectedEngine == TranslationBackend.Google
                ? new GoogleTranslateTranslator(SecretStore.Unprotect(_settings.Translator.GoogleApiKeyProtected), client: _http)
                : new OpenAiTranslator(
                    new OpenAiOptions
                    {
                        ApiKey = SecretStore.Unprotect(_settings.Translator.OpenAiApiKeyProtected) ?? string.Empty,
                        Model = _settings.Translator.OpenAiModel,
                        BaseAddress = TranslatorFactory.ParseBaseAddress(_settings.Translator.OpenAiBaseUrl),
                    },
                    client: _http);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            string thai = await translator.TranslateAsync(Sample, timeout.Token);

            CloudTestResult.Text = string.IsNullOrWhiteSpace(thai)
                ? "เชื่อมต่อได้ แต่ไม่มีข้อความแปลกลับมา"
                : $"ได้ผลแล้ว: {thai}";
        }
        catch (OperationCanceledException)
        {
            CloudTestResult.Text = "หมดเวลารอ — ตรวจสอบการเชื่อมต่ออินเทอร์เน็ต";
        }
        catch (Exception ex) when (ex is CloudTranslationException or HttpRequestException)
        {
            Log.Warning(ex, "Cloud translation test failed.");
            CloudTestResult.Text = ex.Message;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Cloud translation test failed unexpectedly.");
            CloudTestResult.Text = $"ผิดพลาด ({ex.GetType().Name}): {ex.Message}";
        }
        finally
        {
            TestCloudButton.IsEnabled = true;
        }
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

    /// <summary>
    /// Deletes the downloaded model.
    ///
    /// The largest thing this app puts on a disk by two orders of magnitude, and
    /// the first thing to go for a player who has moved to a cloud engine or who
    /// needs the space back for a game.
    /// </summary>
    private void OnDeleteModelClick(object sender, RoutedEventArgs e)
    {
        string target = ModelTarget;

        if (!Confirm("โมเดลแปลภาษา", target, InstallCleaner.ModelSize(target)))
        {
            return;
        }

        try
        {
            InstallCleaner.DeleteModel(target);

            // A path the player chose with Browse is now a path to nothing.
            _settings.Translator.ModelPath = null;

            SaveSettings();
            Status("ลบโมเดลแล้ว");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ReportDeleteFailure(ex);
        }

        RefreshState();
    }

    private void OnDeleteServerClick(object sender, RoutedEventArgs e)
    {
        string target = ServerTarget;

        if (!Confirm("เซิร์ฟเวอร์แปลภาษา", target, InstallCleaner.RuntimeSize(target, InstallRoot)))
        {
            return;
        }

        try
        {
            InstallCleaner.DeleteRuntime(target, InstallRoot);
            _settings.Translator.ExecutablePath = null;

            SaveSettings();
            Status("ลบเซิร์ฟเวอร์แล้ว");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ReportDeleteFailure(ex);
        }

        RefreshState();
    }

    /// <summary>
    /// Asks before deleting, with the size, because this is not undoable and
    /// getting it back means downloading it again.
    /// </summary>
    private static bool Confirm(string what, string path, long bytes)
    {
        string size = bytes > 0
            ? $"\n\nคืนพื้นที่ได้ {FormatBytes(bytes)}"
            : string.Empty;

        return MessageBox.Show(
            $"ลบ{what}ที่ติดตั้งไว้?\n{path}{size}\n\nถ้าจะใช้อีกต้องดาวน์โหลดใหม่",
            "TLOverlay",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    private void ReportDeleteFailure(Exception ex)
    {
        // Almost always the server still running and holding its own files open.
        Log.Warning(ex, "Could not delete an installed file.");
        Status($"ลบไม่สำเร็จ: {ex.Message} — ถ้ากำลังแปลอยู่ ให้กดหยุดแปลก่อน");
    }

    /// <summary>
    /// Moves the install somewhere with room, or just points future downloads at
    /// it. Offered because the system drive is very often the full one, and the
    /// model is the only large thing this app writes.
    /// </summary>
    private async void OnChangeInstallLocationClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "เลือกโฟลเดอร์สำหรับเก็บเซิร์ฟเวอร์และโมเดล",
            Multiselect = false,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        string newRoot = dialog.FolderName;
        string oldRoot = InstallRoot;

        if (string.Equals(Path.GetFullPath(newRoot), Path.GetFullPath(oldRoot), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!InstallLocation.IsWritable(newRoot))
        {
            Status("เขียนไฟล์ในโฟลเดอร์นี้ไม่ได้ — เลือกที่อื่น");
            return;
        }

        string oldRuntime = Path.Combine(oldRoot, "runtime");
        string oldModels = Path.Combine(oldRoot, "models");

        bool anythingInstalled = Directory.Exists(oldRuntime) || Directory.Exists(oldModels);
        bool move = false;

        if (anythingInstalled)
        {
            var answer = MessageBox.Show(
                $"ย้ายไฟล์ที่ติดตั้งไว้แล้วไปที่\n{newRoot}\nด้วยหรือไม่?\n\n" +
                "เลือก No เพื่อเปลี่ยนเฉพาะตำแหน่งของการดาวน์โหลดครั้งต่อไป",
                "TLOverlay",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (answer == MessageBoxResult.Cancel)
            {
                return;
            }

            move = answer == MessageBoxResult.Yes;
        }

        if (!move)
        {
            ApplyInstallRoot(newRoot, oldRoot, rebasePaths: false);
            Status($"การดาวน์โหลดครั้งต่อไปจะเก็บไว้ที่ {newRoot}");
            return;
        }

        await RunAsync("install move", async (_, token) =>
        {
            Status("กำลังย้ายไฟล์…");

            var relay = new Progress<double>(fraction =>
            {
                DownloadBar.IsIndeterminate = false;
                DownloadBar.Value = fraction;
                ProgressLeft.Text = $"{fraction:P0}";
                ProgressRight.Text = "กำลังย้ายไฟล์";
            });

            await InstallLocation.MoveDirectoryAsync(oldRuntime, Path.Combine(newRoot, "runtime"), relay, token);
            await InstallLocation.MoveDirectoryAsync(oldModels, Path.Combine(newRoot, "models"), relay, token);

            ApplyInstallRoot(newRoot, oldRoot, rebasePaths: true);
            Status($"ย้ายไปที่ {newRoot} เรียบร้อยแล้ว");
        });
    }

    private void ApplyInstallRoot(string newRoot, string oldRoot, bool rebasePaths)
    {
        _settings.InstallRoot = newRoot;

        if (rebasePaths)
        {
            _settings.Translator.ExecutablePath = Rebase(_settings.Translator.ExecutablePath, oldRoot, newRoot);
            _settings.Translator.ModelPath = Rebase(_settings.Translator.ModelPath, oldRoot, newRoot);
        }

        SaveSettings();
        RefreshState();
    }

    /// <summary>
    /// Points a stored path at the new root, and leaves alone anything that was
    /// never under the old one - a file picked with Browse from somewhere else
    /// must not be rewritten to a place it was never moved to.
    /// </summary>
    internal static string? Rebase(string? path, string oldRoot, string newRoot)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        string full = Path.GetFullPath(path);
        string previous = Path.GetFullPath(oldRoot).TrimEnd(Path.DirectorySeparatorChar);

        if (!full.StartsWith(previous + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        return Path.Combine(newRoot, Path.GetRelativePath(previous, full));
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
