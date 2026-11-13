using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MemoryMagic.Models;
using MemoryMagic.Services;
using Microsoft.Win32;

namespace MemoryMagic.UI;

public partial class TelemetryBarWindow : Window
{
    private const string RegSettingsPath = @"SOFTWARE\MemoryMagic";
    private const string RegBarLeft = "TelemetryBarLeft";
    private const string RegBarTop = "TelemetryBarTop";
    private const string RegBarDrive = "TelemetryBarDrive";

    private const double BaseBarWidth = 568;
    private const double BatteryBarExtraWidth = 78;
    private const double FixedBarHeight = 36;
    private const string LockedIconGeometry = "M12,17A2,2 0 0,0 14,15C14,13.89 13.1,13 12,13A2,2 0 0,0 10,15A2,2 0 0,0 12,17M18,8A2,2 0 0,1 20,10V20A2,2 0 0,1 18,22H6A2,2 0 0,1 4,20V10C4,8.89 4.9,8 6,8H7V6A5,5 0 0,1 12,1A5,5 0 0,1 17,6V8H18M12,3A3,3 0 0,0 9,6V8H15V6A3,3 0 0,0 12,3Z";
    private const string UnlockedIconGeometry = "M18,8A2,2 0 0,1 20,10V20A2,2 0 0,1 18,22H6A2,2 0 0,1 4,20V10C4,8.89 4.9,8 6,8H15V6A3,3 0 0,0 12,3A3,3 0 0,0 9,6H7A5,5 0 0,1 12,1A5,5 0 0,1 17,6V8H18M12,17A2,2 0 0,0 14,15A2,2 0 0,0 12,13A2,2 0 0,0 10,15A2,2 0 0,0 12,17Z";

    private readonly SystemTelemetryService _telemetry;
    private readonly Action _openDashboardAction;
    private readonly Action<bool>? _onModeChanged;
    private readonly Action<bool>? _onTopmostChanged;
    private readonly Action<bool>? _onLockChanged;
    private readonly Action<int>? _onRefreshRateChanged;
    private readonly Action<string>? _onDriveChanged;
    private readonly Action? _onHideRequested;
    private readonly DispatcherTimer _timer;
    private bool _isPinnedToTaskbar;
    private bool _isAlwaysOnTop;
    private bool _isPositionLocked;
    private bool _hasBatterySlot;
    private int _refreshSeconds;
    private string _selectedDrive;
    private SystemTelemetrySnapshot? _lastSnapshot;

    public bool IsPinnedToTaskbar => _isPinnedToTaskbar;
    public bool IsAlwaysOnTop => _isAlwaysOnTop;
    public bool IsPositionLocked => _isPositionLocked;
    public string SelectedDrive => _selectedDrive;

