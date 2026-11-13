using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MemoryMagic.Models;
using MemoryMagic.UI;
using Microsoft.Win32;
using Drawing = System.Drawing;
using Drawing2D = System.Drawing.Drawing2D;
using Forms = System.Windows.Forms;

namespace MemoryMagic.Services;

/// <summary>
/// Manages the Windows System Tray icon, dynamic right-click telemetry readout
/// (RAM, CPU, Disk, Network, Battery if present), and the floating Graphical Telemetry Bar.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private const string RegSettingsPath = @"SOFTWARE\MemoryMagic";
    private const string RegBarVisible = "TelemetryBarVisible";
    private const string RegRefreshSeconds = "TelemetryRefreshSeconds";
    private const string RegBarPinned = "TelemetryBarPinned";
    private const string RegBarAlwaysOnTop = "TelemetryBarAlwaysOnTop";
    private const string RegBarLocked = "TelemetryBarLocked";
    private const string RegBarDrive = "TelemetryBarDrive";

    private readonly SystemTelemetryService _telemetry;
    private readonly MemoryOptimizerService _optimizer;
    private readonly TempCleanerService _tempCleaner;
    private readonly AuditLogger _logger;
    private readonly Action _restoreWindowAction;
    private readonly Action _exitAppAction;

    private readonly Drawing.Icon _trayIconGraphic;
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ContextMenuStrip _contextMenu;
    private readonly Forms.ToolStripMenuItem _ramTelemetryItem;
    private readonly Forms.ToolStripMenuItem _cpuDiskTelemetryItem;
    private readonly Forms.ToolStripMenuItem _netBatTelemetryItem;
    private readonly Forms.ToolStripMenuItem _toggleBarItem;
    private readonly Forms.ToolStripMenuItem _alwaysOnTopItem;
    private readonly Forms.ToolStripMenuItem _lockPositionItem;
    private readonly Forms.ToolStripMenuItem _pinToTaskbarItem;
    private readonly Forms.ToolStripMenuItem _floatingModeItem;
    private readonly Forms.ToolStripMenuItem _driveScopeMenu;
    private readonly Dictionary<int, Forms.ToolStripMenuItem> _rateItems = new();
    private readonly TelemetryBarWindow _telemetryBar;

    private int _refreshSeconds;
    private bool _isPinnedToTaskbar;
    private bool _isAlwaysOnTop;
    private bool _isPositionLocked;
    private string _selectedDrive;
    private bool _disposed;

    public event Action? TelemetryBarStateChanged;

    public bool IsTelemetryBarVisible
    {
        get => _toggleBarItem.Checked;
        set
        {
            if (_toggleBarItem.Checked == value)
                return;

            _toggleBarItem.Checked = value;
            OnToggleTelemetryBar();
        }
    }

    public bool IsTelemetryBarAlwaysOnTop
    {
        get => _isAlwaysOnTop;
        set
        {
            if (_isAlwaysOnTop == value)
                return;

            ApplyAlwaysOnTopFromUi(value);
            _telemetryBar.SetAlwaysOnTop(value, notify: false);
        }
    }

    public bool IsTelemetryBarPositionLocked
    {
        get => _isPositionLocked;
        set
        {
            if (_isPositionLocked == value)
                return;

            ApplyPositionLockedFromUi(value);
            _telemetryBar.SetPositionLocked(value, notify: false);
        }
    }

    public TrayIconService(
        SystemTelemetryService telemetry,
        MemoryOptimizerService optimizer,
        TempCleanerService tempCleaner,
        AuditLogger logger,
        Action restoreWindowAction,
        Action exitAppAction)
    {
        _telemetry = telemetry;
        _optimizer = optimizer;
        _tempCleaner = tempCleaner;
        _logger = logger;
        _restoreWindowAction = restoreWindowAction;
        _exitAppAction = exitAppAction;

        _refreshSeconds = LoadIntSetting(RegRefreshSeconds, 2);
        if (_refreshSeconds is not (1 or 2 or 5 or 10))
            _refreshSeconds = 2;

        bool showBar = LoadIntSetting(RegBarVisible, 0) == 1;
        _isPinnedToTaskbar = LoadIntSetting(RegBarPinned, 0) == 1;
        _isAlwaysOnTop = LoadIntSetting(RegBarAlwaysOnTop, 1) == 1;
        _isPositionLocked = LoadIntSetting(RegBarLocked, 0) == 1;
        _selectedDrive = LoadStringSetting(RegBarDrive, "ALL");

        _trayIconGraphic = CreateCustomAppIcon();
        _telemetryBar = new TelemetryBarWindow(
            _telemetry,
            _restoreWindowAction,
            _refreshSeconds,
            _isPinnedToTaskbar,
            _selectedDrive,
            onModeChanged: ApplyBarModeFromUi,
            onRefreshRateChanged: ApplyRefreshRateFromUi,
            onDriveChanged: ApplyDriveFromUi,
            onHideRequested: HideBarFromUi,
            isAlwaysOnTop: _isAlwaysOnTop,
            onTopmostChanged: ApplyAlwaysOnTopFromUi,
            isPositionLocked: _isPositionLocked,
            onLockChanged: ApplyPositionLockedFromUi);

        _ramTelemetryItem = new Forms.ToolStripMenuItem("RAM: Sampling...")
        {
            ForeColor = Drawing.Color.FromArgb(56, 189, 248)
        };
        _cpuDiskTelemetryItem = new Forms.ToolStripMenuItem("CPU: --  |  Disk: --")
        {
            ForeColor = Drawing.Color.FromArgb(226, 232, 240)
        };
        _netBatTelemetryItem = new Forms.ToolStripMenuItem("Net: --")
        {
            ForeColor = Drawing.Color.FromArgb(148, 163, 184)
        };

        _toggleBarItem = new Forms.ToolStripMenuItem("Show Graphical Telemetry Bar")
        {
            CheckOnClick = true,
            Checked = showBar
        };
        _toggleBarItem.Click += (_, _) => OnToggleTelemetryBar();

        _alwaysOnTopItem = new Forms.ToolStripMenuItem("Always on Top")
        {
            CheckOnClick = true,
            Checked = _isAlwaysOnTop
        };
        _alwaysOnTopItem.Click += (_, _) => IsTelemetryBarAlwaysOnTop = _alwaysOnTopItem.Checked;

        _lockPositionItem = new Forms.ToolStripMenuItem("Lock Bar Position")
        {
            CheckOnClick = true,
            Checked = _isPositionLocked
        };
        _lockPositionItem.Click += (_, _) => IsTelemetryBarPositionLocked = _lockPositionItem.Checked;

        _pinToTaskbarItem = new Forms.ToolStripMenuItem("Pin to Taskbar", null, (_, _) => SetBarPinnedMode(true))
        {
            Checked = _isPinnedToTaskbar
        };
        _floatingModeItem = new Forms.ToolStripMenuItem("Floating (Draggable)", null, (_, _) => SetBarPinnedMode(false))
        {
            Checked = !_isPinnedToTaskbar
        };

        _driveScopeMenu = new Forms.ToolStripMenuItem("Disk Drive Scope");
        _driveScopeMenu.DropDown.BackColor = Drawing.Color.FromArgb(15, 23, 42);
        _driveScopeMenu.DropDown.ForeColor = Drawing.Color.FromArgb(248, 250, 252);

        var refreshRateMenu = new Forms.ToolStripMenuItem("Refresh Rate");
        refreshRateMenu.DropDown.BackColor = Drawing.Color.FromArgb(15, 23, 42);
        refreshRateMenu.DropDown.ForeColor = Drawing.Color.FromArgb(248, 250, 252);
        foreach (int sec in new[] { 1, 2, 5, 10 })
        {
            int captured = sec;
            var item = new Forms.ToolStripMenuItem($"{sec}s", null, (_, _) => SetRefreshRate(captured))
            {
                Checked = _refreshSeconds == sec,
                BackColor = Drawing.Color.FromArgb(15, 23, 42),
                ForeColor = Drawing.Color.FromArgb(248, 250, 252)
            };
            _rateItems[sec] = item;
            refreshRateMenu.DropDownItems.Add(item);
        }

        var optimizeSafeItem = new Forms.ToolStripMenuItem("Optimize RAM Now", null, async (_, _) => await OptimizeFromTrayAsync(OptimizationScope.SafeStandard, "Tray-Safe"));
        var optimizeDeepItem = new Forms.ToolStripMenuItem("Deep Purge (Admin)", null, async (_, _) => await OptimizeFromTrayAsync(OptimizationScope.DeepPurge, "Tray-Deep"));
        var cleanTempItem = new Forms.ToolStripMenuItem("Clean Temp Files", null, async (_, _) => await CleanTempFromTrayAsync());
        var openItem = new Forms.ToolStripMenuItem("Open Dashboard", null, (_, _) => _restoreWindowAction());
        var exitItem = new Forms.ToolStripMenuItem("Exit", null, (_, _) => _exitAppAction());

        _contextMenu = new Forms.ContextMenuStrip
        {
            Renderer = new DarkTrayRenderer(),
            ForeColor = Drawing.Color.FromArgb(248, 250, 252),
            BackColor = Drawing.Color.FromArgb(15, 23, 42)
        };

        _contextMenu.Items.Add(_ramTelemetryItem);
        _contextMenu.Items.Add(_cpuDiskTelemetryItem);
        _contextMenu.Items.Add(_netBatTelemetryItem);
        _contextMenu.Items.Add(new Forms.ToolStripSeparator());
        _contextMenu.Items.Add(_toggleBarItem);
        _contextMenu.Items.Add(_alwaysOnTopItem);
        _contextMenu.Items.Add(_lockPositionItem);
        _contextMenu.Items.Add(_pinToTaskbarItem);
        _contextMenu.Items.Add(_floatingModeItem);
        _contextMenu.Items.Add(_driveScopeMenu);
        _contextMenu.Items.Add(refreshRateMenu);
        _contextMenu.Items.Add(new Forms.ToolStripSeparator());
        _contextMenu.Items.Add(optimizeSafeItem);
        _contextMenu.Items.Add(optimizeDeepItem);
        _contextMenu.Items.Add(cleanTempItem);
        _contextMenu.Items.Add(new Forms.ToolStripSeparator());
        _contextMenu.Items.Add(openItem);
        _contextMenu.Items.Add(exitItem);

        _contextMenu.Opening += OnContextMenuOpening;

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _trayIconGraphic,
            Text = "Memory Magic — Background Monitor",
            ContextMenuStrip = _contextMenu,
            Visible = true
        };

        _notifyIcon.DoubleClick += (_, _) => _restoreWindowAction();

        if (showBar)
            _telemetryBar.ShowBar();
    }

    public ImageSource CreateWpfWindowIcon()
    {
        IntPtr hIcon = _trayIconGraphic.Handle;
        ImageSource source = Imaging.CreateBitmapSourceFromHIcon(
            hIcon,
            Int32Rect.Empty,
            BitmapSizeOptions.FromEmptyOptions());
        source.Freeze();
        return source;
    }

    public void UpdateTooltip(MemorySnapshot snapshot)
    {
        if (_disposed)
            return;

        double usedGb = snapshot.UsedPhysicalBytes / (1024.0 * 1024.0 * 1024.0);
        double totalGb = snapshot.TotalPhysicalBytes / (1024.0 * 1024.0 * 1024.0);
        string text = $"Memory Magic | RAM: {snapshot.LoadPercent:F0}% ({usedGb:F1}/{totalGb:F1} GB)";
        _notifyIcon.Text = text.Length > 63 ? text[..63] : text;
    }

    public void ShowBalloon(string title, string message, Forms.ToolTipIcon icon = Forms.ToolTipIcon.Info)
    {
        if (_disposed)
            return;

        _notifyIcon.ShowBalloonTip(2500, title, message, icon);
    }

    private void OnToggleTelemetryBar()
    {
        bool show = _toggleBarItem.Checked;
        SaveIntSetting(RegBarVisible, show ? 1 : 0);

        if (show)
            _telemetryBar.ShowBar();
        else
            _telemetryBar.HideBar();

        TelemetryBarStateChanged?.Invoke();
    }

    private void ApplyAlwaysOnTopFromUi(bool alwaysOnTop)
    {
        _isAlwaysOnTop = alwaysOnTop;
        SaveIntSetting(RegBarAlwaysOnTop, alwaysOnTop ? 1 : 0);
        _alwaysOnTopItem.Checked = alwaysOnTop;
        TelemetryBarStateChanged?.Invoke();
    }

    private void ApplyPositionLockedFromUi(bool locked)
    {
        _isPositionLocked = locked;
        SaveIntSetting(RegBarLocked, locked ? 1 : 0);
        _lockPositionItem.Checked = locked;
        TelemetryBarStateChanged?.Invoke();
    }

    private void SetBarPinnedMode(bool pinned)
    {
        ApplyBarModeFromUi(pinned);
        _telemetryBar.SetPinnedMode(pinned, notify: false);
    }

    private void ApplyBarModeFromUi(bool pinned)
    {
        _isPinnedToTaskbar = pinned;
        SaveIntSetting(RegBarPinned, pinned ? 1 : 0);
        _pinToTaskbarItem.Checked = pinned;
        _floatingModeItem.Checked = !pinned;
    }

    private void ApplyRefreshRateFromUi(int seconds)
    {
        _refreshSeconds = seconds;
        SaveIntSetting(RegRefreshSeconds, seconds);
        foreach (KeyValuePair<int, Forms.ToolStripMenuItem> kvp in _rateItems)
            kvp.Value.Checked = kvp.Key == seconds;
    }

    private void ApplyDriveFromUi(string driveKey)
    {
        _selectedDrive = string.IsNullOrWhiteSpace(driveKey) ? "ALL" : driveKey;
    }

    private void SetSelectedDrive(string driveKey)
    {
        ApplyDriveFromUi(driveKey);
        _telemetryBar.SetSelectedDrive(_selectedDrive, notify: false);
    }

    private void HideBarFromUi()
    {
        _toggleBarItem.Checked = false;
        SaveIntSetting(RegBarVisible, 0);
        _telemetryBar.HideBar();
        TelemetryBarStateChanged?.Invoke();
    }

    private void SetRefreshRate(int seconds)
    {
        _telemetryBar.SetRefreshIntervalSeconds(seconds);
        ApplyRefreshRateFromUi(seconds);
    }

    private void OnContextMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        try
        {
            SystemTelemetrySnapshot t = _telemetry.CaptureSnapshot();
            double ramUsedGb = t.RamUsedBytes / (1024.0 * 1024.0 * 1024.0);
            double ramTotalGb = t.RamTotalBytes / (1024.0 * 1024.0 * 1024.0);

            (string scope, double activePct, ulong freeBytes, _, string health) =
                SystemTelemetryService.SelectDriveScope(t, _selectedDrive);
            double diskFreeGb = freeBytes / (1024.0 * 1024.0 * 1024.0);

            _ramTelemetryItem.Text = $"RAM: {t.RamLoadPercent:F0}%  ({ramUsedGb:F1} / {ramTotalGb:F1} GB)";
            _cpuDiskTelemetryItem.Text = $"CPU: {t.CpuUsagePercent:F1}%  |  Disk ({scope}): {activePct:F0}% act, {diskFreeGb:F0} GB free ({health})";

            string netText = $"Net: ↓{FormatRate(t.NetworkDownBytesPerSec)}/s  ↑{FormatRate(t.NetworkUpBytesPerSec)}/s";
            _netBatTelemetryItem.Text = t.HasBattery
                ? $"{netText}  |  Bat: {t.BatteryPercent}%{(t.IsOnAcPower ? "⚡" : "")}"
                : netText;

            PopulateTrayDriveSubmenu(t);
        }
        catch (Exception ex)
        {
            _ramTelemetryItem.Text = "Telemetry unavailable";
            _cpuDiskTelemetryItem.Text = ex.Message;
        }
    }

    private void PopulateTrayDriveSubmenu(SystemTelemetrySnapshot t)
    {
        _driveScopeMenu.DropDownItems.Clear();

        var allItem = new Forms.ToolStripMenuItem(
            $"All Drives ({t.Drives.Count} fixed, {t.DiskFreeBytes / (1024.0 * 1024.0 * 1024.0):F0} GB free | {t.DiskHealthStatus})",
            null,
            (_, _) => SetSelectedDrive("ALL"))
        {
            Checked = string.Equals(_selectedDrive, "ALL", StringComparison.OrdinalIgnoreCase),
            BackColor = Drawing.Color.FromArgb(15, 23, 42),
            ForeColor = Drawing.Color.FromArgb(248, 250, 252)
        };
        _driveScopeMenu.DropDownItems.Add(allItem);

        foreach (DriveTelemetryItem d in t.Drives)
        {
            string driveKey = d.DriveName;
            var item = new Forms.ToolStripMenuItem(
                $"{d.DriveName} — Active {d.ActivePercent:F0}% | {d.FreeBytes / (1024.0 * 1024.0 * 1024.0):F0} GB free | {d.HealthStatus}",
                null,
                (_, _) => SetSelectedDrive(driveKey))
            {
                Checked = string.Equals(_selectedDrive, driveKey, StringComparison.OrdinalIgnoreCase),
                BackColor = Drawing.Color.FromArgb(15, 23, 42),
                ForeColor = Drawing.Color.FromArgb(248, 250, 252)
            };
            _driveScopeMenu.DropDownItems.Add(item);
        }
    }

    private async Task OptimizeFromTrayAsync(OptimizationScope scope, string source)
    {
        try
        {
            OptimizationResult result = await _optimizer.OptimizeAsync(
                new OptimizationOptions(scope, MinimumWorkingSetThresholdMb: 20, ExcludeForegroundWindow: true, TriggerSource: source));

            double deltaMb = result.ReclaimedBytes / (1024.0 * 1024.0);
            string sign = deltaMb >= 0 ? "+" : "";
            ShowBalloon(
                "Memory Magic",
                $"Reclaimed {sign}{deltaMb:F1} MB ({result.Before.LoadPercent:F0}% -> {result.After.LoadPercent:F0}%).");
        }
        catch (Exception ex)
        {
            _logger.Warn("Tray", $"Tray optimization failed: {ex.Message}");
            ShowBalloon("Memory Magic", $"Optimization warning: {ex.Message}", Forms.ToolTipIcon.Warning);
        }
    }

    private Task CleanTempFromTrayAsync()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
            return Task.CompletedTask;

        dispatcher.BeginInvoke(() =>
        {
            ExecutionProgressDialog.RunWithDialog(
                Application.Current?.MainWindow?.IsVisible == true ? Application.Current.MainWindow : null,
                "Clean Temporary Files",
                "Cleaning default temporary locations...",
                async (reporter, _) =>
                {
                    reporter.SetProgress(-1, "Scanning temporary targets...");
                    IReadOnlyList<TempScanTarget> targets = await _tempCleaner.ScanTargetsAsync();
                    List<TempScanTarget> defaultTargets = targets.Where(t => t.IsSelectedByDefault && t.IsAvailable).ToList();

                    long bytes = 0;
                    int files = 0, skipped = 0;
                    for (int i = 0; i < defaultTargets.Count; i++)
                    {
                        TempScanTarget t = defaultTargets[i];
                        reporter.SetProgress(((i + 0.5) * 100.0) / Math.Max(1, defaultTargets.Count), $"Cleaning {t.DisplayName}...");
                        reporter.Log($"Cleaning {t.DisplayName} ({t.PathDescription})...");
                        TempCleanResult res = await _tempCleaner.CleanTargetsAsync([t]);
                        bytes += res.BytesDeleted;
                        files += res.FilesDeleted;
                        skipped += res.FilesSkippedLocked;
                        reporter.Log($"  -> Removed {res.FilesDeleted} files ({res.BytesDeleted / (1024.0 * 1024.0):F1} MB), skipped {res.FilesSkippedLocked} locked.");
                    }

                    string summary = $"Removed {files} files ({bytes / (1024.0 * 1024.0):F1} MB freed, {skipped} locked skipped).";
                    return new ExecutionOutcome(true, "Temporary Cleanup Complete", summary);
                });
        });

        return Task.CompletedTask;
    }

    private static string FormatRate(double bytesPerSec)
    {
        if (bytesPerSec >= 1024 * 1024)
            return $"{bytesPerSec / (1024 * 1024):F1} MB";
        if (bytesPerSec >= 1024)
            return $"{bytesPerSec / 1024:F0} KB";
        return $"{bytesPerSec:F0} B";
    }

    private static int LoadIntSetting(string name, int fallback)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RegSettingsPath, false);
            return key?.GetValue(name) is int v ? v : fallback;
        }
        catch { return fallback; }
    }

    private static string LoadStringSetting(string name, string fallback)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RegSettingsPath, false);
            return key?.GetValue(name) is string s && !string.IsNullOrWhiteSpace(s) ? s : fallback;
        }
        catch { return fallback; }
    }

    private static void SaveIntSetting(string name, int value)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RegSettingsPath, true);
            key.SetValue(name, value, RegistryValueKind.DWord);
        }
        catch { }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private static Drawing.Icon CreateCustomAppIcon()
    {
        using var bmp = new Drawing.Bitmap(32, 32);
        using (Drawing.Graphics g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);

            using var bgBrush = new Drawing2D.LinearGradientBrush(
                new Drawing.Rectangle(0, 0, 32, 32),
                Drawing.Color.FromArgb(37, 99, 235),
                Drawing.Color.FromArgb(14, 165, 233),
                45f);
            g.FillEllipse(bgBrush, 1, 1, 30, 30);

            using var borderPen = new Drawing.Pen(Drawing.Color.FromArgb(186, 230, 253), 1.5f);
            g.DrawEllipse(borderPen, 1.5f, 1.5f, 29f, 29f);

            using var font = new Drawing.Font("Segoe UI", 14f, Drawing.FontStyle.Bold, Drawing.GraphicsUnit.Pixel);
            using var sf = new Drawing.StringFormat
            {
                Alignment = Drawing.StringAlignment.Center,
                LineAlignment = Drawing.StringAlignment.Center
            };
            g.DrawString("M", font, Drawing.Brushes.White, new Drawing.RectangleF(0, 0, 32, 31), sf);
        }

        IntPtr hIcon = bmp.GetHicon();
        try
        {
            using var temp = Drawing.Icon.FromHandle(hIcon);
            return (Drawing.Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _contextMenu.Opening -= OnContextMenuOpening;
        _telemetryBar.StopAndClose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _contextMenu.Dispose();
        _trayIconGraphic.Dispose();
    }

    private sealed class DarkTrayRenderer : Forms.ToolStripProfessionalRenderer
    {
        private static readonly Drawing.Color BgDark = Drawing.Color.FromArgb(15, 23, 42);
        private static readonly Drawing.Color HoverBg = Drawing.Color.FromArgb(37, 99, 235);
        private static readonly Drawing.Color PressedBg = Drawing.Color.FromArgb(29, 78, 216);
        private static readonly Drawing.Color HoverBorder = Drawing.Color.FromArgb(96, 165, 250);
        private static readonly Drawing.Color MenuBorderColor = Drawing.Color.FromArgb(56, 189, 248);
        private static readonly Drawing.Color SeparatorColor = Drawing.Color.FromArgb(51, 65, 85);
        private static readonly Drawing.Color TextBright = Drawing.Color.FromArgb(248, 250, 252);
        private static readonly Drawing.Color TextDisabled = Drawing.Color.FromArgb(100, 116, 139);
        private static readonly Drawing.Color CheckAccent = Drawing.Color.FromArgb(56, 189, 248);

        public DarkTrayRenderer() : base(new DarkTrayColorTable())
        {
            RoundedEdges = false;
        }

        protected override void OnRenderToolStripBackground(Forms.ToolStripRenderEventArgs e)
        {
            using var brush = new Drawing.SolidBrush(BgDark);
            e.Graphics.FillRectangle(brush, e.AffectedBounds);
        }

        protected override void OnRenderImageMargin(Forms.ToolStripRenderEventArgs e)
        {
            using var brush = new Drawing.SolidBrush(BgDark);
            e.Graphics.FillRectangle(brush, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(Forms.ToolStripRenderEventArgs e)
        {
            using var pen = new Drawing.Pen(MenuBorderColor);
            var rect = new Drawing.Rectangle(0, 0, e.AffectedBounds.Width - 1, e.AffectedBounds.Height - 1);
            e.Graphics.DrawRectangle(pen, rect);
        }

        protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
        {
            var bounds = new Drawing.Rectangle(3, 1, e.Item.Width - 7, e.Item.Height - 2);
            if (!e.Item.Enabled)
            {
                using var disabledBg = new Drawing.SolidBrush(BgDark);
                e.Graphics.FillRectangle(disabledBg, bounds);
                return;
            }

            if (e.Item.Pressed)
            {
                using var fill = new Drawing.SolidBrush(PressedBg);
                using var border = new Drawing.Pen(MenuBorderColor);
                e.Graphics.FillRectangle(fill, bounds);
                e.Graphics.DrawRectangle(border, bounds);
            }
            else if (e.Item.Selected)
            {
                using var fill = new Drawing.SolidBrush(HoverBg);
                using var border = new Drawing.Pen(HoverBorder);
                e.Graphics.FillRectangle(fill, bounds);
                e.Graphics.DrawRectangle(border, bounds);
            }
            else
            {
                using var fill = new Drawing.SolidBrush(BgDark);
                e.Graphics.FillRectangle(fill, bounds);
            }
        }

        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            if (!e.Item.Enabled)
                e.TextColor = TextDisabled;
            else if (e.Item.Selected || e.Item.Pressed)
                e.TextColor = Drawing.Color.White;
            else
                e.TextColor = e.Item.ForeColor.IsEmpty || e.Item.ForeColor == Drawing.Color.Black
                    ? TextBright
                    : e.Item.ForeColor;

            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(Forms.ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = e.Item?.Selected == true ? Drawing.Color.White : TextBright;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemCheck(Forms.ToolStripItemImageRenderEventArgs e)
        {
            Drawing.Rectangle rect = e.ImageRectangle;
            using var bg = new Drawing.SolidBrush(Drawing.Color.FromArgb(30, 58, 138));
            using var pen = new Drawing.Pen(CheckAccent);
            e.Graphics.FillRectangle(bg, rect);
            e.Graphics.DrawRectangle(pen, rect);

            e.Graphics.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias;
            using var checkPen = new Drawing.Pen(Drawing.Color.White, 2f);
            e.Graphics.DrawLines(checkPen, new[]
            {
                new Drawing.Point(rect.Left + 3, rect.Top + rect.Height / 2),
                new Drawing.Point(rect.Left + rect.Width / 2 - 1, rect.Bottom - 4),
                new Drawing.Point(rect.Right - 3, rect.Top + 4)
            });
        }

        protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using var pen = new Drawing.Pen(SeparatorColor);
            e.Graphics.DrawLine(pen, 6, y, e.Item.Width - 7, y);
        }
    }

    private sealed class DarkTrayColorTable : Forms.ProfessionalColorTable
    {
        private static readonly Drawing.Color BgDark = Drawing.Color.FromArgb(15, 23, 42);
        private static readonly Drawing.Color HoverBg = Drawing.Color.FromArgb(37, 99, 235);
        private static readonly Drawing.Color PressedBg = Drawing.Color.FromArgb(29, 78, 216);

        public override Drawing.Color MenuBorder => Drawing.Color.FromArgb(56, 189, 248);
        public override Drawing.Color MenuItemBorder => Drawing.Color.FromArgb(96, 165, 250);
        public override Drawing.Color MenuItemSelected => HoverBg;
        public override Drawing.Color MenuItemSelectedGradientBegin => HoverBg;
        public override Drawing.Color MenuItemSelectedGradientEnd => HoverBg;
        public override Drawing.Color MenuItemPressedGradientBegin => PressedBg;
        public override Drawing.Color MenuItemPressedGradientMiddle => PressedBg;
        public override Drawing.Color MenuItemPressedGradientEnd => PressedBg;
        public override Drawing.Color CheckBackground => Drawing.Color.FromArgb(30, 58, 138);
        public override Drawing.Color CheckSelectedBackground => HoverBg;
        public override Drawing.Color CheckPressedBackground => PressedBg;
        public override Drawing.Color MenuStripGradientBegin => BgDark;
        public override Drawing.Color MenuStripGradientEnd => BgDark;
        public override Drawing.Color ToolStripDropDownBackground => BgDark;
        public override Drawing.Color ImageMarginGradientBegin => BgDark;
        public override Drawing.Color ImageMarginGradientMiddle => BgDark;
        public override Drawing.Color ImageMarginGradientEnd => BgDark;
        public override Drawing.Color SeparatorDark => Drawing.Color.FromArgb(51, 65, 85);
        public override Drawing.Color SeparatorLight => Drawing.Color.FromArgb(51, 65, 85);
    }
}
