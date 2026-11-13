using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using MemoryMagic.Models;
using MemoryMagic.Services;
using MemoryMagic.UI;

namespace MemoryMagic.ViewModels;

public sealed class TempTargetItem : ViewModelBase
{
    private bool _isSelected;

    public TempTargetItem(TempScanTarget model)
    {
        Model = model;
        _isSelected = model.IsSelectedByDefault;
    }

    public TempScanTarget Model { get; }
    public string DisplayName => Model.DisplayName;
    public string PathDescription => Model.PathDescription;
    public bool RequiresElevation => Model.RequiresElevation;
    public string EstimatedSizeDisplay => $"{Model.EstimatedBytes / (1024.0 * 1024.0):F1} MB ({Model.FileCount} files)";

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

public sealed class DebloatRowItem : ViewModelBase
{
    private bool _isSelected;

    public DebloatRowItem(DebloatCandidate model)
    {
        Model = model;
    }

    public DebloatCandidate Model { get; }
    public string DisplayName => Model.DisplayName;
    public DebloatItemKind Kind => Model.Kind;
    public DebloatSafetyLevel SafetyLevel => Model.SafetyLevel;
    public string Publisher => Model.Publisher;
    public string Version => Model.Version;
    public string SafetyReason => Model.SafetyReason;
    public bool CanSelect => Model.SafetyLevel != DebloatSafetyLevel.ProtectedCore;

    public string ActionButtonLabel => !CanSelect
        ? "Locked"
        : Model.Kind switch
        {
            DebloatItemKind.SystemTweak => "Apply Tweak",
            DebloatItemKind.AppxPackage or DebloatItemKind.Win32Program => "Uninstall",
            _ => "Remove"
        };

    public string CategoryGroup => Model.Kind switch
    {
        DebloatItemKind.AppxPackage => "1. Bloatware Apps (UWP / Appx)",
        DebloatItemKind.WindowsCapability or DebloatItemKind.OptionalFeature => "2. Windows Optional Capabilities & Features (DISM)",
        DebloatItemKind.SystemTweak => "3. Performance & Privacy Registry Tweaks",
        DebloatItemKind.Win32Program => "4. Installed Desktop Programs (Win32 / MSI)",
        _ => "Other"
    };

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (CanSelect)
                SetProperty(ref _isSelected, value);
        }
    }
}

public sealed class ProcessRowItem
{
    public ProcessRowItem(ProcessMemorySample s)
    {
        Model = s;
    }

    public ProcessMemorySample Model { get; }
    public int ProcessId => Model.ProcessId;
    public string Name => Model.Name;
    public string PrivateWsMb => $"{Model.PrivateWorkingSetBytes / (1024.0 * 1024.0):F1} MB";
    public string SharedWsMb => $"{Model.SharedWorkingSetBytes / (1024.0 * 1024.0):F1} MB";
    public string TotalWsMb => $"{Model.TotalWorkingSetBytes / (1024.0 * 1024.0):F1} MB";
    public string CommitMb => $"{Model.CommitBytes / (1024.0 * 1024.0):F1} MB";
    public string PagedPoolKb => $"{Model.PagedPoolBytes / 1024.0:F0} KB";
    public string NonPagedPoolKb => $"{Model.NonPagedPoolBytes / 1024.0:F0} KB";
    public uint ThreadCount => Model.ThreadCount;
    public bool IsCritical => Model.IsCritical;
    public bool IsHighConsumption => Model.IsHighConsumption;
    public string SafetyTag => Model.IsCritical ? "CRITICAL" : (Model.IsProtectedSystem ? "PROTECTED" : (Model.IsHighConsumption ? "HIGH RAM" : "Normal"));
}

public sealed class MainViewModel : ViewModelBase, IAsyncDisposable
{
    private const string RegSettingsPath = @"SOFTWARE\MemoryMagic";
    private const string RegCloseBehavior = "CloseBehavior";
    private const string RegRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RegRunValueName = "MemoryMagic";

    private readonly AuditLogger _logger;
    private readonly PrivilegeService _privileges;
    private readonly MemoryOptimizerService _optimizer;
    private readonly BackgroundMonitorService _monitor;
    private readonly ProcessProfilerService _profiler;
    private readonly StartupManagerService _startupManager;
    private readonly TempCleanerService _tempCleaner;
    private readonly WindowsDebloaterService _debloater;
    private readonly SystemTelemetryService _telemetry;
    private readonly TrayIconService _trayIcon;

