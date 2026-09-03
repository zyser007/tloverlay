using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Serilog;
using TLOverlay.App.Interop;
using TLOverlay.App.Services;
using TLOverlay.Core.Input;
using TLOverlay.Core.Update;

namespace TLOverlay.App.Views;

/// <summary>
/// Everything the player configures that is not about one game: which keys do
/// what, and how updates behave.
///
/// A separate window rather than more cards on the control panel. The panel is
/// used mid-session - pick a window, start, stop - and settings are touched once
/// and then left alone; mixing them made the panel taller than most screens and
/// buried the button people actually came for.
/// </summary>
public partial class SettingsWindow : Window
{
    /// <summary>What each hotkey does, in the player's words.</summary>
    private static readonly Dictionary<HotKeyAction, string> ActionNames = new()
    {
        [HotKeyAction.ToggleTranslation] = "เปิด/ปิดการแปล",
        [HotKeyAction.EditRegions] = "เลือกพื้นที่การแปล",
        [HotKeyAction.ToggleTranslations] = "ซ่อน/แสดงข้อความแปล",
        [HotKeyAction.ToggleRegionOutlines] = "ซ่อน/แสดงพื้นที่การแปล",
        [HotKeyAction.ToggleClickThrough] = "สลับโหมดเมาส์",
        [HotKeyAction.TranslateOnce] = "แปลครั้งเดียว",
    };

    private readonly AppSettings _settings;
    private readonly GlobalHotKeyService _hotKeys;
    private readonly UpdateService _updates;
    private readonly List<HotKeyBinding> _bindings;
    private readonly Dictionary<HotKeyAction, Button> _buttons = new();

    private Button? _listening;
    private bool _loading = true;

    public SettingsWindow(AppSettings settings, GlobalHotKeyService hotKeys, UpdateService updates)
    {
        InitializeComponent();

        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _hotKeys = hotKeys ?? throw new ArgumentNullException(nameof(hotKeys));
        _updates = updates ?? throw new ArgumentNullException(nameof(updates));
        _bindings = [.. HotKeyProfile.Load(settings)];

        WindowSizing.ClampToWorkArea(this);

        BuildHotKeyRows();
        BuildUpdateSection();

        // Escape has to reach the capture in progress before it closes the window.
        PreviewKeyDown += OnPreviewKeyDown;

        // Capturing releases the global keys so the combination being rebound can
        // be pressed. Closing mid-capture would otherwise leave the player with no
        // hotkeys at all until the next restart.
        Closed += (_, _) =>
        {
            StopListening();
            ReRegister();
        };

        _loading = false;
    }

    /// <summary>True when a hotkey changed, so the owner can refresh what it shows.</summary>
    public bool HotKeysChanged { get; private set; }

    private void BuildHotKeyRows()
    {
        HotKeyList.Items.Clear();
        _buttons.Clear();

        foreach (HotKeyBinding binding in _bindings)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var label = new TextBlock
            {
                Text = ActionNames.TryGetValue(binding.Action, out string? name) ? name : binding.Action.ToString(),
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
            };

            Grid.SetColumn(label, 0);
            row.Children.Add(label);

            var button = new Button
            {
                Content = binding.Gesture,
                MinWidth = 150,
                Padding = new Thickness(10, 5, 10, 5),
                FontFamily = (FontFamily)FindResource("MonoFont"),
                FontSize = 12,
                Tag = binding.Action,
                ToolTip = "กดที่นี่ แล้วกดคีย์ใหม่ที่ต้องการ",
            };

            button.Click += OnCaptureClick;

            Grid.SetColumn(button, 1);
            row.Children.Add(button);

            _buttons[binding.Action] = button;
            HotKeyList.Items.Add(row);
        }

