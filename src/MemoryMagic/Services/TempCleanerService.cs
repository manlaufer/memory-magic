using System.IO;
using MemoryMagic.Models;
using MemoryMagic.Native;

namespace MemoryMagic.Services;

/// <summary>
/// Audits and safely purges temporary directories, browser caches, optional Windows Prefetch,
/// and Recycle Bin while skipping locked files and never traversing reparse points/symlinks.
/// </summary>
public sealed class TempCleanerService
{
    private readonly PrivilegeService _privileges;
    private readonly AuditLogger _logger;

    public TempCleanerService(PrivilegeService privileges, AuditLogger logger)
    {
        _privileges = privileges;
        _logger = logger;
    }

    public async Task<IReadOnlyList<TempScanTarget>> ScanTargetsAsync(CancellationToken ct = default)
    {
        return await Task.Run(() => ScanTargetsCore(ct), ct).ConfigureAwait(false);
    }

    public async Task<TempCleanResult> CleanTargetsAsync(
        IEnumerable<TempScanTarget> selectedTargets,
        CancellationToken ct = default)
    {
        var targets = selectedTargets.ToList();
        return await Task.Run(() => CleanTargetsCore(targets, ct), ct).ConfigureAwait(false);
    }

    private IReadOnlyList<TempScanTarget> ScanTargetsCore(CancellationToken ct)
    {
        var specs = BuildTargetDefinitions();
        var results = new List<TempScanTarget>(specs.Count);

        foreach (var spec in specs)
        {
            if (ct.IsCancellationRequested)
                break;

            if (spec.Category == TempCategory.RecycleBin)
            {
                results.Add(new TempScanTarget(
                    spec.Id,
                    spec.DisplayName,
                    "Windows Shell Recycle Bin",
                    spec.Category,
                    spec.RequiresElevation,
                    spec.SelectedByDefault,
                    EstimatedBytes: 0,
                    FileCount: 0,
                    IsAvailable: true));
                continue;
            }

            long totalBytes = 0;
            int fileCount = 0;
            bool anyExists = false;

            foreach (string dir in spec.Paths)
            {
                if (!Directory.Exists(dir))
                    continue;

                anyExists = true;
                (long bytes, int count) = EstimateDirectory(dir, ct);
                totalBytes += bytes;
                fileCount += count;
            }

            results.Add(new TempScanTarget(
                spec.Id,
                spec.DisplayName,
                string.Join("; ", spec.Paths),
                spec.Category,
                spec.RequiresElevation,
                spec.SelectedByDefault,
                totalBytes,
                fileCount,
                anyExists));
        }

        return results;
    }

    private TempCleanResult CleanTargetsCore(List<TempScanTarget> targets, CancellationToken ct)
    {
        var defsById = BuildTargetDefinitions().ToDictionary(d => d.Id);
        long bytesDeleted = 0;
        int filesDeleted = 0;
        int filesSkippedLocked = 0;
        int dirsRemoved = 0;
        var notes = new List<string>();
        bool isElevated = _privileges.IsElevated();

        foreach (TempScanTarget target in targets)
        {
            if (ct.IsCancellationRequested)
                break;

            if (target.RequiresElevation && !isElevated)
            {
                notes.Add($"Skipped '{target.DisplayName}': requires Administrator elevation.");
                continue;
            }

            if (target.Category == TempCategory.RecycleBin)
            {
                int hr = Win32Interop.SHEmptyRecycleBinW(
                    IntPtr.Zero,
                    null,
                    Win32Interop.SHERB_NOCONFIRMATION | Win32Interop.SHERB_NOPROGRESSUI | Win32Interop.SHERB_NOSOUND);

                notes.Add(hr == 0
                    ? "Emptied Recycle Bin."
                    : $"Recycle Bin returned HRESULT 0x{hr:X8} (may already be empty).");
                continue;
            }

            if (!defsById.TryGetValue(target.Id, out var def))
                continue;

            foreach (string rootDir in def.Paths)
            {
                if (!Directory.Exists(rootDir))
                    continue;

                PurgeDirectoryContents(
                    rootDir,
                    isRoot: true,
                    ct,
                    ref bytesDeleted,
                    ref filesDeleted,
                    ref filesSkippedLocked,
                    ref dirsRemoved);
            }
        }

        _logger.Info(
            "Cleaner",
            $"Purged {filesDeleted} files ({bytesDeleted / (1024.0 * 1024.0):F1} MB), " +
            $"SkippedLocked={filesSkippedLocked}, DirsRemoved={dirsRemoved}");

        return new TempCleanResult(bytesDeleted, filesDeleted, filesSkippedLocked, dirsRemoved, notes);
    }

