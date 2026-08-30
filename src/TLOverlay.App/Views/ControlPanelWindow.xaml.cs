using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using TLOverlay.Core.Capture;

namespace TLOverlay.App.Views;

public partial class ControlPanelWindow : Window
{
    public ControlPanelWindow()
    {
        InitializeComponent();
        WindowList.ItemsSource = Windows;
        Refresh();
    }

    public ObservableCollection<GameWindow> Windows { get; } = [];

    public GameWindow? SelectedWindow => WindowList.SelectedItem as GameWindow;

    private void Refresh()
    {
        Windows.Clear();

        foreach (var window in WindowFinder.EnumerateCandidates())
        {
            Windows.Add(window);
        }

        StatusText.Text = Windows.Count == 0
            ? "ไม่พบหน้าต่างที่จับภาพได้ — เปิดเกมก่อนแล้วกดรีเฟรช"
            : $"พบ {Windows.Count} หน้าต่าง";
    }

    private void OnWindowSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = SelectedWindow;
        if (selected is null)
        {
            return;
        }

        // Exclusive fullscreen is the single most common reason capture comes back
        // black, and it is not obvious to the player, so say it plainly here.
        StatusText.Text = WindowFinder.IsBorderless(selected.Handle)
            ? $"เลือก: {selected.Title} ({selected.Width}x{selected.Height}) — เป็นหน้าต่างแบบ borderless พร้อมจับภาพ"
            : $"เลือก: {selected.Title} — หน้าต่างนี้มีขอบ ถ้าเกมอยู่ในโหมด Exclusive Fullscreen จะจับภาพไม่ได้";
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => Refresh();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