        ShowRegistrationState(_hotKeys.Register(_bindings));
    }

    /// <summary>
    /// Puts a button into listening mode. Only one at a time: two captures armed
    /// at once would race for the same key press.
    /// </summary>
    private void OnCaptureClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        StopListening();

        _listening = button;
        button.Content = "กดคีย์ที่ต้องการ…";
        button.FontWeight = FontWeights.SemiBold;

        // While a capture is armed the global keys are released, so the very
        // combination being rebound can be pressed without firing its action.
        _hotKeys.UnregisterAll();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_listening is null)
        {
            return;
        }

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape)
        {
            e.Handled = true;
            StopListening();
            ReRegister();
            return;
        }

        // A modifier on its own is the player still reaching for the key.
        if (IsModifier(key))
        {
            e.Handled = true;
            return;
        }

        e.Handled = true;

        ModifierKeys modifiers = Keyboard.Modifiers;

        var gesture = new HotKeyGesture(
            modifiers.HasFlag(ModifierKeys.Control),
            modifiers.HasFlag(ModifierKeys.Alt),
            modifiers.HasFlag(ModifierKeys.Shift),
            modifiers.HasFlag(ModifierKeys.Windows),
            key.ToString());

        Apply(gesture);
    }

    private void Apply(HotKeyGesture gesture)
    {
        if (_listening?.Tag is not HotKeyAction action)
        {
            return;
        }

        if (!gesture.IsValid)
        {
            // Without a modifier this key would be swallowed system-wide - taken
            // away from the game the overlay is sitting on.
            HotKeyWarning.Text = "ต้องมี Ctrl, Alt หรือ Shift อย่างน้อยหนึ่งตัว";
            StopListening();
            ReRegister();
            return;
        }

        HotKeyBinding? binding = HotKeyBinding.FromGesture(action, gesture);

        if (binding is null)
        {
            HotKeyWarning.Text = "ใช้คีย์นี้ไม่ได้ ลองคีย์อื่น";
            StopListening();
            ReRegister();
            return;
        }

        HotKeyBinding? clash = _bindings.FirstOrDefault(existing =>
            existing.Action != action
            && string.Equals(existing.Gesture, binding.Gesture, StringComparison.OrdinalIgnoreCase));

        if (clash is not null)
        {
            // Registering it would leave whichever came second doing nothing, with
            // nothing on screen to say why.
            HotKeyWarning.Text =
                $"{binding.Gesture} ถูกใช้กับ “{ActionNames[clash.Action]}” อยู่แล้ว";
            StopListening();
            ReRegister();
            return;
        }

        int index = _bindings.FindIndex(existing => existing.Action == action);
        _bindings[index] = binding;

        HotKeyProfile.Save(_settings, _bindings);
        SettingsStore.Save(App.DataDirectory, _settings);
        HotKeysChanged = true;

        StopListening();
        ReRegister();
    }

    private void StopListening()
    {
        if (_listening?.Tag is HotKeyAction action)
        {
            _listening.Content = _bindings.First(binding => binding.Action == action).Gesture;
            _listening.FontWeight = FontWeights.Normal;
        }

        _listening = null;
    }

    private void ReRegister() => ShowRegistrationState(_hotKeys.Register(_bindings));

    private void ShowRegistrationState(IReadOnlyList<HotKeyBinding> failed)
    {
        foreach (HotKeyBinding binding in _bindings)
        {
            if (_buttons.TryGetValue(binding.Action, out Button? button))
            {
                button.Content = binding.Gesture;
            }
        }

        // A key another application already owns is registered nowhere and does
        // nothing here. Saying which is the difference between "rebind that one"
        // and "this program is broken".
        HotKeyWarning.Text = failed.Count == 0
            ? string.Empty
            : "โปรแกรมอื่นใช้คีย์นี้อยู่ ลองเปลี่ยนเป็นคีย์อื่น: "
                + string.Join(", ", failed.Select(static binding => binding.Gesture));
    }

    private static bool IsModifier(Key key) => key
        is Key.LeftCtrl or Key.RightCtrl
        or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift
        or Key.LWin or Key.RWin
        or Key.System;

    private void BuildUpdateSection()
    {
        UpdatePolicyCombo.ItemsSource = new[]
        {
            new PolicyChoice(UpdatePolicy.Notify, "แจ้งเตือนเมื่อมีเวอร์ชันใหม่"),
            new PolicyChoice(UpdatePolicy.Automatic, "ดาวน์โหลดให้อัตโนมัติ"),
            new PolicyChoice(UpdatePolicy.Off, "ไม่ต้องตรวจสอบ"),
        };

        UpdatePolicyCombo.DisplayMemberPath = nameof(PolicyChoice.Label);
        UpdatePolicyCombo.SelectedItem = ((PolicyChoice[])UpdatePolicyCombo.ItemsSource)
            .FirstOrDefault(choice => choice.Policy == _settings.Updates);

        VersionText.Text = $"เวอร์ชันปัจจุบัน {App.Version}";

        UpdateHint.Text = UpdateService.CanSelfUpdate
            ? string.Empty
            : "โฟลเดอร์ที่ติดตั้งอยู่เขียนไฟล์ไม่ได้ จึงอัพเดทให้อัตโนมัติไม่ได้ — ต้องดาวน์โหลดมาแทนที่เอง";
    }

    private void OnUpdatePolicyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || UpdatePolicyCombo.SelectedItem is not PolicyChoice choice)
        {
            return;
        }

        _updates.SetPolicy(choice.Policy);
    }

    private async void OnCheckUpdatesClick(object sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        UpdateHint.Text = "กำลังตรวจสอบ…";

        try
        {
            UpdateManifest? found = await _updates.CheckAsync(force: true);

            UpdateHint.Text = found is null
                ? $"ใช้เวอร์ชันล่าสุดอยู่แล้ว ({App.Version})"
                : $"มีเวอร์ชันใหม่ {found.Version} — ปิดหน้านี้แล้วกด “อัพเดทเลย” บนแถบด้านบน";

            if (found is not null)
            {
                UpdateFound?.Invoke(this, found);
            }
        }
        catch (Exception ex) when (ex is UpdateCheckException or HttpRequestException or TaskCanceledException)
        {
            Log.Warning(ex, "Update check failed.");
            UpdateHint.Text = $"ตรวจสอบไม่สำเร็จ: {ex.Message}";
        }
        finally
        {
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    /// <summary>Raised when a manual check finds something, so the panel can show its banner.</summary>
    public event EventHandler<UpdateManifest>? UpdateFound;

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                "คืนคีย์ลัดและการตั้งค่าอัพเดทกลับเป็นค่าเริ่มต้นทั้งหมด?",
                "TLOverlay",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        StopListening();

        HotKeyProfile.Reset(_settings);
        _settings.Updates = UpdatePolicy.Notify;

        // A version the player skipped is part of what they set, so it goes too -
        // otherwise "reset" would leave an update silently hidden.
        _settings.SkippedVersion = null;
        SettingsStore.Save(App.DataDirectory, _settings);

        _bindings.Clear();
        _bindings.AddRange(HotKeyProfile.Load(_settings));
        HotKeysChanged = true;

        _loading = true;
        BuildHotKeyRows();
        BuildUpdateSection();
        _loading = false;

        HotKeyWarning.Text = string.Empty;
        UpdateHint.Text = "คืนค่าพื้นฐานแล้ว";
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>One row of the update policy dropdown.</summary>
    private sealed record PolicyChoice(UpdatePolicy Policy, string Label);
}