    private MemorySnapshot? _snapshot;
    private string _statusMessage = "Ready.";
    private string _lastOptimizationSummary = "No optimization run yet.";
    private string _processFilter = string.Empty;
    private string _debloatFilter = string.Empty;
    private string _selectedDebloatCategory = "All";
    private bool _showProtectedDebloatItems;
    private string _selectedCloseBehavior;
    private bool _runAtWindowsStartup;
    private bool _autoOptimizeEnabled;
    private double _autoThresholdPercent = 85;
    private int _autoIntervalMinutes = 30;
    private bool _deepPurgeAuto;
    private ProcessRowItem? _selectedProcess;
    private StartupEntry? _selectedStartupEntry;
    private DebloatRowItem? _selectedDebloatItem;
    private IReadOnlyList<ProcessMemorySample> _allProcesses = [];
    private IReadOnlyList<DebloatRowItem> _allDebloatItems = [];

    public MainViewModel(Action restoreWindowAction, Action minimizeToTrayAction, Action exitAppAction)
    {
        _logger = new AuditLogger();
        _privileges = new PrivilegeService(_logger);
        _optimizer = new MemoryOptimizerService(_privileges, _logger);
        _monitor = new BackgroundMonitorService(_optimizer, _logger);
        _profiler = new ProcessProfilerService(_privileges, _logger);
        _startupManager = new StartupManagerService(_privileges, _logger);
        _tempCleaner = new TempCleanerService(_privileges, _logger);
        _debloater = new WindowsDebloaterService(_privileges, _logger);
        _telemetry = new SystemTelemetryService(_optimizer);
        _trayIcon = new TrayIconService(_telemetry, _optimizer, _tempCleaner, _logger, restoreWindowAction, exitAppAction);

        Processes = new ObservableCollection<ProcessRowItem>();
        StartupEntries = new ObservableCollection<StartupEntry>();
        TempTargets = new ObservableCollection<TempTargetItem>();
        DebloatItems = new ObservableCollection<DebloatRowItem>();
        DebloatItemsView = CollectionViewSource.GetDefaultView(DebloatItems);
        DebloatItemsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(DebloatRowItem.CategoryGroup)));
        AuditLogs = new ObservableCollection<AuditLogEntry>();
        DebloatCategories = ["All", "UWP / Appx Apps", "Installed Programs (Win32)", "Capabilities & Features", "System & Telemetry Tweaks"];
        CloseBehaviorOptions = ["Ask Every Time", "Run in Background (Tray)", "Exit Completely"];
        _selectedCloseBehavior = LoadCloseBehaviorOption();
        _runAtWindowsStartup = IsWindowsStartupEnabled();

        OptimizeSafeCommand = new AsyncRelayCommand(_ => RunOptimizeAsync(OptimizationScope.SafeStandard, "Manual-Safe"));
        OptimizeDeepCommand = new AsyncRelayCommand(_ => RunOptimizeAsync(OptimizationScope.DeepPurge, "Manual-Deep"));
        RefreshProcessesCommand = new AsyncRelayCommand(_ => RefreshProcessesAsync());
        TrimSelectedProcessCommand = new RelayCommand(_ => TrimSelectedProcess(), _ => SelectedProcess is not null);
        RefreshStartupCommand = new AsyncRelayCommand(_ => RefreshStartupAsync());
        ToggleStartupCommand = new RelayCommand(_ => ToggleSelectedStartup(), _ => SelectedStartupEntry is not null);
        ScanTempCommand = new AsyncRelayCommand(_ => ScanTempAsync());
        CleanTempCommand = new AsyncRelayCommand(_ => CleanTempAsync());
        ScanDebloatCommand = new AsyncRelayCommand(_ => ScanDebloatAsync());
        SelectSafeBloatCommand = new RelayCommand(_ => SelectAllSafeDebloat());
        ClearDebloatSelectionCommand = new RelayCommand(_ => ClearDebloatSelection());
        CreateRestorePointCommand = new AsyncRelayCommand(_ => CreateRestorePointManualAsync());
        ApplyDebloatBatchCommand = new AsyncRelayCommand(_ => ApplyDebloatBatchAsync());
        ExecuteSingleDebloatCommand = new AsyncRelayCommand(
            p => ExecuteSingleDebloatAsync(p as DebloatRowItem),
            p => p is DebloatRowItem item && item.CanSelect);
        MinimizeToTrayCommand = new RelayCommand(_ => minimizeToTrayAction());
        ElevateCommand = new RelayCommand(_ => Elevate(), _ => !IsElevated);
        OpenUserGuideCommand = new RelayCommand(_ => OpenUserGuide());

        _monitor.SnapshotUpdated += OnSnapshotUpdated;
        _monitor.AutoOptimizationCompleted += OnOptimizationCompleted;
        _logger.EntryLogged += OnAuditEntryLogged;
        _trayIcon.TelemetryBarStateChanged += OnTelemetryBarStateChanged;

        _monitor.Start();
        _ = InitializeListsAsync();
    }

    public ObservableCollection<ProcessRowItem> Processes { get; }
    public ObservableCollection<StartupEntry> StartupEntries { get; }
    public ObservableCollection<TempTargetItem> TempTargets { get; }
    public ObservableCollection<DebloatRowItem> DebloatItems { get; }
    public ICollectionView DebloatItemsView { get; }
    public ObservableCollection<AuditLogEntry> AuditLogs { get; }
    public IReadOnlyList<string> DebloatCategories { get; }
    public IReadOnlyList<string> CloseBehaviorOptions { get; }

    public string WindowTitle
    {
        get
        {
            string? info = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;
            int plus = info?.IndexOf('+') ?? -1;
            string buildTag = plus >= 0 && info is not null ? info[(plus + 1)..] : DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            return $"Memory Magic [v2 - LIVE | Build: {buildTag}]";
        }
    }

    public ICommand OptimizeSafeCommand { get; }
    public ICommand OptimizeDeepCommand { get; }
    public ICommand RefreshProcessesCommand { get; }
    public ICommand TrimSelectedProcessCommand { get; }
    public ICommand RefreshStartupCommand { get; }
    public ICommand ToggleStartupCommand { get; }
    public ICommand ScanTempCommand { get; }
    public ICommand CleanTempCommand { get; }
    public ICommand ScanDebloatCommand { get; }
    public ICommand SelectSafeBloatCommand { get; }
    public ICommand ClearDebloatSelectionCommand { get; }
    public ICommand CreateRestorePointCommand { get; }
    public ICommand ApplyDebloatBatchCommand { get; }
    public ICommand ExecuteSingleDebloatCommand { get; }
    public ICommand MinimizeToTrayCommand { get; }
    public ICommand ElevateCommand { get; }

    public ImageSource WindowIcon => _trayIcon.CreateWpfWindowIcon();

    public bool IsElevated => _privileges.IsElevated();
    public string PrivilegeStatusText => IsElevated
        ? "Running as Administrator (Full Kernel Standby, HKLM, DISM & Policy Tweaks Access)"
        : "Standard User Mode (Standby Purge, HKLM, DISM & Policy Tweaks Require Elevation)";

    public double RamLoadPercent => _snapshot?.LoadPercent ?? 0;
    public string PhysicalRamSummary => _snapshot is null
        ? "Sampling..."
        : $"{ToGb(_snapshot.UsedPhysicalBytes):F2} GB / {ToGb(_snapshot.TotalPhysicalBytes):F2} GB Used ({ToGb(_snapshot.AvailablePhysicalBytes):F2} GB Available)";

    public string KernelDetailsSummary => _snapshot is null
        ? string.Empty
        : $"Commit: {ToGb(_snapshot.CommitTotalBytes):F2}/{ToGb(_snapshot.CommitLimitBytes):F2} GB | " +
          $"System Cache: {ToMb(_snapshot.SystemCacheBytes):F0} MB | " +
          $"Kernel Paged/Non-Paged: {ToMb(_snapshot.KernelPagedBytes):F0}/{ToMb(_snapshot.KernelNonPagedBytes):F0} MB | " +
          $"Processes: {_snapshot.ProcessCount}";

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string LastOptimizationSummary
    {
        get => _lastOptimizationSummary;
        private set => SetProperty(ref _lastOptimizationSummary, value);
    }

    public string ProcessFilter
    {
        get => _processFilter;
        set
        {
            if (SetProperty(ref _processFilter, value))
                ApplyProcessFilter();
        }
    }

    public string DebloatFilter
    {
        get => _debloatFilter;
        set
        {
            if (SetProperty(ref _debloatFilter, value))
                ApplyDebloatFilter();
        }
    }

    public string SelectedDebloatCategory
    {
        get => _selectedDebloatCategory;
        set
        {
            if (SetProperty(ref _selectedDebloatCategory, value))
                ApplyDebloatFilter();
        }
    }

    public bool ShowProtectedDebloatItems
    {
        get => _showProtectedDebloatItems;
        set
        {
            if (SetProperty(ref _showProtectedDebloatItems, value))
                _ = ScanDebloatAsync();
        }
    }

    public bool PerformanceBarVisible
    {
        get => _trayIcon.IsTelemetryBarVisible;
        set
        {
            if (_trayIcon.IsTelemetryBarVisible != value)
            {
                _trayIcon.IsTelemetryBarVisible = value;
                OnPropertyChanged();
            }
        }
    }

    public bool PerformanceBarAlwaysOnTop
    {
        get => _trayIcon.IsTelemetryBarAlwaysOnTop;
        set
        {
            if (_trayIcon.IsTelemetryBarAlwaysOnTop != value)
            {
                _trayIcon.IsTelemetryBarAlwaysOnTop = value;
                OnPropertyChanged();
            }
        }
    }

    public bool PerformanceBarPositionLocked
    {
        get => _trayIcon.IsTelemetryBarPositionLocked;
        set
        {
            if (_trayIcon.IsTelemetryBarPositionLocked != value)
            {
                _trayIcon.IsTelemetryBarPositionLocked = value;
                OnPropertyChanged();
            }
        }
    }

    public string SelectedCloseBehavior
    {
        get => _selectedCloseBehavior;
        set
        {
            if (SetProperty(ref _selectedCloseBehavior, value))
                SaveCloseBehaviorOption(value);
        }
    }

    public bool RunAtWindowsStartup
    {
        get => _runAtWindowsStartup;
        set
        {
            if (SetProperty(ref _runAtWindowsStartup, value))
                SetWindowsStartupEnabled(value);
        }
    }

    public ICommand OpenUserGuideCommand { get; }

    private static string LoadCloseBehaviorOption()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RegSettingsPath, false);
            return key?.GetValue(RegCloseBehavior)?.ToString() switch
            {
                "Run in Background (Tray)" => "Run in Background (Tray)",
                "Exit Completely" => "Exit Completely",
                _ => "Ask Every Time"
            };
        }
        catch
        {
            return "Ask Every Time";
        }
    }

    private static void SaveCloseBehaviorOption(string value)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RegSettingsPath, true);
            key.SetValue(RegCloseBehavior, value, RegistryValueKind.String);
        }
        catch { }
    }

    public bool AutoOptimizeEnabled
    {
        get => _autoOptimizeEnabled;
        set
        {
            if (SetProperty(ref _autoOptimizeEnabled, value))
                PushMonitorConfig();
        }
    }

    public double AutoThresholdPercent
    {
        get => _autoThresholdPercent;
        set
        {
            if (SetProperty(ref _autoThresholdPercent, value))
                PushMonitorConfig();
        }
    }

    public int AutoIntervalMinutes
    {
        get => _autoIntervalMinutes;
        set
        {
            if (SetProperty(ref _autoIntervalMinutes, value))
                PushMonitorConfig();
        }
    }

    public bool DeepPurgeAuto
    {
        get => _deepPurgeAuto;
        set
        {
            if (SetProperty(ref _deepPurgeAuto, value))
                PushMonitorConfig();
        }
    }

    public ProcessRowItem? SelectedProcess
    {
        get => _selectedProcess;
        set => SetProperty(ref _selectedProcess, value);
    }

    public StartupEntry? SelectedStartupEntry
    {
        get => _selectedStartupEntry;
        set => SetProperty(ref _selectedStartupEntry, value);
    }

    public DebloatRowItem? SelectedDebloatItem
    {
        get => _selectedDebloatItem;
        set => SetProperty(ref _selectedDebloatItem, value);
    }

    public void NotifyMinimizedToTray()
    {
        _trayIcon.ShowBalloon(
            "Memory Magic",
            "Running in the System Tray. Right-click the icon for live CPU/RAM/Disk telemetry or quick actions.");
    }

    private async Task InitializeListsAsync()
    {
        await RefreshProcessesAsync();
        await RefreshStartupAsync();
        await ScanTempAsync();
        await ScanDebloatAsync();
    }

    private void PushMonitorConfig()
    {
        _monitor.UpdateConfiguration(new MonitorConfiguration(
            IsEnabled: _autoOptimizeEnabled,
            RamThresholdPercent: _autoThresholdPercent,
            PeriodicIntervalMinutes: _autoIntervalMinutes,
            Scope: _deepPurgeAuto ? OptimizationScope.DeepPurge : OptimizationScope.SafeStandard));
    }

    private async Task RunOptimizeAsync(OptimizationScope scope, string source)
    {
        StatusMessage = $"Running {source} memory optimization...";
        OptimizationResult result = await _optimizer.OptimizeAsync(
            new OptimizationOptions(scope, MinimumWorkingSetThresholdMb: 20, ExcludeForegroundWindow: true, TriggerSource: source));
        OnOptimizationCompleted(result);
        await RefreshProcessesAsync();
    }

    private async Task RefreshProcessesAsync()
    {
        _allProcesses = await Task.Run(() => _profiler.GetProcesses());
        RunOnUi(ApplyProcessFilter);
    }

    private void ApplyProcessFilter()
    {
        Processes.Clear();
        string filter = _processFilter.Trim();

        foreach (ProcessMemorySample sample in _allProcesses)
        {
            if (filter.Length > 0 &&
                !sample.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                !sample.ProcessId.ToString().Contains(filter, StringComparison.Ordinal))
            {
                continue;
            }

            Processes.Add(new ProcessRowItem(sample));
        }
    }

    private void TrimSelectedProcess()
    {
        if (SelectedProcess is null)
            return;

        _profiler.TryTrimProcess(SelectedProcess.ProcessId, SelectedProcess.Name, out string msg);
        StatusMessage = msg;
        _ = RefreshProcessesAsync();
    }

    private async Task RefreshStartupAsync()
    {
        IReadOnlyList<StartupEntry> entries = await Task.Run(() => _startupManager.GetStartupEntries());
        RunOnUi(() =>
        {
            StartupEntries.Clear();
            foreach (StartupEntry entry in entries)
                StartupEntries.Add(entry);
        });
    }

    private void ToggleSelectedStartup()
    {
        if (SelectedStartupEntry is null)
            return;

        StartupEntry entry = SelectedStartupEntry;
        bool enable = !entry.IsEnabled;
        string actionWord = enable ? "Enable" : "Disable";

        ExecutionOutcome outcome = ExecutionProgressDialog.RunWithDialog(
            Application.Current?.MainWindow,
            $"{actionWord} Startup Entry",
            $"Modifying {entry.Source}: {entry.Name}",
            async (reporter, _) =>
            {
                reporter.SetProgress(20, $"{actionWord}ing '{entry.Name}'...");
                reporter.Log($"Target: {entry.Name} ({entry.Location})");

                (bool ok, string msg) = await Task.Run(() =>
                {
                    bool succeeded = _startupManager.TrySetEnabled(entry, enable, out string m);
                    return (succeeded, m);
                });

                reporter.Log(msg);
                reporter.SetProgress(100, ok ? "Completed." : "Failed.");

                return ok
                    ? new ExecutionOutcome(true, $"{actionWord}d '{entry.Name}'", msg)
                    : new ExecutionOutcome(false, $"Could not {actionWord.ToLowerInvariant()} '{entry.Name}'", msg);
            });

        StatusMessage = outcome.Details;
        _ = RefreshStartupAsync();
    }

    private async Task ScanTempAsync()
    {
        StatusMessage = "Scanning temporary locations...";
        IReadOnlyList<TempScanTarget> targets = await _tempCleaner.ScanTargetsAsync();
        RunOnUi(() =>
        {
            TempTargets.Clear();
            foreach (TempScanTarget t in targets)
                TempTargets.Add(new TempTargetItem(t));
            StatusMessage = "Temp scan complete.";
        });
    }

    private async Task CleanTempAsync()
    {
        List<TempScanTarget> selected = TempTargets.Where(t => t.IsSelected).Select(t => t.Model).ToList();
        if (selected.Count == 0)
        {
            StatusMessage = "Select at least one temporary target to clean.";
            return;
        }

        ExecutionOutcome outcome = ExecutionProgressDialog.RunWithDialog(
            Application.Current?.MainWindow,
            "Clean Temporary Files",
            $"Cleaning {selected.Count} selected temporary location(s)...",
            async (reporter, _) =>
            {
                int done = 0;
                long totalDeletedBytes = 0;
                int totalDeletedFiles = 0;
                int totalSkipped = 0;
                var notes = new List<string>();

                foreach (TempScanTarget target in selected)
                {
                    done++;
                    double pct = (done * 100.0) / selected.Count;
                    reporter.SetProgress(pct - (50.0 / selected.Count), $"Cleaning {target.DisplayName}...");
                    reporter.Log($"Cleaning target: {target.DisplayName} ({target.PathDescription})");

                    TempCleanResult res = await _tempCleaner.CleanTargetsAsync([target]);
                    totalDeletedBytes += res.BytesDeleted;
                    totalDeletedFiles += res.FilesDeleted;
                    totalSkipped += res.FilesSkippedLocked;
                    notes.AddRange(res.Notes);

                    reporter.Log($"  -> Removed {res.FilesDeleted} files ({res.BytesDeleted / (1024.0 * 1024.0):F1} MB), skipped {res.FilesSkippedLocked} locked.");
                    foreach (string n in res.Notes)
                        reporter.Log($"  -> {n}");

                    reporter.SetProgress(pct, $"Completed {target.DisplayName}");
                }

                string summary = $"Cleaned {totalDeletedFiles} files ({totalDeletedBytes / (1024.0 * 1024.0):F1} MB freed), skipped {totalSkipped} locked file(s).";
                return new ExecutionOutcome(true, "Temporary Files Cleaned", summary);
            });

        StatusMessage = outcome.Details;
        await ScanTempAsync();
    }

    private async Task ScanDebloatAsync()
    {
        StatusMessage = "Scanning Windows apps, DISM capabilities, features, and system tweaks...";
        (IReadOnlyList<DebloatCandidate> items, string? note) = await _debloater.ScanAsync(_showProtectedDebloatItems);
        _allDebloatItems = items.Select(c => new DebloatRowItem(c)).ToList();
        RunOnUi(() =>
        {
            ApplyDebloatFilter();
            StatusMessage = note ?? $"Found {items.Count} Windows debloat & optimization entries.";
        });
    }

    private void ApplyDebloatFilter()
    {
        DebloatItems.Clear();
        string filter = _debloatFilter.Trim();

        foreach (DebloatRowItem row in _allDebloatItems)
        {
            bool categoryMatch = _selectedDebloatCategory switch
            {
                "UWP / Appx Apps" => row.Kind == DebloatItemKind.AppxPackage,
                "Installed Programs (Win32)" => row.Kind == DebloatItemKind.Win32Program,
                "Capabilities & Features" => row.Kind is DebloatItemKind.WindowsCapability or DebloatItemKind.OptionalFeature,
                "System & Telemetry Tweaks" => row.Kind == DebloatItemKind.SystemTweak,
                _ => true
            };

            if (!categoryMatch)
                continue;

            if (filter.Length > 0 &&
                !row.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                !row.Model.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                !row.Publisher.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            DebloatItems.Add(row);
        }
    }

    private void SelectAllSafeDebloat()
    {
        foreach (DebloatRowItem row in DebloatItems)
        {
            if (row.SafetyLevel == DebloatSafetyLevel.SafeToRemove && !row.Model.IsCurrentlyApplied)
                row.IsSelected = true;
        }
        StatusMessage = $"Selected {DebloatItems.Count(r => r.IsSelected)} safe bloatware/tweak items.";
    }

    private void ClearDebloatSelection()
    {
        foreach (DebloatRowItem row in _allDebloatItems)
            row.IsSelected = false;
        StatusMessage = "Cleared debloater selection.";
    }

    private Task CreateRestorePointManualAsync()
    {
        ExecutionOutcome outcome = ExecutionProgressDialog.RunWithDialog(
            Application.Current?.MainWindow,
            "Create Windows System Restore Point",
            "Creating a system protection checkpoint before system modifications...",
            async (reporter, ct) =>
            {
                reporter.SetProgress(-1, "Running Checkpoint-Computer...");
                DebloatResult res = await _debloater.CreateRestorePointAsync(
                    "MemoryMagic Manual Checkpoint",
                    reporter.Log,
                    ct);
                return res.Succeeded
                    ? new ExecutionOutcome(true, "Restore Point Created", res.Message)
                    : new ExecutionOutcome(false, "Restore Point Failed", res.Message);
            });

        StatusMessage = outcome.Details;
        return Task.CompletedTask;
    }

    private Task ExecuteSingleDebloatAsync(DebloatRowItem? row)
    {
        if (row is null || !row.CanSelect)
            return Task.CompletedTask;

        return ExecuteDebloatItemsWithDialogAsync([row.Model]);
    }

    private Task ApplyDebloatBatchAsync()
    {
        List<DebloatCandidate> selected = _allDebloatItems
            .Where(r => r.IsSelected)
            .Select(r => r.Model)
            .ToList();

        if (selected.Count == 0 && SelectedDebloatItem is not null && SelectedDebloatItem.CanSelect)
            selected.Add(SelectedDebloatItem.Model);

        if (selected.Count == 0)
        {
            StatusMessage = "Check one or more items (or select a non-protected row) to apply.";
            return Task.CompletedTask;
        }

        return ExecuteDebloatItemsWithDialogAsync(selected);
    }

    private async Task ExecuteDebloatItemsWithDialogAsync(List<DebloatCandidate> selected)
    {
        if (!IsElevated && selected.Any(s => s.RequiresElevation))
        {
            MessageBoxResult uacPrompt = MessageBox.Show(
                "One or more selected items (DISM capabilities, Optional Features, Win32 HKLM programs, or policies) require Administrator privileges.\n\n" +
                "• Yes = Restart Memory Magic as Administrator (UAC)\n" +
                "• No = Continue (Admin-only items will fail/skip with clear error reporting)\n" +
                "• Cancel = Abort",
                "Administrator Elevation Recommended",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question,
                MessageBoxResult.Yes);

            if (uacPrompt == MessageBoxResult.Cancel)
                return;
            if (uacPrompt == MessageBoxResult.Yes)
            {
                Elevate();
                return;
            }
        }

        bool hasRestoreEligibleAction = selected.Any(s => s.Kind != DebloatItemKind.Win32Program || selected.Count > 1);
        bool createRestorePoint = false;

        if (hasRestoreEligibleAction)
        {
            MessageBoxResult restoreChoice = MessageBox.Show(
                $"You are about to execute {selected.Count} Windows debloat / uninstall action(s):\n" +
                string.Join(Environment.NewLine, selected.Take(5).Select(s => $"  • {s.DisplayName}")) +
                (selected.Count > 5 ? $"{Environment.NewLine}  ...and {selected.Count - 5} more" : string.Empty) +
                "\n\nCreate a System Restore Point before proceeding?\n" +
                "• Yes = Create Restore Point & Proceed\n" +
                "• No = Proceed Directly with Live Progress Dialog\n" +
                "• Cancel = Abort",
                "Confirm System / App Modification",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning,
                MessageBoxResult.Yes);

            if (restoreChoice == MessageBoxResult.Cancel)
            {
                StatusMessage = "Action canceled by user.";
                return;
            }

            createRestorePoint = restoreChoice == MessageBoxResult.Yes;
        }

        string title = selected.Count == 1
            ? $"Executing: {selected[0].DisplayName}"
            : $"Executing {selected.Count} Debloat / Uninstall Tasks";
        string subtitle = selected.Count == 1
            ? $"{selected[0].Kind} — live stdout & exit status"
            : "Live step-by-step progress & stdout stream";

        ExecutionOutcome outcome = ExecutionProgressDialog.RunWithDialog(
            Application.Current?.MainWindow,
            title,
            subtitle,
            async (reporter, ct) =>
            {
                if (createRestorePoint)
                {
                    reporter.SetProgress(-1, "Creating pre-execution System Restore Point...");
                    DebloatResult rp = await _debloater.CreateRestorePointAsync("MemoryMagic Pre-Debloat Backup", reporter.Log, ct);
                    if (!rp.Succeeded)
                    {
                        reporter.Log($"Restore point warning: {rp.Message}");
                        return new ExecutionOutcome(
                            false,
                            "System Restore Point creation failed (execution halted for safety)",
                            rp.Message);
                    }
                    reporter.Log(rp.Message);
                }

                BatchDebloatSummary summary = await _debloater.ExecuteBatchAsync(
                    selected,
                    onStepProgress: null,
                    onOutput: reporter.Log,
                    ct: ct,
                    onProgress: (pct, stepMsg) => reporter.SetProgress(pct, stepMsg));

                if (summary.FailedCount == 0 && summary.SucceededCount > 0)
                {
                    reporter.SetProgress(100, "Finished.");
                    string headline = selected.Count == 1
                        ? selected[0].DisplayName
                        : $"{summary.SucceededCount} action(s) completed";
                    return new ExecutionOutcome(true, headline, string.Join(Environment.NewLine, summary.Messages.Take(6)));
                }

                string failHeadline = selected.Count == 1
                    ? selected[0].DisplayName
                    : $"{summary.FailedCount} of {selected.Count} action(s) failed ({summary.SucceededCount} succeeded)";
                string failDetails = string.Join(Environment.NewLine, summary.Messages.Take(8));
                return new ExecutionOutcome(false, failHeadline, failDetails);
            });

        StatusMessage = outcome.Details;
        await ScanDebloatAsync();
    }

    private void Elevate()
    {
        if (_privileges.RestartElevated())
        {
            _trayIcon.Dispose();
            Application.Current?.Shutdown();
        }
    }

    private void OnSnapshotUpdated(MemorySnapshot snapshot)
    {
        RunOnUi(() =>
        {
            _snapshot = snapshot;
            _trayIcon.UpdateTooltip(snapshot);
            OnPropertyChanged(nameof(RamLoadPercent));
            OnPropertyChanged(nameof(PhysicalRamSummary));
            OnPropertyChanged(nameof(KernelDetailsSummary));
        });
    }

    private void OnOptimizationCompleted(OptimizationResult result)
    {
        RunOnUi(() =>
        {
            double deltaMb = result.ReclaimedBytes / (1024.0 * 1024.0);
            string sign = deltaMb >= 0 ? "+" : "";
            LastOptimizationSummary =
                $"[{result.ExecutedAt:HH:mm:ss}] {result.TriggerSource}: {sign}{deltaMb:F1} MB available | " +
                $"Trimmed {result.ProcessesTrimmed} procs | Standby={result.StandbyPurged}";
            StatusMessage = result.Warnings.Count > 0 ? result.Warnings[0] : LastOptimizationSummary;
        });
    }

    private void OnAuditEntryLogged(AuditLogEntry entry)
    {
        RunOnUi(() =>
        {
            AuditLogs.Insert(0, entry);
            while (AuditLogs.Count > 150)
                AuditLogs.RemoveAt(AuditLogs.Count - 1);
        });
    }

    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.BeginInvoke(action);
    }

    private static double ToGb(ulong bytes) => bytes / (1024.0 * 1024.0 * 1024.0);
    private static double ToMb(ulong bytes) => bytes / (1024.0 * 1024.0);

    private void OnTelemetryBarStateChanged()
    {
        RunOnUi(() =>
        {
            OnPropertyChanged(nameof(PerformanceBarVisible));
            OnPropertyChanged(nameof(PerformanceBarAlwaysOnTop));
            OnPropertyChanged(nameof(PerformanceBarPositionLocked));
        });
    }

    public async ValueTask DisposeAsync()
    {
        _monitor.SnapshotUpdated -= OnSnapshotUpdated;
        _monitor.AutoOptimizationCompleted -= OnOptimizationCompleted;
        _logger.EntryLogged -= OnAuditEntryLogged;
        _trayIcon.TelemetryBarStateChanged -= OnTelemetryBarStateChanged;
        _trayIcon.Dispose();
        await _monitor.DisposeAsync().ConfigureAwait(false);
    }

    private static bool IsWindowsStartupEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RegRunKeyPath, false);
            return key?.GetValue(RegRunValueName) is string val && !string.IsNullOrWhiteSpace(val);
        }
        catch
        {
            return false;
        }
    }

    private void SetWindowsStartupEnabled(bool enabled)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RegRunKeyPath, true);
            if (enabled)
            {
                string? exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(exePath))
                    throw new InvalidOperationException("Unable to resolve MemoryMagic executable path.");

                string command = $"\"{exePath}\" --minimized";
                key.SetValue(RegRunValueName, command, RegistryValueKind.String);
                _logger.Info("Startup", $"Enabled Windows startup entry: {command}");
                StatusMessage = "Windows startup enabled (launches minimized to tray).";
            }
            else
            {
                key.DeleteValue(RegRunValueName, throwOnMissingValue: false);
                _logger.Info("Startup", "Disabled Windows startup entry.");
                StatusMessage = "Windows startup disabled.";
            }
        }
        catch (Exception ex)
        {
            _logger.Warn("Startup", $"Failed to update Windows startup setting: {ex.Message}");
            StatusMessage = $"Startup update failed: {ex.Message}";
        }
    }

    private void OpenUserGuide()
    {
        try
        {
            string localGuide = Path.Combine(AppContext.BaseDirectory, "UserGuide.html");
            string guidePath = localGuide;

            if (!File.Exists(guidePath))
            {
                string fallbackDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MemoryMagic");
                Directory.CreateDirectory(fallbackDir);
                guidePath = Path.Combine(fallbackDir, "UserGuide.html");

                using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MemoryMagic.UserGuide.html");
                if (stream is null)
                    throw new FileNotFoundException("UserGuide.html could not be found.");

                using var fs = new FileStream(guidePath, FileMode.Create, FileAccess.Write, FileShare.None);
                stream.CopyTo(fs);
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = guidePath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _logger.Warn("Help", $"Unable to open User Guide: {ex.Message}");
            MessageBox.Show(
                $"Unable to open User Guide:\n\n{ex.Message}",
                "Memory Magic — User Guide",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }
}
