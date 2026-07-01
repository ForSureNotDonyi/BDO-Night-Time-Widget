using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace BDONightTimeTracker;

public partial class MainWindow : Window
{
    private readonly BdoCycleService   _cycle;
    private readonly BdoProcessWatcher _watcher;
    private readonly OcrService        _ocr;
    private readonly DispatcherTimer   _timer;

    // ─── P/Invoke ─────────────────────────────────────────────────────────────

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern int    GetWindowLong(IntPtr hwnd, int nIndex);
    [DllImport("user32.dll")] private static extern int    SetWindowLong(IntPtr hwnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] private static extern short  GetKeyState(int vKey);

    private const int GWL_EXSTYLE       = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int VK_CONTROL        = 0x11;
    private const int VK_LBUTTON        = 0x01;

    // ─── State ────────────────────────────────────────────────────────────────

    private System.Windows.Forms.NotifyIcon _trayIcon = null!;

    private CancellationTokenSource? _ocrCts;
    private int    _tickCount;
    private bool   _bdoWasFocused;
    private bool   _clickThrough;
    private IntPtr _ourHwnd;

    private static readonly SolidColorBrush DayColor     = new(Color.FromRgb(0xFF, 0xD7, 0x00));
    private static readonly SolidColorBrush NightColor   = new(Color.FromRgb(0x88, 0xAA, 0xFF));
    private static readonly SolidColorBrush WaitingColor = new(Color.FromRgb(0x44, 0x55, 0x66));

    public MainWindow()
    {
        InitializeComponent();
        LoadWindowPosition();

        Loaded += (_, _) =>
        {
            _ourHwnd = new WindowInteropHelper(this).Handle;
            ApplyClickThrough(true); // pass-through by default while grinding
        };

        InitTrayIcon();

        _cycle   = new BdoCycleService();
        _watcher = new BdoProcessWatcher();
        _ocr     = new OcrService(OcrRegionConfig.LoadOrDefault());

        _cycle.LoadFromFile();

        _watcher.BdoStarted   += OnBdoStarted;
        _watcher.BdoStopped   += OnBdoStopped;
        _watcher.BdoMinimized += OnBdoMinimized;
        _watcher.BdoRestored  += OnBdoRestored;

        // 250 ms display refresh; process poll and focus check every 4th tick (≈ 1 s).
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += OnTick;
        _timer.Start();

        _watcher.Poll();
        UpdateDisplay();
    }

    // ─── Timer ────────────────────────────────────────────────────────────────

    private void OnTick(object? sender, EventArgs e)
    {
        // Toggle click-through based on Ctrl key. Skip if left mouse button is
        // held so we don't interrupt an in-progress drag.
        bool mouseDown = (GetKeyState(VK_LBUTTON) & 0x8000) != 0;
        if (!mouseDown)
        {
            bool ctrlHeld = (GetKeyState(VK_CONTROL) & 0x8000) != 0;
            ApplyClickThrough(!ctrlHeld);
        }

        if (++_tickCount % 4 == 0)
        {
            _watcher.Poll();
            UpdateFocusVisibility();
        }

        UpdateDisplay();
    }

    // ─── Focus / visibility ───────────────────────────────────────────────────

    // Collapse (invisible) when BDO is not the foreground window so the widget
    // disappears entirely rather than jumping to the taskbar row like Minimized does.
    // We also treat our own window as "BDO focused" so Ctrl+drag doesn't collapse it.
    private void UpdateFocusVisibility()
    {
        if (!_watcher.IsBdoRunning) return;

        IntPtr fg      = GetForegroundWindow();
        bool   focused = fg == _watcher.BdoWindowHandle || fg == _ourHwnd;

        if (!focused && _bdoWasFocused)
            Visibility = Visibility.Collapsed;
        else if (focused && !_bdoWasFocused)
            Visibility = Visibility.Visible;

        _bdoWasFocused = focused;
    }

    // ─── Click-through ────────────────────────────────────────────────────────

    // WS_EX_TRANSPARENT makes all mouse events fall through to the window behind.
    // WPF sets WS_EX_LAYERED automatically for AllowsTransparency=True windows,
    // which is the prerequisite for WS_EX_TRANSPARENT to take effect.
    private void ApplyClickThrough(bool passThrough)
    {
        if (_ourHwnd == IntPtr.Zero || _clickThrough == passThrough) return;
        _clickThrough = passThrough;

        int style = GetWindowLong(_ourHwnd, GWL_EXSTYLE);
        SetWindowLong(_ourHwnd, GWL_EXSTYLE,
            passThrough ? style |  WS_EX_TRANSPARENT
                        : style & ~WS_EX_TRANSPARENT);
    }

    // ─── BdoProcessWatcher events ─────────────────────────────────────────────

    private void OnBdoStarted()
    {
        IntPtr fg      = GetForegroundWindow();
        _bdoWasFocused = fg == _watcher.BdoWindowHandle || fg == _ourHwnd;
        Visibility     = Visibility.Visible;
        if (_ocr.IsOcrAvailable)
            _ = TriggerOcrSyncAsync(delayMs: 3000);
    }