    private static (long Bytes, int Count) EstimateDirectory(string directoryPath, CancellationToken ct)
    {
        long bytes = 0;
        int count = 0;
        var stack = new Stack<string>();
        stack.Push(directoryPath);

        while (stack.Count > 0 && !ct.IsCancellationRequested)
        {
            string current = stack.Pop();
            try
            {
                var dirInfo = new DirectoryInfo(current);
                if (dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    continue;

                foreach (FileInfo file in dirInfo.EnumerateFiles())
                {
                    if (ct.IsCancellationRequested)
                        break;
                    bytes += file.Length;
                    count++;
                }

                foreach (DirectoryInfo sub in dirInfo.EnumerateDirectories())
                {
                    if (!sub.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        stack.Push(sub.FullName);
                }
            }
            catch
            {
                // Skip protected/inaccessible subfolders gracefully
            }
        }

        return (bytes, count);
    }

    private static void PurgeDirectoryContents(
        string directoryPath,
        bool isRoot,
        CancellationToken ct,
        ref long bytesDeleted,
        ref int filesDeleted,
        ref int filesSkippedLocked,
        ref int dirsRemoved)
    {
        DirectoryInfo dirInfo;
        try
        {
            dirInfo = new DirectoryInfo(directoryPath);
            if (!dirInfo.Exists || dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return;
        }
        catch
        {
            return;
        }

        try
        {
            foreach (FileInfo file in dirInfo.EnumerateFiles())
            {
                if (ct.IsCancellationRequested)
                    return;

                try
                {
                    if (file.IsReadOnly)
                        file.IsReadOnly = false;

                    long len = file.Length;
                    file.Delete();
                    bytesDeleted += len;
                    filesDeleted++;
                }
                catch
                {
                    filesSkippedLocked++;
                }
            }

            foreach (DirectoryInfo sub in dirInfo.EnumerateDirectories())
            {
                if (ct.IsCancellationRequested)
                    return;

                if (sub.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    continue;

                PurgeDirectoryContents(
                    sub.FullName,
                    isRoot: false,
                    ct,
                    ref bytesDeleted,
                    ref filesDeleted,
                    ref filesSkippedLocked,
                    ref dirsRemoved);
            }
        }
        catch
        {
            // Ignore folder enumeration permission failures
        }

        if (!isRoot)
        {
            try
            {
                if (!dirInfo.EnumerateFileSystemInfos().Any())
                {
                    dirInfo.Delete();
                    dirsRemoved++;
                }
            }
            catch
            {
            }
        }
    }

    private static List<TargetDefinition> BuildTargetDefinitions()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        var browserCacheDirs = new List<string>
        {
            Path.Combine(localAppData, @"Microsoft\Edge\User Data\Default\Cache\Cache_Data"),
            Path.Combine(localAppData, @"Microsoft\Edge\User Data\Default\Code Cache"),
            Path.Combine(localAppData, @"Google\Chrome\User Data\Default\Cache\Cache_Data"),
            Path.Combine(localAppData, @"Google\Chrome\User Data\Default\Code Cache"),
            Path.Combine(localAppData, @"BraveSoftware\Brave-Browser\User Data\Default\Cache\Cache_Data")
        };

        string firefoxProfiles = Path.Combine(localAppData, @"Mozilla\Firefox\Profiles");
        if (Directory.Exists(firefoxProfiles))
        {
            try
            {
                foreach (string profile in Directory.EnumerateDirectories(firefoxProfiles))
                {
                    browserCacheDirs.Add(Path.Combine(profile, "cache2"));
                }
            }
            catch
            {
            }
        }

        return
        [
            new TargetDefinition(
                "user-temp",
                "User Temporary Files (%TEMP%)",
                TempCategory.UserTemp,
                RequiresElevation: false,
                SelectedByDefault: true,
                Paths: [Path.GetTempPath()]),

            new TargetDefinition(
                "windows-temp",
                "Windows System Temp",
                TempCategory.WindowsTemp,
                RequiresElevation: true,
                SelectedByDefault: true,
                Paths: [Path.Combine(windowsDir, "Temp")]),

            new TargetDefinition(
                "browser-cache",
                "Browser Caches (Edge / Chrome / Brave / Firefox)",
                TempCategory.BrowserCache,
                RequiresElevation: false,
                SelectedByDefault: true,
                Paths: browserCacheDirs),

            new TargetDefinition(
                "windows-update",
                "Windows Update Download Cache",
                TempCategory.UpdateCache,
                RequiresElevation: true,
                SelectedByDefault: false,
                Paths: [Path.Combine(windowsDir, @"SoftwareDistribution\Download")]),

            new TargetDefinition(
                "windows-prefetch",
                "Windows Prefetch Cache (Optional)",
                TempCategory.WindowsPrefetch,
                RequiresElevation: true,
                SelectedByDefault: false,
                Paths: [Path.Combine(windowsDir, "Prefetch")]),

            new TargetDefinition(
                "recycle-bin",
                "Recycle Bin",
                TempCategory.RecycleBin,
                RequiresElevation: false,
                SelectedByDefault: false,
                Paths: [])
        ];
    }

    private sealed record TargetDefinition(
        string Id,
        string DisplayName,
        TempCategory Category,
        bool RequiresElevation,
        bool SelectedByDefault,
        IReadOnlyList<string> Paths);
}
