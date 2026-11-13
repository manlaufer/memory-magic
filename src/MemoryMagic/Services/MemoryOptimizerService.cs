using System.Runtime.InteropServices;
using MemoryMagic.Models;
using MemoryMagic.Native;

namespace MemoryMagic.Services;

/// <summary>
/// Core Memory Optimizer responsible for non-destructive working set trimming
/// and kernel memory list flushing (Standby, Modified, File Cache) with safety guardrails.
/// </summary>
public sealed class MemoryOptimizerService
{
    private readonly PrivilegeService _privileges;
    private readonly AuditLogger _logger;
    private readonly SemaphoreSlim _optimizeGate = new(1, 1);

    public MemoryOptimizerService(PrivilegeService privileges, AuditLogger logger)
    {
        _privileges = privileges;
        _logger = logger;
    }

    /// <summary>
    /// Captures a real-time physical, commit, and kernel pool memory snapshot using Win32 APIs.
    /// </summary>
    public MemorySnapshot GetSnapshot()
    {
        var memStatus = new Win32Interop.MEMORYSTATUSEX
        {
            dwLength = (uint)Marshal.SizeOf<Win32Interop.MEMORYSTATUSEX>()
        };

        if (!Win32Interop.GlobalMemoryStatusEx(ref memStatus))
            throw new InvalidOperationException($"GlobalMemoryStatusEx failed ({Marshal.GetLastWin32Error()}).");

        Win32Interop.GetPerformanceInfo(
            out var perf,
            (uint)Marshal.SizeOf<Win32Interop.PERFORMANCE_INFORMATION>());

        ulong pageSize = (ulong)perf.PageSize;
        ulong usedPhys = memStatus.ullTotalPhys - memStatus.ullAvailPhys;

        return new MemorySnapshot(
            Timestamp: DateTimeOffset.Now,
            TotalPhysicalBytes: memStatus.ullTotalPhys,
            AvailablePhysicalBytes: memStatus.ullAvailPhys,
            UsedPhysicalBytes: usedPhys,
            LoadPercent: memStatus.dwMemoryLoad,
            CommitTotalBytes: (ulong)perf.CommitTotal * pageSize,
            CommitLimitBytes: (ulong)perf.CommitLimit * pageSize,
            SystemCacheBytes: (ulong)perf.SystemCache * pageSize,
            KernelPagedBytes: (ulong)perf.KernelPaged * pageSize,
            KernelNonPagedBytes: (ulong)perf.KernelNonpaged * pageSize,
            ProcessCount: perf.ProcessCount,
            ThreadCount: perf.ThreadCount,
            HandleCount: perf.HandleCount);
    }

    /// <summary>
    /// Executes safe, non-destructive memory optimization according to the supplied options.
    /// Serialized via SemaphoreSlim so concurrent manual and background triggers cannot collide.
    /// </summary>
    public async Task<OptimizationResult> OptimizeAsync(
        OptimizationOptions options,
        CancellationToken cancellationToken = default)
    {
        await _optimizeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => OptimizeCore(options, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _optimizeGate.Release();
        }
    }

    private OptimizationResult OptimizeCore(OptimizationOptions options, CancellationToken ct)
    {
        MemorySnapshot before = GetSnapshot();
        var warnings = new List<string>();
        int trimmed = 0;
        int skippedProtected = 0;
        int skippedAccessDenied = 0;
        bool standbyPurged = false;
        bool modifiedFlushed = false;
        bool fileCacheFlushed = false;

        // 1. Working Set Trimming (non-destructive: OS moves unreferenced/cold pages to Standby/Pagefile)
        if (options.Scope.HasFlag(OptimizationScope.WorkingSets))
        {
            TrimWorkingSets(
                options,
                ct,
                ref trimmed,
                ref skippedProtected,
                ref skippedAccessDenied);
        }

        // 2. Modified Page List Flush (writes dirty pages to disk before purging Standby so they can be reclaimed)
        if (options.Scope.HasFlag(OptimizationScope.ModifiedPageList) && !ct.IsCancellationRequested)
        {
            modifiedFlushed = TryIssueMemoryListCommand(
                Win32Interop.SYSTEM_MEMORY_LIST_COMMAND.MemoryFlushModifiedList,
                "Flush Modified Page List",
                warnings);
        }

        // 3. Standby List Purge (full standby or low-priority standby)
        if (options.Scope.HasFlag(OptimizationScope.StandbyList) && !ct.IsCancellationRequested)
        {
            standbyPurged = TryIssueMemoryListCommand(
                Win32Interop.SYSTEM_MEMORY_LIST_COMMAND.MemoryPurgeStandbyList,
                "Purge Standby List",
                warnings);
        }
        else if (options.Scope.HasFlag(OptimizationScope.LowPriorityStandbyList) && !ct.IsCancellationRequested)
        {
            standbyPurged = TryIssueMemoryListCommand(
                Win32Interop.SYSTEM_MEMORY_LIST_COMMAND.MemoryPurgeLowPriorityStandbyList,
                "Purge Low-Priority Standby List",
                warnings);
        }

        // 4. System File Cache Trim
        if (options.Scope.HasFlag(OptimizationScope.SystemFileCache) && !ct.IsCancellationRequested)
        {
            fileCacheFlushed = TryFlushSystemFileCache(warnings);
        }

        MemorySnapshot after = GetSnapshot();
        long reclaimed = (long)after.AvailablePhysicalBytes - (long)before.AvailablePhysicalBytes;

        _logger.Info(
            "Optimizer",
            $"[{options.TriggerSource}] Scope={options.Scope} | Trimmed={trimmed}, SkippedProtected={skippedProtected}, " +
            $"AccessDenied={skippedAccessDenied} | StandbyPurged={standbyPurged}, ModifiedFlushed={modifiedFlushed}, " +
            $"FileCacheFlushed={fileCacheFlushed} | DeltaAvailable={FormatDeltaMb(reclaimed)} " +
            $"({before.LoadPercent:F0}% -> {after.LoadPercent:F0}%)");

        return new OptimizationResult(
            ExecutedAt: DateTimeOffset.Now,
            TriggerSource: options.TriggerSource,
            Before: before,
            After: after,
            ReclaimedBytes: reclaimed,
            ProcessesTrimmed: trimmed,
            ProcessesSkippedProtected: skippedProtected,
            ProcessesSkippedAccessDenied: skippedAccessDenied,
            StandbyPurged: standbyPurged,
            ModifiedListFlushed: modifiedFlushed,
            FileCacheFlushed: fileCacheFlushed,
            Warnings: warnings);
    }

