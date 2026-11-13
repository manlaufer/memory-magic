using System.IO;
using System.Runtime.InteropServices;
using MemoryMagic.Models;
using Microsoft.Win32;

namespace MemoryMagic.Services;

/// <summary>
/// Audits and safely toggles Windows startup programs across Registry Run/RunOnce keys,
/// User/Common Startup folders (using Windows StartupApproved binary flags), and Scheduled Tasks.
/// </summary>
public sealed class StartupManagerService
{
    private const string RunSubKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string RunOnceSubKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce";
    private const string RunWow64SubKey = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";

    private const string ApprovedRunSubKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ApprovedRun32SubKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32";
    private const string ApprovedFolderSubKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";

    private static readonly string[] HighImpactHints =
    [
        "teams", "discord", "slack", "steam", "epicgames", "docker", "onedrive",
        "dropbox", "adobe", "chrome", "msedge", "spotify", "vmware", "virtualbox"
    ];

    private readonly PrivilegeService _privileges;
    private readonly AuditLogger _logger;

    public StartupManagerService(PrivilegeService privileges, AuditLogger logger)
    {
        _privileges = privileges;
        _logger = logger;
    }

    public IReadOnlyList<StartupEntry> GetStartupEntries()
    {
        var entries = new List<StartupEntry>();

        ReadRegistryRunKey(RegistryHive.CurrentUser, RunSubKey, ApprovedRunSubKey, StartupSourceKind.RegistryRunHkcu, false, entries);
        ReadRegistryRunKey(RegistryHive.CurrentUser, RunOnceSubKey, null, StartupSourceKind.RegistryRunOnce, false, entries);
        ReadRegistryRunKey(RegistryHive.LocalMachine, RunSubKey, ApprovedRunSubKey, StartupSourceKind.RegistryRunHklm, true, entries);
        ReadRegistryRunKey(RegistryHive.LocalMachine, RunWow64SubKey, ApprovedRun32SubKey, StartupSourceKind.RegistryRunHklm, true, entries);
        ReadRegistryRunKey(RegistryHive.LocalMachine, RunOnceSubKey, null, StartupSourceKind.RegistryRunOnce, true, entries);

        ReadStartupFolder(
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            RegistryHive.CurrentUser,
            StartupSourceKind.StartupFolderUser,
            requiresElevation: false,
            entries);

        ReadStartupFolder(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
            RegistryHive.LocalMachine,
            StartupSourceKind.StartupFolderCommon,
            requiresElevation: true,
            entries);

        ReadScheduledStartupTasks(entries);

        return entries
            .OrderByDescending(e => e.Impact)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public bool TrySetEnabled(StartupEntry entry, bool enable, out string message)
    {
        if (entry.RequiresElevationToToggle && !_privileges.IsElevated())
        {
            message = $"Administrator privileges required to modify '{entry.Name}' ({entry.Location}).";
            return false;
        }

        try
        {
            switch (entry.Source)
            {
                case StartupSourceKind.RegistryRunHkcu:
                case StartupSourceKind.RegistryRunHklm:
                    SetRegistryStartupApproved(entry, enable);
                    break;

                case StartupSourceKind.StartupFolderUser:
                case StartupSourceKind.StartupFolderCommon:
                    SetFolderStartupApproved(entry, enable);
                    break;

                case StartupSourceKind.ScheduledTask:
                    SetScheduledTaskEnabled(entry.Location, enable);
                    break;

                case StartupSourceKind.RegistryRunOnce:
                    message = "RunOnce entries execute once and cannot be toggled via StartupApproved.";
                    return false;
            }

            message = $"{(enable ? "Enabled" : "Disabled")} startup item '{entry.Name}'.";
            _logger.Info("Startup", message);
            return true;
        }
        catch (Exception ex)
        {
            message = $"Failed to update '{entry.Name}': {ex.Message}";
            _logger.Warn("Startup", message);
            return false;
        }
    }

    private static void ReadRegistryRunKey(
        RegistryHive hive,
        string runPath,
        string? approvedPath,
        StartupSourceKind kind,
        bool requiresElevation,
        List<StartupEntry> entries)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using RegistryKey? runKey = baseKey.OpenSubKey(runPath, writable: false);
        if (runKey is null)
            return;

        using RegistryKey? approvedKey = approvedPath is not null
            ? baseKey.OpenSubKey(approvedPath, writable: false)
            : null;

        string hivePrefix = hive == RegistryHive.CurrentUser ? "HKCU" : "HKLM";

        foreach (string valueName in runKey.GetValueNames())
        {
            string? command = runKey.GetValue(valueName)?.ToString();
            if (string.IsNullOrWhiteSpace(command))
                continue;

            bool isEnabled = approvedKey is null || DecodeStartupApproved(approvedKey.GetValue(valueName) as byte[]);
            string? exePath = ExtractExecutablePath(command);
            (StartupImpactRating impact, string reason) = EstimateImpact(valueName, exePath, command);

            entries.Add(new StartupEntry(
                Id: $"{hivePrefix}|{runPath}|{valueName}",
                Name: valueName,
                Command: command,
                ResolvedExecutablePath: exePath,
                Source: kind,
                Location: $@"{hivePrefix}\{runPath}",
                IsEnabled: isEnabled,
                RequiresElevationToToggle: requiresElevation,
                Impact: impact,
                ImpactReason: reason));
        }
    }

