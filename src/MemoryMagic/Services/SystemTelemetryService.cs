using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using MemoryMagic.Models;
using MemoryMagic.Native;

namespace MemoryMagic.Services;

/// <summary>
/// Lightweight system telemetry sampler for CPU, RAM, Network (Up/Down),
/// multi-drive Disk Active % / Free Space / WMI SMART Health, and physical Battery status.
/// </summary>
public sealed class SystemTelemetryService
{
    private readonly MemoryOptimizerService _optimizer;
    private readonly object _sampleLock = new();
    private readonly object _healthLock = new();

    private bool _hasCpuBaseline;
    private ulong _prevIdle;
    private ulong _prevKernel;
    private ulong _prevUser;
    private double _lastCpuPercent;

    private long _lastIoTimestampTicks;
    private long _prevNetDown;
    private long _prevNetUp;
    private long _prevDiskBytes;
    private double _lastNetDownPerSec;
    private double _lastNetUpPerSec;
    private double _lastDiskBytesPerSec;

    private IntPtr _pdhQuery;
    private IntPtr _pdhDiskIdleCounter;
    private bool _pdhHasBaseline;
    private Dictionary<string, double> _lastDiskActiveByDrive = new(StringComparer.OrdinalIgnoreCase);

    private long _lastHealthRefreshTicks;
    private int _healthRefreshInFlight;
    private Dictionary<string, (string Health, string Model)> _driveHealthCache = new(StringComparer.OrdinalIgnoreCase);

    public SystemTelemetryService(MemoryOptimizerService optimizer)
    {
        _optimizer = optimizer;
        InitPdhDiskQuery();
        CaptureBaselines();
        TriggerAsyncHealthRefresh();
    }

    public SystemTelemetrySnapshot CaptureSnapshot()
    {
        double cpu = SampleCpuPercent();
        MemorySnapshot mem = _optimizer.GetSnapshot();
        (double netDown, double netUp, double diskThroughput) = SampleThroughput();
        IReadOnlyDictionary<string, double> activeMap = SampleDiskActivePercentages();
        MaybeScheduleHealthRefresh();

        IReadOnlyDictionary<string, (string Health, string Model)> healthMap;
        lock (_healthLock)
            healthMap = _driveHealthCache;

        var drives = new List<DriveTelemetryItem>();
        ulong allTotalDisk = 0;
        ulong allFreeDisk = 0;

        try
        {
            foreach (DriveInfo di in DriveInfo.GetDrives())
            {
                if (!di.IsReady || di.DriveType != DriveType.Fixed)
                    continue;

                string letter = di.Name.TrimEnd('\\');
                ulong total = (ulong)Math.Max(0, di.TotalSize);
                ulong free = (ulong)Math.Max(0, di.AvailableFreeSpace);
                allTotalDisk += total;
                allFreeDisk += free;

                double activePct = activeMap.TryGetValue(letter, out double pct) ? pct : 0.0;
                (string health, string model) = healthMap.TryGetValue(letter, out var info)
                    ? info
                    : ("Healthy", "Local Disk");

                string label = string.Empty;
                try { label = di.VolumeLabel ?? string.Empty; } catch { }

                drives.Add(new DriveTelemetryItem(
                    DriveName: letter,
                    VolumeLabel: label,
                    ActivePercent: activePct,
                    FreeBytes: free,
                    TotalBytes: total,
                    HealthStatus: health,
                    PhysicalDiskName: model));
            }
        }
        catch
        {
        }

        string root = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        double totalUsedPct = allTotalDisk > 0 ? ((allTotalDisk - allFreeDisk) * 100.0) / allTotalDisk : 0.0;
        double overallActive = activeMap.TryGetValue("_Total", out double totalAct)
            ? totalAct
            : (drives.Count > 0 ? drives.Max(d => d.ActivePercent) : 0.0);
        string overallHealth = AggregateHealthStatus(drives);

        (bool hasBattery, int batteryPct, bool onAc) = ReadBatteryStatus();

        return new SystemTelemetrySnapshot(
            CpuUsagePercent: cpu,
            RamLoadPercent: mem.LoadPercent,
            RamUsedBytes: mem.UsedPhysicalBytes,
            RamTotalBytes: mem.TotalPhysicalBytes,
            SystemDriveName: root.TrimEnd('\\'),
            DiskUsedPercent: totalUsedPct,
            DiskActivePercent: overallActive,
            DiskFreeBytes: allFreeDisk,
            DiskTotalBytes: allTotalDisk,
            DiskHealthStatus: overallHealth,
            DiskThroughputBytesPerSec: diskThroughput,
            NetworkDownBytesPerSec: netDown,
            NetworkUpBytesPerSec: netUp,
            HasBattery: hasBattery,
            BatteryPercent: batteryPct,
            IsOnAcPower: onAc,
            Drives: drives);
    }