    private void TrimWorkingSets(
        OptimizationOptions options,
        CancellationToken ct,
        ref int trimmed,
        ref int skippedProtected,
        ref int skippedAccessDenied)
    {
        int currentPid = Environment.ProcessId;
        int foregroundPid = options.ExcludeForegroundWindow ? GetForegroundProcessId() : 0;
        long thresholdBytes = Math.Max(0, options.MinimumWorkingSetThresholdMb) * 1024L * 1024L;

        // Enable SeDebugPrivilege when elevated so we can trim non-critical service working sets too.
        _privileges.TryEnablePrivilege(Win32Interop.SE_DEBUG_NAME);

        List<RawProcessEntry> processes = SystemProcessReader.SnapshotProcesses();
        foreach (RawProcessEntry proc in processes)
        {
            if (ct.IsCancellationRequested)
                break;

            if (proc.ProcessId == currentPid || (foregroundPid > 0 && proc.ProcessId == foregroundPid))
            {
                skippedProtected++;
                continue;
            }

            if (proc.TotalWorkingSetBytes < thresholdBytes)
                continue;

            if (_privileges.IsProtectedOrCriticalProcess(proc.ProcessId, proc.Name, out _))
            {
                skippedProtected++;
                continue;
            }

            IntPtr hProcess = Win32Interop.OpenProcess(
                Win32Interop.PROCESS_QUERY_LIMITED_INFORMATION | Win32Interop.PROCESS_SET_QUOTA,
                false,
                proc.ProcessId);

            if (hProcess == IntPtr.Zero)
            {
                skippedAccessDenied++;
                continue;
            }

            try
            {
                bool ok = Win32Interop.EmptyWorkingSet(hProcess);
                if (!ok)
                {
                    ok = Win32Interop.SetProcessWorkingSetSizeEx(hProcess, new IntPtr(-1), new IntPtr(-1), 0);
                }

                if (ok)
                    trimmed++;
                else
                    skippedAccessDenied++;
            }
            finally
            {
                Win32Interop.CloseHandle(hProcess);
            }
        }
    }

    private bool TryIssueMemoryListCommand(
        Win32Interop.SYSTEM_MEMORY_LIST_COMMAND command,
        string actionName,
        List<string> warnings)
    {
        if (!_privileges.TryEnablePrivilege(Win32Interop.SE_PROFILE_SINGLE_PROCESS_NAME))
        {
            string msg = $"{actionName} skipped: requires Administrator elevation (SeProfileSingleProcessPrivilege).";
            warnings.Add(msg);
            _logger.Warn("Optimizer", msg);
            return false;
        }

        int cmdValue = (int)command;
        IntPtr ptr = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            Marshal.WriteInt32(ptr, cmdValue);
            int status = Win32Interop.NtSetSystemInformation(
                Win32Interop.SystemMemoryListInformation,
                ptr,
                sizeof(int));

            if (status == Win32Interop.STATUS_SUCCESS)
                return true;

            string msg = $"{actionName} failed with NTSTATUS 0x{status:X8}.";
            warnings.Add(msg);
            _logger.Warn("Optimizer", msg);
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    private bool TryFlushSystemFileCache(List<string> warnings)
    {
        if (!_privileges.TryEnablePrivilege(Win32Interop.SE_INCREASE_QUOTA_NAME))
        {
            string msg = "System File Cache trim skipped: requires Administrator elevation (SeIncreaseQuotaPrivilege).";
            warnings.Add(msg);
            _logger.Warn("Optimizer", msg);
            return false;
        }

        int size = Marshal.SizeOf<Win32Interop.SYSTEM_FILECACHE_INFORMATION>();
        var info = new Win32Interop.SYSTEM_FILECACHE_INFORMATION
        {
            MinimumWorkingSet = unchecked((UIntPtr)(long)-1),
            MaximumWorkingSet = unchecked((UIntPtr)(long)-1)
        };

        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            int status = Win32Interop.NtSetSystemInformation(
                Win32Interop.SystemFileCacheInformation,
                ptr,
                (uint)size);

            if (status == Win32Interop.STATUS_SUCCESS)
                return true;

            string msg = $"System File Cache trim failed with NTSTATUS 0x{status:X8}.";
            warnings.Add(msg);
            _logger.Warn("Optimizer", msg);
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    private static int GetForegroundProcessId()
    {
        IntPtr hwnd = Win32Interop.GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            return 0;

        _ = Win32Interop.GetWindowThreadProcessId(hwnd, out uint pid);
        return (int)pid;
    }

    private static string FormatDeltaMb(long bytes)
    {
        double mb = bytes / (1024.0 * 1024.0);
        return mb >= 0 ? $"+{mb:F1} MB" : $"{mb:F1} MB";
    }
}