    private static void ReadStartupFolder(
        string folderPath,
        RegistryHive hive,
        StartupSourceKind kind,
        bool requiresElevation,
        List<StartupEntry> entries)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            return;

        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using RegistryKey? approvedKey = baseKey.OpenSubKey(ApprovedFolderSubKey, writable: false);

        foreach (string file in Directory.EnumerateFiles(folderPath))
        {
            string fileName = Path.GetFileName(file);
            if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                continue;

            bool isEnabled = approvedKey is null || DecodeStartupApproved(approvedKey.GetValue(fileName) as byte[]);
            string command = file;
            string? exePath = file;

            if (Path.GetExtension(file).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                string? target = TryResolveShortcutTarget(file);
                if (!string.IsNullOrWhiteSpace(target))
                {
                    command = target;
                    exePath = target;
                }
            }

            (StartupImpactRating impact, string reason) = EstimateImpact(fileName, exePath, command);

            entries.Add(new StartupEntry(
                Id: $"FOLDER|{folderPath}|{fileName}",
                Name: Path.GetFileNameWithoutExtension(fileName),
                Command: command,
                ResolvedExecutablePath: exePath,
                Source: kind,
                Location: folderPath,
                IsEnabled: isEnabled,
                RequiresElevationToToggle: requiresElevation,
                Impact: impact,
                ImpactReason: reason));
        }
    }

    private void ReadScheduledStartupTasks(List<StartupEntry> entries)
    {
        Type? schedulerType = Type.GetTypeFromProgID("Schedule.Service");
        if (schedulerType is null)
            return;

        object? serviceObj = null;
        try
        {
            serviceObj = Activator.CreateInstance(schedulerType);
            if (serviceObj is null)
                return;

            dynamic service = serviceObj;
            service.Connect();
            dynamic rootFolder = service.GetFolder("\\");
            EnumerateTaskFolder(rootFolder, entries);
        }
        catch (Exception ex)
        {
            _logger.Warn("Startup", $"Scheduled Task enumeration partial/skipped: {ex.Message}");
        }
        finally
        {
            if (serviceObj is not null && Marshal.IsComObject(serviceObj))
                Marshal.FinalReleaseComObject(serviceObj);
        }
    }

    private static void EnumerateTaskFolder(dynamic folder, List<StartupEntry> entries)
    {
        // Skip Microsoft internal OS maintenance folders to keep the startup audit focused and safe.
        string folderPath = (string)folder.Path;
        if (folderPath.StartsWith(@"\Microsoft\Windows", StringComparison.OrdinalIgnoreCase))
            return;

        dynamic tasks = folder.GetTasks(0);
        foreach (dynamic task in tasks)
        {
            try
            {
                dynamic def = task.Definition;
                bool hasStartupTrigger = false;
                foreach (dynamic trigger in def.Triggers)
                {
                    int triggerType = (int)trigger.Type;
                    // 8 = TASK_TRIGGER_BOOT, 9 = TASK_TRIGGER_LOGON
                    if (triggerType is 8 or 9)
                    {
                        hasStartupTrigger = true;
                        break;
                    }
                }

                if (!hasStartupTrigger)
                    continue;

                string command = "";
                foreach (dynamic action in def.Actions)
                {
                    if ((int)action.Type == 0) // TASK_ACTION_EXEC
                    {
                        string path = (string)(action.Path ?? "");
                        string args = (string)(action.Arguments ?? "");
                        command = string.IsNullOrWhiteSpace(args) ? path : $"{path} {args}";
                        break;
                    }
                }

                string taskName = (string)task.Name;
                string taskPath = (string)task.Path;
                bool enabled = (bool)task.Enabled;
                string? exePath = ExtractExecutablePath(command);
                (StartupImpactRating impact, string reason) = EstimateImpact(taskName, exePath, command);

                entries.Add(new StartupEntry(
                    Id: $"TASK|{taskPath}",
                    Name: taskName,
                    Command: command,
                    ResolvedExecutablePath: exePath,
                    Source: StartupSourceKind.ScheduledTask,
                    Location: taskPath,
                    IsEnabled: enabled,
                    RequiresElevationToToggle: true,
                    Impact: impact,
                    ImpactReason: reason));
            }
            catch
            {
                // Ignore individual restricted task entries
            }
        }

        dynamic subFolders = folder.GetFolders(0);
        foreach (dynamic sub in subFolders)
        {
            EnumerateTaskFolder(sub, entries);
        }
    }

    private static void SetRegistryStartupApproved(StartupEntry entry, bool enable)
    {
        string[] parts = entry.Id.Split('|', 3);
        RegistryHive hive = parts[0] == "HKCU" ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;
        string runSubKey = parts[1];
        string valueName = parts[2];

        string approvedSubKey = runSubKey.Contains("WOW6432Node", StringComparison.OrdinalIgnoreCase)
            ? ApprovedRun32SubKey
            : ApprovedRunSubKey;

        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using RegistryKey approvedKey = baseKey.CreateSubKey(approvedSubKey, writable: true);
        approvedKey.SetValue(valueName, EncodeStartupApproved(enable), RegistryValueKind.Binary);
    }

    private static void SetFolderStartupApproved(StartupEntry entry, bool enable)
    {
        string[] parts = entry.Id.Split('|', 3);
        string fileName = parts[2];
        RegistryHive hive = entry.Source == StartupSourceKind.StartupFolderUser
            ? RegistryHive.CurrentUser
            : RegistryHive.LocalMachine;

        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using RegistryKey approvedKey = baseKey.CreateSubKey(ApprovedFolderSubKey, writable: true);
        approvedKey.SetValue(fileName, EncodeStartupApproved(enable), RegistryValueKind.Binary);
    }

    private static void SetScheduledTaskEnabled(string taskPath, bool enable)
    {
        Type? schedulerType = Type.GetTypeFromProgID("Schedule.Service");
        if (schedulerType is null)
            throw new PlatformNotSupportedException("Task Scheduler COM service unavailable.");

        object? serviceObj = Activator.CreateInstance(schedulerType);
        if (serviceObj is null)
            throw new InvalidOperationException("Could not create Schedule.Service instance.");

        try
        {
            dynamic service = serviceObj;
            service.Connect();
            dynamic root = service.GetFolder("\\");
            dynamic task = root.GetTask(taskPath.TrimStart('\\'));
            task.Enabled = enable;
        }
        finally
        {
            if (Marshal.IsComObject(serviceObj))
                Marshal.FinalReleaseComObject(serviceObj);
        }
    }

    private static bool DecodeStartupApproved(byte[]? data)
    {
        if (data is null || data.Length == 0)
            return true;

        // Windows StartupApproved convention: even low bit (0x02 / 0x06) = Enabled, odd (0x03 / 0x01) = Disabled
        return (data[0] & 0x01) == 0;
    }

    private static byte[] EncodeStartupApproved(bool enable)
    {
        var bytes = new byte[12];
        if (enable)
        {
            bytes[0] = 0x02;
            return bytes;
        }

        bytes[0] = 0x03;
        long fileTime = DateTime.UtcNow.ToFileTimeUtc();
        BitConverter.GetBytes(fileTime).CopyTo(bytes, 4);
        return bytes;
    }

    private static string? ExtractExecutablePath(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;

        string expanded = Environment.ExpandEnvironmentVariables(command.Trim());
        if (expanded.StartsWith('"'))
        {
            int endQuote = expanded.IndexOf('"', 1);
            return endQuote > 1 ? expanded[1..endQuote] : expanded.Trim('"');
        }

        int exeIdx = expanded.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIdx >= 0)
            return expanded[..(exeIdx + 4)].Trim();

        int firstSpace = expanded.IndexOf(' ');
        return firstSpace > 0 ? expanded[..firstSpace] : expanded;
    }

    private static string? TryResolveShortcutTarget(string lnkPath)
    {
        Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null)
            return null;

        object? shellObj = null;
        try
        {
            shellObj = Activator.CreateInstance(shellType);
            if (shellObj is null)
                return null;

            dynamic shell = shellObj;
            dynamic shortcut = shell.CreateShortcut(lnkPath);
            string target = (string)(shortcut.TargetPath ?? "");
            string args = (string)(shortcut.Arguments ?? "");
            return string.IsNullOrWhiteSpace(args) ? target : $"{target} {args}";
        }
        catch
        {
            return null;
        }
        finally
        {
            if (shellObj is not null && Marshal.IsComObject(shellObj))
                Marshal.FinalReleaseComObject(shellObj);
        }
    }

    private static (StartupImpactRating Rating, string Reason) EstimateImpact(
        string name,
        string? exePath,
        string command)
    {
        string haystack = $"{name} {exePath} {command}";
        foreach (string hint in HighImpactHints)
        {
            if (haystack.Contains(hint, StringComparison.OrdinalIgnoreCase))
                return (StartupImpactRating.High, $"Heavy runtime / background suite ({hint})");
        }

        if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
        {
            long sizeBytes = new FileInfo(exePath).Length;
            if (sizeBytes >= 40L * 1024 * 1024)
                return (StartupImpactRating.High, $"Large binary ({sizeBytes / (1024 * 1024)} MB)");
            if (sizeBytes >= 8L * 1024 * 1024)
                return (StartupImpactRating.Medium, $"Medium binary ({sizeBytes / (1024 * 1024)} MB)");
            return (StartupImpactRating.Low, $"Lightweight binary ({Math.Max(1, sizeBytes / 1024)} KB)");
        }

        return (StartupImpactRating.Unknown, "Executable not found on disk");
    }
}