    public TelemetryBarWindow(
        SystemTelemetryService telemetry,
        Action openDashboardAction,
        int refreshSeconds = 2,
        bool isPinnedToTaskbar = false,
        string selectedDrive = "ALL",
        Action<bool>? onModeChanged = null,
        Action<int>? onRefreshRateChanged = null,
        Action<string>? onDriveChanged = null,
        Action? onHideRequested = null,
        bool isAlwaysOnTop = true,
        Action<bool>? onTopmostChanged = null,
        bool isPositionLocked = false,
        Action<bool>? onLockChanged = null)
    {
        InitializeComponent();
        _telemetry = telemetry;
        _openDashboardAction = openDashboardAction;
        _isPinnedToTaskbar = isPinnedToTaskbar;
        _isAlwaysOnTop = isAlwaysOnTop;
        _isPositionLocked = isPositionLocked;
        _selectedDrive = string.IsNullOrWhiteSpace(selectedDrive) ? "ALL" : selectedDrive;
        _onModeChanged = onModeChanged;
        _onTopmostChanged = onTopmostChanged;
        _onLockChanged = onLockChanged;
        _onRefreshRateChanged = onRefreshRateChanged;
        _onDriveChanged = onDriveChanged;
        _onHideRequested = onHideRequested;
        _refreshSeconds = Math.Clamp(refreshSeconds, 1, 60);
        Topmost = _isAlwaysOnTop;
        AlwaysOnTopMenuItem.IsChecked = _isAlwaysOnTop;

        bool hasBattery = false;
        try
        {
            _lastSnapshot = _telemetry.CaptureSnapshot();
            hasBattery = _lastSnapshot.HasBattery;
        }
        catch
        {
        }

        ConfigureFixedDimensions(hasBattery);
        UpdateModeUi();
        UpdateRateMenuChecks();

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(_refreshSeconds)
        };
        _timer.Tick += (_, _) => RefreshMetrics();

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        Loaded += (_, _) => ApplyCurrentPlacement();
    }

    public void SetRefreshIntervalSeconds(int seconds)
    {
        _refreshSeconds = Math.Clamp(seconds, 1, 60);
        _timer.Interval = TimeSpan.FromSeconds(_refreshSeconds);
        UpdateRateMenuChecks();
    }

    public void SetPinnedMode(bool pinned, bool notify = false)
    {
        _isPinnedToTaskbar = pinned;
        UpdateModeUi();
        ApplyCurrentPlacement();

        if (notify)
            _onModeChanged?.Invoke(pinned);
    }

    public void SetAlwaysOnTop(bool alwaysOnTop, bool notify = false)
    {
        _isAlwaysOnTop = alwaysOnTop;
        Topmost = alwaysOnTop;
        AlwaysOnTopMenuItem.IsChecked = alwaysOnTop;

        if (notify)
            _onTopmostChanged?.Invoke(alwaysOnTop);
    }

    public void SetPositionLocked(bool locked, bool notify = false)
    {
        _isPositionLocked = locked;
        if (locked && IsLoaded && !_isPinnedToTaskbar)
            SavePosition(Left, Top);

        UpdateModeUi();

        if (notify)
            _onLockChanged?.Invoke(locked);
    }

    public void SetSelectedDrive(string driveKey, bool notify = false)
    {
        _selectedDrive = string.IsNullOrWhiteSpace(driveKey) ? "ALL" : driveKey;
        SaveDriveSetting(_selectedDrive);
        RefreshMetrics();

        if (notify)
            _onDriveChanged?.Invoke(_selectedDrive);
    }

    public void ShowBar()
    {
        RefreshMetrics();
        Show();
        ApplyCurrentPlacement();
        _timer.Start();
    }

    public void HideBar()
    {
        _timer.Stop();
        Hide();
    }

    public void StopAndClose()
    {
        _timer.Stop();
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        Close();
    }

    private void ConfigureFixedDimensions(bool hasBattery)
    {
        _hasBatterySlot = hasBattery;
        double lockedWidth = hasBattery ? BaseBarWidth + BatteryBarExtraWidth : BaseBarWidth;

        BatteryColumn.Width = hasBattery ? new GridLength(BatteryBarExtraWidth) : new GridLength(0);
        BatteryPanel.Visibility = hasBattery ? Visibility.Visible : Visibility.Collapsed;

        Width = lockedWidth;
        MinWidth = lockedWidth;
        MaxWidth = lockedWidth;
        Height = FixedBarHeight;
        MinHeight = FixedBarHeight;
        MaxHeight = FixedBarHeight;
    }

    private void RefreshMetrics()
    {
        try
        {
            SystemTelemetrySnapshot s = _telemetry.CaptureSnapshot();
            _lastSnapshot = s;

            CpuText.Text = $"CPU {s.CpuUsagePercent,3:F0}%";
            RamText.Text = $"RAM {s.RamLoadPercent,3:F0}%";
            NetText.Text = $"↓{FormatFixedRate(s.NetworkDownBytesPerSec)} ↑{FormatFixedRate(s.NetworkUpBytesPerSec)}";

            (string scopeLabel, double activePct, ulong freeBytes, ulong totalBytes, string healthStatus) =
                SystemTelemetryService.SelectDriveScope(s, _selectedDrive);

            DiskText.Text = $"{FormatDriveScope(scopeLabel)} {activePct,3:F0}% {FormatShortCapacity(freeBytes)}";
            DiskHealthText.Text = FormatShortHealth(healthStatus);
            DiskHealthText.Foreground = healthStatus switch
            {
                "Healthy" => new SolidColorBrush(Color.FromRgb(74, 222, 128)),
                "Warning" => new SolidColorBrush(Color.FromRgb(251, 191, 36)),
                "Unhealthy" => new SolidColorBrush(Color.FromRgb(248, 113, 113)),
                _ => new SolidColorBrush(Color.FromRgb(148, 163, 184))
            };

            DiskWidgetPanel.ToolTip = BuildDiskTooltip(s, scopeLabel, activePct, freeBytes, totalBytes, healthStatus);

            if (_hasBatterySlot && s.HasBattery)
                BatteryText.Text = s.IsOnAcPower ? $"{s.BatteryPercent,3}%⚡" : $"{s.BatteryPercent,3}% ";
        }
        catch
        {
        }
    }

    private void ApplyCurrentPlacement()
    {
        if (_isPinnedToTaskbar)
        {
            DockAboveTaskbar();
            return;
        }

        if (TryLoadSavedPosition(out double savedLeft, out double savedTop))
        {
            Rect clamped = ClampToVirtualScreen(savedLeft, savedTop);
            Left = clamped.Left;
            Top = clamped.Top;
        }
        else
        {
            DockAboveTaskbar();
        }
    }

    private void DockAboveTaskbar()
    {
        Rect workArea = SystemParameters.WorkArea;
        double w = Width > 0 ? Width : BaseBarWidth;
        double h = Height > 0 ? Height : FixedBarHeight;

        Left = workArea.Right - w - 12;
        Top = workArea.Bottom - h - 8;
    }

    private Rect ClampToVirtualScreen(double left, double top)
    {
        double w = Width > 0 ? Width : BaseBarWidth;
        double h = Height > 0 ? Height : FixedBarHeight;

        double minX = SystemParameters.VirtualScreenLeft;
        double minY = SystemParameters.VirtualScreenTop;
        double maxX = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - w;
        double maxY = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - h;

        return new Rect(
            Math.Clamp(left, minX, Math.Max(minX, maxX)),
            Math.Clamp(top, minY, Math.Max(minY, maxY)),
            w,
            h);
    }

    private void UpdateModeUi()
    {
        PinToTaskbarMenuItem.IsChecked = _isPinnedToTaskbar;
        FloatingMenuItem.IsChecked = !_isPinnedToTaskbar;
        LockPositionMenuItem.IsChecked = _isPositionLocked;
        LockPositionButton.IsChecked = _isPositionLocked;

        if (_isPositionLocked)
        {
            LockIconPath.Data = Geometry.Parse(LockedIconGeometry);
            LockIconPath.Fill = new SolidColorBrush(Color.FromRgb(56, 189, 248));
            LockPositionButton.Background = new SolidColorBrush(Color.FromRgb(12, 74, 110));
            LockPositionButton.BorderBrush = new SolidColorBrush(Color.FromRgb(2, 132, 199));
            LockPositionButton.ToolTip = "Position Locked (Click to Unlock)";
        }
        else
        {
            LockIconPath.Data = Geometry.Parse(UnlockedIconGeometry);
            LockIconPath.Fill = new SolidColorBrush(Color.FromRgb(148, 163, 184));
            LockPositionButton.Background = new SolidColorBrush(Color.FromRgb(30, 41, 59));
            LockPositionButton.BorderBrush = new SolidColorBrush(Color.FromRgb(51, 65, 85));
            LockPositionButton.ToolTip = "Position Unlocked — Drag to Move (Click to Lock)";
        }

        Cursor = (_isPinnedToTaskbar || _isPositionLocked) ? Cursors.Arrow : Cursors.SizeAll;
    }

    private void UpdateRateMenuChecks()
    {
        Rate1sMenuItem.IsChecked = _refreshSeconds == 1;
        Rate2sMenuItem.IsChecked = _refreshSeconds == 2;
        Rate5sMenuItem.IsChecked = _refreshSeconds == 5;
        Rate10sMenuItem.IsChecked = _refreshSeconds == 10;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(ApplyCurrentPlacement);
    }

    private void OnMouseDownDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;

        if (e.ClickCount >= 2)
        {
            e.Handled = true;
            _openDashboardAction();
            return;
        }

        if (_isPinnedToTaskbar || _isPositionLocked || e.LeftButton != MouseButtonState.Pressed)
            return;

        try
        {
            DragMove();
            SavePosition(Left, Top);
        }
        catch
        {
        }
    }

    private void OnPinToTaskbarClick(object sender, RoutedEventArgs e)
    {
        SetPinnedMode(true, notify: true);
    }

    private void OnFloatingModeClick(object sender, RoutedEventArgs e)
    {
        SetPinnedMode(false, notify: true);
    }

    private void OnAlwaysOnTopClick(object sender, RoutedEventArgs e)
    {
        SetAlwaysOnTop(AlwaysOnTopMenuItem.IsChecked, notify: true);
    }

    private void OnLockPositionButtonClick(object sender, RoutedEventArgs e)
    {
        SetPositionLocked(LockPositionButton.IsChecked == true, notify: true);
    }

    private void OnLockPositionMenuClick(object sender, RoutedEventArgs e)
    {
        SetPositionLocked(LockPositionMenuItem.IsChecked, notify: true);
    }

    private void OnRefreshRateClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && int.TryParse(mi.Tag?.ToString(), out int seconds))
        {
            SetRefreshIntervalSeconds(seconds);
            _onRefreshRateChanged?.Invoke(seconds);
        }
    }

    private void OnOpenDashboardClick(object sender, RoutedEventArgs e) => _openDashboardAction();

    private void OnHideBarClick(object sender, RoutedEventArgs e) => _onHideRequested?.Invoke();

    private void OnBarContextMenuOpened(object sender, RoutedEventArgs e)
    {
        PopulateDriveMenuItems(DiskDriveMenu.Items);
    }

    private void OnDiskSelectorButtonClick(object sender, RoutedEventArgs e)
    {
        PopulateDriveMenuItems(DiskSelectorContextMenu.Items);
        DiskSelectorContextMenu.PlacementTarget = DiskSelectorButton;
        Rect work = SystemParameters.WorkArea;
        DiskSelectorContextMenu.Placement = Top > work.Height * 0.5 ? PlacementMode.Top : PlacementMode.Bottom;
        DiskSelectorContextMenu.IsOpen = true;
    }

    private void PopulateDriveMenuItems(ItemCollection items)
    {
        items.Clear();
        SystemTelemetrySnapshot snap = _lastSnapshot ?? _telemetry.CaptureSnapshot();

        var allItem = new MenuItem
        {
            Header = $"All Drives ({snap.Drives.Count} fixed, {FormatCapacityDetail(snap.DiskFreeBytes)} free | {snap.DiskHealthStatus})",
            IsCheckable = true,
            IsChecked = string.Equals(_selectedDrive, "ALL", StringComparison.OrdinalIgnoreCase),
            Tag = "ALL"
        };
        allItem.Click += OnSelectDriveMenuItemClick;
        items.Add(allItem);

        if (snap.Drives.Count > 0)
            items.Add(new Separator());

        foreach (DriveTelemetryItem d in snap.Drives)
        {
            string label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? "Local Disk" : d.VolumeLabel;
            var item = new MenuItem
            {
                Header = $"{d.DriveName} ({label}) — Active {d.ActivePercent:F0}% | {FormatCapacityDetail(d.FreeBytes)} free | {d.HealthStatus}",
                IsCheckable = true,
                IsChecked = string.Equals(_selectedDrive, d.DriveName, StringComparison.OrdinalIgnoreCase),
                Tag = d.DriveName
            };
            item.Click += OnSelectDriveMenuItemClick;
            items.Add(item);
        }
    }

    private void OnSelectDriveMenuItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is string driveKey)
            SetSelectedDrive(driveKey, notify: true);
    }

    private static string BuildDiskTooltip(
        SystemTelemetrySnapshot s,
        string scopeLabel,
        double activePct,
        ulong freeBytes,
        ulong totalBytes,
        string healthStatus)
    {
        var lines = new List<string>
        {
            $"Scope: {(scopeLabel == "ALL" ? "All Fixed Drives" : scopeLabel)} | Active: {activePct:F0}% | Free: {FormatCapacityDetail(freeBytes)} / {FormatCapacityDetail(totalBytes)} | Health: {healthStatus}"
        };

        foreach (DriveTelemetryItem d in s.Drives)
        {
            lines.Add($" • {d.DriveName} [{d.PhysicalDiskName}] — Active {d.ActivePercent:F0}%, Free {FormatCapacityDetail(d.FreeBytes)} / {FormatCapacityDetail(d.TotalBytes)}, Health: {d.HealthStatus}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatDriveScope(string scope)
    {
        if (string.Equals(scope, "ALL", StringComparison.OrdinalIgnoreCase))
            return "ALL";
        return scope.Length >= 3 ? scope[..3] : scope.PadRight(3);
    }

    private static string FormatShortCapacity(ulong bytes)
    {
        double gb = bytes / (1024d * 1024d * 1024d);
        if (gb >= 1024d)
        {
            double tb = gb / 1024d;
            return $"{tb,3:F1}T";
        }
        return gb >= 10d ? $"{gb,3:F0}G" : $"{gb,3:F1}G";
    }

    private static string FormatCapacityDetail(ulong bytes)
    {
        double gb = bytes / (1024d * 1024d * 1024d);
        return gb >= 1024d ? $"{gb / 1024d:F2} TB" : $"{gb:F1} GB";
    }

    private static string FormatShortHealth(string status) => status switch
    {
        "Healthy" => "OK",
        "Warning" => "WARN",
        "Unhealthy" => "FAIL",
        _ => "UNK"
    };

    private static void SaveDriveSetting(string driveKey)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RegSettingsPath, true);
            key.SetValue(RegBarDrive, driveKey, RegistryValueKind.String);
        }
        catch
        {
        }
    }

    private static bool TryLoadSavedPosition(out double left, out double top)
    {
        left = 0;
        top = 0;
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RegSettingsPath, false);
            if (key?.GetValue(RegBarLeft) is string ls &&
                key.GetValue(RegBarTop) is string ts &&
                double.TryParse(ls, NumberStyles.Float, CultureInfo.InvariantCulture, out left) &&
                double.TryParse(ts, NumberStyles.Float, CultureInfo.InvariantCulture, out top))
            {
                return true;
            }
        }
        catch
        {
        }
        return false;
    }

    private static void SavePosition(double left, double top)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RegSettingsPath, true);
            key.SetValue(RegBarLeft, left.ToString("F1", CultureInfo.InvariantCulture), RegistryValueKind.String);
            key.SetValue(RegBarTop, top.ToString("F1", CultureInfo.InvariantCulture), RegistryValueKind.String);
        }
        catch
        {
        }
    }

    private static string FormatFixedRate(double bytesPerSec)
    {
        if (bytesPerSec >= 1024d * 1024d * 1024d)
            return $"{bytesPerSec / (1024d * 1024d * 1024d),4:F1}G";
        if (bytesPerSec >= 1024d * 1024d)
        {
            double mb = bytesPerSec / (1024d * 1024d);
            return mb >= 10d ? $"{mb,4:F0}M" : $"{mb,4:F1}M";
        }
        if (bytesPerSec >= 1024d)
            return $"{bytesPerSec / 1024d,4:F0}K";
        return $"{bytesPerSec,4:F0}B";
    }
}