    public static (string ScopeLabel, double ActivePercent, ulong FreeBytes, ulong TotalBytes, string HealthStatus) SelectDriveScope(
        SystemTelemetrySnapshot snapshot,
        string? selectedDrive)
    {
        if (!string.IsNullOrWhiteSpace(selectedDrive) &&
            !selectedDrive.Equals("ALL", StringComparison.OrdinalIgnoreCase))
        {
            DriveTelemetryItem? match = snapshot.Drives.FirstOrDefault(d =>
                d.DriveName.Equals(selectedDrive, StringComparison.OrdinalIgnoreCase));

            if (match is not null)
                return (match.DriveName, match.ActivePercent, match.FreeBytes, match.TotalBytes, match.HealthStatus);
        }

        return ("ALL", snapshot.DiskActivePercent, snapshot.DiskFreeBytes, snapshot.DiskTotalBytes, snapshot.DiskHealthStatus);
    }

    private void CaptureBaselines()
    {
        lock (_sampleLock)
        {
            if (Win32Interop.GetSystemTimes(out var idle, out var kernel, out var user))
            {
                _prevIdle = idle.ToUInt64();
                _prevKernel = kernel.ToUInt64();
                _prevUser = user.ToUInt64();
                _hasCpuBaseline = true;
            }

            (_prevNetDown, _prevNetUp) = ReadNetworkTotals();
            _prevDiskBytes = ReadDiskIoTotals();
            _lastIoTimestampTicks = Stopwatch.GetTimestamp();
        }
    }

    private double SampleCpuPercent()
    {
        lock (_sampleLock)
        {
            if (!Win32Interop.GetSystemTimes(out var idleFt, out var kernelFt, out var userFt))
                return _lastCpuPercent;

            ulong idle = idleFt.ToUInt64();
            ulong kernel = kernelFt.ToUInt64();
            ulong user = userFt.ToUInt64();

            if (!_hasCpuBaseline)
            {
                _prevIdle = idle;
                _prevKernel = kernel;
                _prevUser = user;
                _hasCpuBaseline = true;
                return _lastCpuPercent;
            }

            ulong idleDelta = idle - _prevIdle;
            ulong kernelDelta = kernel - _prevKernel;
            ulong userDelta = user - _prevUser;
            ulong totalSys = kernelDelta + userDelta;

            if (totalSys < 500_000)
                return _lastCpuPercent;

            _prevIdle = idle;
            _prevKernel = kernel;
            _prevUser = user;

            ulong busy = totalSys > idleDelta ? totalSys - idleDelta : 0;
            _lastCpuPercent = Math.Clamp((busy * 100.0) / totalSys, 0.0, 100.0);
            return _lastCpuPercent;
        }
    }

