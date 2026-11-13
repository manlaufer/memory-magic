using System.Runtime.InteropServices;
using MemoryMagic.Models;
using MemoryMagic.Native;

namespace MemoryMagic.Services;

/// <summary>
/// Real-time process memory profiler using NtQuerySystemInformation(SystemProcessInformation)
/// for accurate Private Working Set, Shared Working Set, Paged/Non-Paged Pool, and Commit metrics.
/// </summary>
public sealed class ProcessProfilerService
{
    private readonly PrivilegeService _privileges;
    private readonly AuditLogger _logger;

    public ProcessProfilerService(
        PrivilegeService privileges,
        AuditLogger logger)
    {
        _privileges = privileges;
        _logger = logger;
    }

    public unsafe IReadOnlyList<ProcessMemorySample> GetProcesses(
        long highConsumptionThresholdBytes = 250L * 1024 * 1024)
    {
        List<RawProcessEntry> rawList = SystemProcessReader.SnapshotProcesses();
        var results = new List<ProcessMemorySample>(rawList.Count);
        char* pathBuffer = stackalloc char[520];

        foreach (RawProcessEntry raw in rawList)
        {
            string? exePath = null;
            bool isCritical = raw.ProcessId <= 4;
            bool isProtected = _privileges.IsProtectedOrCriticalProcess(raw.ProcessId, raw.Name, out bool kernelCritical);
            isCritical |= kernelCritical;

            if (raw.ProcessId > 4)
            {
                IntPtr hProc = Win32Interop.OpenProcess(
                    Win32Interop.PROCESS_QUERY_LIMITED_INFORMATION,
                    false,
                    raw.ProcessId);

                if (hProc != IntPtr.Zero)
                {
                    try
                    {
                        uint size = 520;
                        if (Win32Interop.QueryFullProcessImageNameW(hProc, 0, pathBuffer, ref size) && size > 0)
                        {
                            exePath = new string(pathBuffer, 0, (int)size);
                        }
                    }
                    finally
                    {
                        Win32Interop.CloseHandle(hProc);
                    }
                }
            }

            long sharedWs = Math.Max(0L, raw.TotalWorkingSetBytes - raw.PrivateWorkingSetBytes);
            bool isHigh = raw.PrivateWorkingSetBytes >= highConsumptionThresholdBytes ||
                          raw.TotalWorkingSetBytes >= highConsumptionThresholdBytes * 2;

            results.Add(new ProcessMemorySample(
                ProcessId: raw.ProcessId,
                Name: raw.Name,
                ExecutablePath: exePath,
                SessionId: raw.SessionId,
                PrivateWorkingSetBytes: raw.PrivateWorkingSetBytes,
                SharedWorkingSetBytes: sharedWs,
                TotalWorkingSetBytes: raw.TotalWorkingSetBytes,
                CommitBytes: raw.CommitBytes,
                PagedPoolBytes: raw.PagedPoolBytes,
                NonPagedPoolBytes: raw.NonPagedPoolBytes,
                ThreadCount: raw.ThreadCount,
                HandleCount: raw.HandleCount,
                IsCritical: isCritical,
                IsProtectedSystem: isProtected,
                IsHighConsumption: isHigh));
        }

        results.Sort((a, b) => b.PrivateWorkingSetBytes.CompareTo(a.PrivateWorkingSetBytes));
        return results;
    }

    /// <summary>
    /// Safely trims a single non-critical process's working set.
    /// Never terminates processes and blocks trimming on critical kernel processes.
    /// </summary>
    public bool TryTrimProcess(int pid, string processName, out string statusMessage)
    {
        if (pid == Environment.ProcessId)
        {
            statusMessage = "Skipped trimming Memory Magic itself.";
            return false;
        }

        if (_privileges.IsProtectedOrCriticalProcess(pid, processName, out bool isCritical))
        {
            statusMessage = isCritical
                ? $"Blocked: {processName} (PID {pid}) is a critical OS process."
                : $"Skipped: {processName} (PID {pid}) is a protected system process.";
            _logger.Warn("Profiler", statusMessage);
            return false;
        }

        IntPtr hProcess = Win32Interop.OpenProcess(
            Win32Interop.PROCESS_QUERY_LIMITED_INFORMATION | Win32Interop.PROCESS_SET_QUOTA,
            false,
            pid);

        if (hProcess == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            statusMessage = $"Access denied opening {processName} (PID {pid}, Win32={err}). Try running as Administrator.";
            return false;
        }

        try
        {
            bool ok = Win32Interop.EmptyWorkingSet(hProcess) ||
                      Win32Interop.SetProcessWorkingSetSizeEx(hProcess, new IntPtr(-1), new IntPtr(-1), 0);

            if (ok)
            {
                statusMessage = $"Trimmed working set for {processName} (PID {pid}).";
                _logger.Info("Profiler", statusMessage);
                return true;
            }

            statusMessage = $"Working set trim failed for {processName} (PID {pid}, Win32={Marshal.GetLastWin32Error()}).";
            return false;
        }
        finally
        {
            Win32Interop.CloseHandle(hProcess);
        }
    }
}
