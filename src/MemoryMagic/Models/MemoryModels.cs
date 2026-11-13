namespace MemoryMagic.Models;

public sealed record MemorySnapshot(
    DateTimeOffset Timestamp,
    ulong TotalPhysicalBytes,
    ulong AvailablePhysicalBytes,
    ulong UsedPhysicalBytes,
    double LoadPercent,
    ulong CommitTotalBytes,
    ulong CommitLimitBytes,
    ulong SystemCacheBytes,
    ulong KernelPagedBytes,
    ulong KernelNonPagedBytes,
    uint ProcessCount,
    uint ThreadCount,
    uint HandleCount);

[Flags]
public enum OptimizationScope
{
    None = 0,
    WorkingSets = 1 << 0,
    LowPriorityStandbyList = 1 << 1,
    StandbyList = 1 << 2,
    ModifiedPageList = 1 << 3,
    SystemFileCache = 1 << 4,

    SafeStandard = WorkingSets | LowPriorityStandbyList,
    DeepPurge = WorkingSets | ModifiedPageList | StandbyList | SystemFileCache
}

public sealed record OptimizationOptions(
    OptimizationScope Scope,
    int MinimumWorkingSetThresholdMb = 20,
    bool ExcludeForegroundWindow = true,
    string TriggerSource = "Manual");

public sealed record OptimizationResult(
    DateTimeOffset ExecutedAt,
    string TriggerSource,
    MemorySnapshot Before,
    MemorySnapshot After,
    long ReclaimedBytes,
    int ProcessesTrimmed,
    int ProcessesSkippedProtected,
    int ProcessesSkippedAccessDenied,
    bool StandbyPurged,
    bool ModifiedListFlushed,
    bool FileCacheFlushed,
    IReadOnlyList<string> Warnings);

public sealed record ProcessMemorySample(
    int ProcessId,
    string Name,
    string? ExecutablePath,
    uint SessionId,
    long PrivateWorkingSetBytes,
    long SharedWorkingSetBytes,
    long TotalWorkingSetBytes,
    long CommitBytes,
    long PagedPoolBytes,
    long NonPagedPoolBytes,
    uint ThreadCount,
    uint HandleCount,
    bool IsCritical,
    bool IsProtectedSystem,
    bool IsHighConsumption);

public enum StartupSourceKind
{
    RegistryRunHkcu,
    RegistryRunHklm,
    RegistryRunOnce,
    StartupFolderUser,
    StartupFolderCommon,
    ScheduledTask
}

public enum StartupImpactRating
{
    Unknown,
    Low,
    Medium,
    High
}

public sealed record StartupEntry(
    string Id,
    string Name,
    string Command,
    string? ResolvedExecutablePath,
    StartupSourceKind Source,
    string Location,
    bool IsEnabled,
    bool RequiresElevationToToggle,
    StartupImpactRating Impact,
    string ImpactReason);

public enum TempCategory
{
    UserTemp,
    WindowsTemp,
    BrowserCache,
    WindowsPrefetch,
    UpdateCache,
    RecycleBin
}

public sealed record TempScanTarget(
    string Id,
    string DisplayName,
    string PathDescription,
    TempCategory Category,
    bool RequiresElevation,
    bool IsSelectedByDefault,
    long EstimatedBytes,
    int FileCount,
    bool IsAvailable);

public sealed record TempCleanResult(
    long BytesDeleted,
    int FilesDeleted,
    int FilesSkippedLocked,
    int DirectoriesRemoved,
    IReadOnlyList<string> Notes);

public sealed record AuditLogEntry(
    DateTimeOffset Timestamp,
    string Category,
    string Level,
    string Message);

public enum DebloatItemKind
{
    AppxPackage,
    WindowsCapability,
    OptionalFeature,
    SystemTweak,
    Win32Program
}

public enum DebloatSafetyLevel
{
    SafeToRemove,
    Caution,
    ProtectedCore
}

public sealed record DebloatCandidate(
    string Id,
    string Name,
    string DisplayName,
    string Publisher,
    string Version,
    DebloatItemKind Kind,
    DebloatSafetyLevel SafetyLevel,
    string SafetyReason,
    bool RequiresElevation,
    bool IsCurrentlyApplied = false,
    string? UninstallCommand = null);

public sealed record DebloatResult(
    bool Succeeded,
    string Message,
    string? Details = null);

public sealed record BatchDebloatSummary(
    int SucceededCount,
    int FailedCount,
    int SkippedProtectedCount,
    IReadOnlyList<string> Messages);

public sealed record DriveTelemetryItem(
    string DriveName,
    string VolumeLabel,
    double ActivePercent,
    ulong FreeBytes,
    ulong TotalBytes,
    string HealthStatus,
    string PhysicalDiskName);

public sealed record SystemTelemetrySnapshot(
    double CpuUsagePercent,
    double RamLoadPercent,
    ulong RamUsedBytes,
    ulong RamTotalBytes,
    string SystemDriveName,
    double DiskUsedPercent,
    double DiskActivePercent,
    ulong DiskFreeBytes,
    ulong DiskTotalBytes,
    string DiskHealthStatus,
    double DiskThroughputBytesPerSec,
    double NetworkDownBytesPerSec,
    double NetworkUpBytesPerSec,
    bool HasBattery,
    int BatteryPercent,
    bool IsOnAcPower,
    IReadOnlyList<DriveTelemetryItem> Drives);