    private (double NetDownPerSec, double NetUpPerSec, double DiskPerSec) SampleThroughput()
    {
        lock (_sampleLock)
        {
            long nowTicks = Stopwatch.GetTimestamp();
            double elapsedSec = (nowTicks - _lastIoTimestampTicks) / (double)Stopwatch.Frequency;
            if (elapsedSec < 0.25)
                return (_lastNetDownPerSec, _lastNetUpPerSec, _lastDiskBytesPerSec);

            (long curDown, long curUp) = ReadNetworkTotals();
            long curDisk = ReadDiskIoTotals();

            _lastNetDownPerSec = Math.Max(0, curDown - _prevNetDown) / elapsedSec;
            _lastNetUpPerSec = Math.Max(0, curUp - _prevNetUp) / elapsedSec;
            _lastDiskBytesPerSec = Math.Max(0, curDisk - _prevDiskBytes) / elapsedSec;

            _prevNetDown = curDown;
            _prevNetUp = curUp;
            _prevDiskBytes = curDisk;
            _lastIoTimestampTicks = nowTicks;

            return (_lastNetDownPerSec, _lastNetUpPerSec, _lastDiskBytesPerSec);
        }
    }

    private static (long Down, long Up) ReadNetworkTotals()
    {
        long down = 0;
        long up = 0;

        try
        {
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up)
                    continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;

                IPInterfaceStatistics stats = ni.GetIPStatistics();
                down += stats.BytesReceived;
                up += stats.BytesSent;
            }
        }
        catch
        {
        }

        return (down, up);
    }

    private static long ReadDiskIoTotals()
    {
        try
        {
            long total = 0;
            foreach (RawProcessEntry entry in SystemProcessReader.SnapshotProcesses())
            {
                total += entry.IoTransferBytes;
            }
            return total;
        }
        catch
        {
            return 0;
        }
    }

    private void InitPdhDiskQuery()
    {
        try
        {
            if (Win32Interop.PdhOpenQueryW(null, IntPtr.Zero, out IntPtr q) == 0 &&
                Win32Interop.PdhAddEnglishCounterW(q, @"\LogicalDisk(*)\% Idle Time", IntPtr.Zero, out IntPtr c) == 0)
            {
                _pdhQuery = q;
                _pdhDiskIdleCounter = c;
                if (Win32Interop.PdhCollectQueryData(_pdhQuery) == 0)
                    _pdhHasBaseline = true;
            }
        }
        catch
        {
            _pdhQuery = IntPtr.Zero;
            _pdhDiskIdleCounter = IntPtr.Zero;
        }
    }

    private IReadOnlyDictionary<string, double> SampleDiskActivePercentages()
    {
        lock (_sampleLock)
        {
            if (_pdhQuery == IntPtr.Zero || _pdhDiskIdleCounter == IntPtr.Zero)
                return _lastDiskActiveByDrive;

            if (Win32Interop.PdhCollectQueryData(_pdhQuery) != 0)
                return _lastDiskActiveByDrive;

            if (!_pdhHasBaseline)
            {
                _pdhHasBaseline = true;
                return _lastDiskActiveByDrive;
            }

            uint bufSize = 0;
            uint itemCount = 0;
            int status = Win32Interop.PdhGetFormattedCounterArrayW(
                _pdhDiskIdleCounter,
                Win32Interop.PDH_FMT_DOUBLE,
                ref bufSize,
                ref itemCount,
                IntPtr.Zero);

            if (status != Win32Interop.PDH_MORE_DATA || bufSize == 0)
                return _lastDiskActiveByDrive;

            IntPtr buffer = Marshal.AllocHGlobal((int)bufSize);
            try
            {
                status = Win32Interop.PdhGetFormattedCounterArrayW(
                    _pdhDiskIdleCounter,
                    Win32Interop.PDH_FMT_DOUBLE,
                    ref bufSize,
                    ref itemCount,
                    buffer);

                if (status != 0 || itemCount == 0)
                    return _lastDiskActiveByDrive;

                var updated = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                int stride = Marshal.SizeOf<Win32Interop.PDH_FMT_COUNTERVALUE_ITEM_DOUBLE>();
                for (int i = 0; i < itemCount; i++)
                {
                    IntPtr elemPtr = IntPtr.Add(buffer, i * stride);
                    var item = Marshal.PtrToStructure<Win32Interop.PDH_FMT_COUNTERVALUE_ITEM_DOUBLE>(elemPtr);
                    if (item.szName == IntPtr.Zero || item.CStatus != 0)
                        continue;

                    string? name = Marshal.PtrToStringUni(item.szName);
                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    updated[name.TrimEnd('\\')] = Math.Clamp(100.0 - item.doubleValue, 0.0, 100.0);
                }

                if (updated.Count > 0)
                    _lastDiskActiveByDrive = updated;
            }
            catch
            {
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            return _lastDiskActiveByDrive;
        }
    }

    private void MaybeScheduleHealthRefresh()
    {
        long now = Stopwatch.GetTimestamp();
        if ((now - _lastHealthRefreshTicks) / (double)Stopwatch.Frequency >= 60.0)
            TriggerAsyncHealthRefresh();
    }

    private void TriggerAsyncHealthRefresh()
    {
        if (Interlocked.CompareExchange(ref _healthRefreshInFlight, 1, 0) != 0)
            return;

        _ = Task.Run(() =>
        {
            try
            {
                Dictionary<string, (string Health, string Model)> fresh = QueryWmiDiskHealthByDrive();
                lock (_healthLock)
                    _driveHealthCache = fresh;
                _lastHealthRefreshTicks = Stopwatch.GetTimestamp();
            }
            catch
            {
            }
            finally
            {
                Interlocked.Exchange(ref _healthRefreshInFlight, 0);
            }
        });
    }

    private static Dictionary<string, (string Health, string Model)> QueryWmiDiskHealthByDrive()
    {
        var result = new Dictionary<string, (string Health, string Model)>(StringComparer.OrdinalIgnoreCase);
        var physicalByIndex = new Dictionary<int, (string Health, string Model)>();

        try
        {
            Type? locatorType = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
            if (locatorType is null)
                return result;

            dynamic locator = Activator.CreateInstance(locatorType)!;

            // 1. Primary query: ROOT\Microsoft\Windows\Storage -> MSFT_PhysicalDisk & MSFT_Partition
            dynamic storageSvc = locator.ConnectServer(".", @"ROOT\Microsoft\Windows\Storage");
            foreach (dynamic pd in storageSvc.ExecQuery("SELECT DeviceId, FriendlyName, HealthStatus, OperationalStatus FROM MSFT_PhysicalDisk"))
            {
                string? idStr = ReadWmiProp(pd, "DeviceId")?.ToString();
                if (!int.TryParse(idStr, out int diskIdx))
                    continue;

                string model = ReadWmiProp(pd, "FriendlyName")?.ToString()?.Trim() ?? "Physical Disk";
                int healthCode = ReadWmiInt(ReadWmiProp(pd, "HealthStatus"), -1);
                string health = MapMsftHealthStatus(healthCode, ReadWmiProp(pd, "OperationalStatus"));
                physicalByIndex[diskIdx] = (health, model);
            }

            // 2. Fallback / SMART status corroboration via Win32_DiskDrive
            dynamic cimSvc = locator.ConnectServer(".", @"ROOT\CIMV2");
            foreach (dynamic w32 in cimSvc.ExecQuery("SELECT Index, Model, Status FROM Win32_DiskDrive"))
            {
                int idx = ReadWmiInt(ReadWmiProp(w32, "Index"), -1);
                if (idx < 0) continue;

                string w32Model = ReadWmiProp(w32, "Model")?.ToString()?.Trim() ?? "Disk Drive";
                string w32Status = ReadWmiProp(w32, "Status")?.ToString()?.Trim() ?? string.Empty;
                string w32Health = w32Status.Equals("OK", StringComparison.OrdinalIgnoreCase)
                    ? "Healthy"
                    : w32Status.Contains("Pred", StringComparison.OrdinalIgnoreCase)
                        ? "Warning"
                        : w32Status.Contains("Error", StringComparison.OrdinalIgnoreCase) || w32Status.Contains("Degrad", StringComparison.OrdinalIgnoreCase)
                            ? "Unhealthy"
                            : "Unknown";

                if (!physicalByIndex.TryGetValue(idx, out var existing))
                    physicalByIndex[idx] = (w32Health, w32Model);
                else if (existing.Health == "Unknown" && w32Health != "Unknown")
                    physicalByIndex[idx] = (w32Health, existing.Model);
            }

            // 3. Map partition drive letters (e.g. 'C' or 67) -> DiskNumber
            foreach (dynamic part in storageSvc.ExecQuery("SELECT DriveLetter, DiskNumber FROM MSFT_Partition"))
            {
                object? dlRaw = ReadWmiProp(part, "DriveLetter");
                string? driveLetter = ParseWmiDriveLetter(dlRaw);
                if (driveLetter is null)
                    continue;

                int diskNum = ReadWmiInt(ReadWmiProp(part, "DiskNumber"), -1);
                if (physicalByIndex.TryGetValue(diskNum, out var info))
                    result[driveLetter] = info;
            }
        }
        catch
        {
        }

        return result;
    }

    private static object? ReadWmiProp(dynamic swbemObj, string propName)
    {
        try
        {
            return swbemObj.Properties_.Item(propName).Value;
        }
        catch
        {
            return null;
        }
    }

    private static int ReadWmiInt(object? raw, int fallback)
    {
        if (raw is null) return fallback;
        try { return Convert.ToInt32(raw); } catch { return fallback; }
    }

    private static string? ParseWmiDriveLetter(object? raw)
    {
        if (raw is null) return null;
        char c;
        if (raw is char ch) c = ch;
        else if (raw is ushort u && u > 0) c = (char)u;
        else if (raw is int iv && iv > 0) c = (char)iv;
        else if (raw is string s && s.Length > 0) c = s[0];
        else return null;

        return char.IsLetter(c) ? $"{char.ToUpperInvariant(c)}:" : null;
    }

    private static string MapMsftHealthStatus(int healthStatus, object? operationalStatusRaw)
    {
        switch (healthStatus)
        {
            case 0: return "Healthy";
            case 1: return "Warning";
            case 2: return "Unhealthy";
            case 5:
            default:
                if (operationalStatusRaw is Array arr)
                {
                    foreach (object? v in arr)
                    {
                        int code = ReadWmiInt(v, -1);
                        if (code == 2) return "Healthy";
                        if (code is 3 or 10) return "Warning";
                        if (code is 4 or 6 or 8 or 9) return "Unhealthy";
                    }
                }
                return "Unknown";
        }
    }

    private static string AggregateHealthStatus(IReadOnlyList<DriveTelemetryItem> drives)
    {
        if (drives.Count == 0) return "Unknown";
        if (drives.Any(d => d.HealthStatus == "Unhealthy")) return "Unhealthy";
        if (drives.Any(d => d.HealthStatus == "Warning")) return "Warning";
        if (drives.Any(d => d.HealthStatus == "Healthy")) return "Healthy";
        return "Unknown";
    }

    private static (bool HasBattery, int Percent, bool OnAc) ReadBatteryStatus()
    {
        if (!Win32Interop.GetSystemPowerStatus(out var status))
            return (false, 0, true);

        // BatteryFlag 128 = No system battery; 255 = Unknown status
        bool noBattery = (status.BatteryFlag & 128) != 0 || status.BatteryFlag == 255 || status.BatteryLifePercent > 100;
        if (noBattery)
            return (false, 0, status.ACLineStatus == 1);

        return (true, status.BatteryLifePercent, status.ACLineStatus == 1);
    }
}