    private void OnBdoStopped()
    {
        _ocrCts?.Cancel();
        _bdoWasFocused = false;
        Visibility     = Visibility.Collapsed;
    }

    private void OnBdoMinimized()
    {
        _ocrCts?.Cancel();
        _bdoWasFocused = false;
        Visibility     = Visibility.Collapsed;
    }

    private void OnBdoRestored()
    {
        Visibility     = Visibility.Visible;
        _bdoWasFocused = true;
        if (_ocr.IsOcrAvailable)
            _ = TriggerOcrSyncAsync(delayMs: 1500);
    }

    // ─── Async OCR ────────────────────────────────────────────────────────────

    private async Task TriggerOcrSyncAsync(int delayMs)
    {
        _ocrCts?.Cancel();
        _ocrCts = new CancellationTokenSource();
        CancellationToken ct = _ocrCts.Token;

        try
        {
            await Task.Delay(delayMs, ct);
            IntPtr hwnd = _watcher.BdoWindowHandle;
            var (success, hour, minute, anchorRealTime) = await _ocr.CalibrateAnchorAsync(hwnd, ct);
            if (success) _cycle.SetAnchor(hour, minute, anchorRealTime);
        }
        catch (OperationCanceledException) { }

        UpdateDisplay();
    }

    // ─── UI update ────────────────────────────────────────────────────────────

    private void UpdateDisplay()
    {
        if (!_watcher.IsBdoRunning || !_cycle.IsAnchored)
        {
            PhaseIconText.Text       = "⏳";
            PhaseIconText.Foreground = WaitingColor;
            NextTransitionLabel.Text = "";
            return;
        }

        bool     isDay    = _cycle.IsDay();
        TimeSpan countdown = _cycle.GetTimeUntilTransition();
        int      totalSec  = (int)countdown.TotalSeconds;

        PhaseIconText.Text       = isDay ? "☀" : "☾";
        PhaseIconText.Foreground = isDay ? DayColor : NightColor;
        NextTransitionLabel.Text = isDay
            ? $"Night in {FormatCountdown(totalSec)}"
            : $"Day in {FormatCountdown(totalSec)}";
    }

    // ─── UI events ────────────────────────────────────────────────────────────

    private void ForceOcrButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_watcher.IsBdoRunning || !_ocr.IsOcrAvailable) return;
        _ocr.SaveDebugCapture(_watcher.BdoWindowHandle);
        _ = TriggerOcrSyncAsync(delayMs: 300);
    }

    private void MainBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        _ocrCts?.Cancel();
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        SaveWindowPosition();
        base.OnClosed(e);
    }

    // ─── Tray icon ────────────────────────────────────────────────────────────

    private void InitTrayIcon()
    {
        // Icon is embedded as a managed resource so it works inside a single-file exe.
        using var stream = System.Reflection.Assembly
            .GetExecutingAssembly()
            .GetManifestResourceStream("BDONightTimeTracker.Assets.bdo_widget_icon_spirit.ico");

        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon    = stream != null
                          ? new System.Drawing.Icon(stream)
                          : System.Drawing.SystemIcons.Application,
            Text    = "BDO Night Tracker",
            Visible = true,
        };

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Exit", null, (_, _) => { _ocrCts?.Cancel(); Close(); });
        _trayIcon.ContextMenuStrip = menu;
    }

    // ─── Position persistence ─────────────────────────────────────────────────

    private static string PosFilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BDONightTimeTracker", "window_pos.json");

    private void LoadWindowPosition()
    {
        try
        {
            if (!File.Exists(PosFilePath)) return;
            var pos = JsonSerializer.Deserialize<WindowPos>(File.ReadAllText(PosFilePath));
            if (pos is null) return;

            double vl = SystemParameters.VirtualScreenLeft;
            double vt = SystemParameters.VirtualScreenTop;
            double vr = vl + SystemParameters.VirtualScreenWidth  - Width;
            double vb = vt + SystemParameters.VirtualScreenHeight - Height;

            Left = Math.Clamp(pos.Left, vl, vr);
            Top  = Math.Clamp(pos.Top,  vt, vb);
        }
        catch { }
    }

    private void SaveWindowPosition()
    {
        try
        {
            string dir = Path.GetDirectoryName(PosFilePath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(PosFilePath, JsonSerializer.Serialize(new WindowPos(Left, Top)));
        }
        catch { }
    }

    private record WindowPos(double Left, double Top);

    private static string FormatCountdown(int totalSec)
    {
        if (totalSec >= 3600)
        {
            int h = totalSec / 3600;
            int m = (totalSec % 3600) / 60;
            return $"{h}h {m:D2}m";
        }
        if (totalSec >= 60)
        {
            int m = totalSec / 60;
            int s = totalSec % 60;
            return $"{m}m {s:D2}s";
        }
        return $"{totalSec}s";
    }
}
