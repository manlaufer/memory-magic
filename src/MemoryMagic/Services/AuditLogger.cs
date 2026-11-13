using System.Collections.Concurrent;
using System.IO;
using MemoryMagic.Models;

namespace MemoryMagic.Services;

/// <summary>
/// Thread-safe audit logger persisting all trim actions, privilege transitions,
/// startup changes, and cleanup operations to %LOCALAPPDATA%\MemoryMagic\logs.
/// </summary>
public sealed class AuditLogger
{
    private const int MaxInMemoryEntries = 250;
    private const long MaxLogFileBytes = 2 * 1024 * 1024; // 2 MB rolling cap

    private readonly string _logDirectory;
    private readonly object _fileLock = new();
    private readonly ConcurrentQueue<AuditLogEntry> _recentEntries = new();

    public event Action<AuditLogEntry>? EntryLogged;

    public AuditLogger(string? logDirectory = null)
    {
        _logDirectory = logDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MemoryMagic",
            "logs");
    }

    public IReadOnlyList<AuditLogEntry> GetRecentEntries() => _recentEntries.ToArray();

    public void Info(string category, string message) => Write("INFO", category, message);
    public void Warn(string category, string message) => Write("WARN", category, message);
    public void Error(string category, string message) => Write("ERROR", category, message);

    private void Write(string level, string category, string message)
    {
        var entry = new AuditLogEntry(DateTimeOffset.Now, category, level, message);
        _recentEntries.Enqueue(entry);
        while (_recentEntries.Count > MaxInMemoryEntries && _recentEntries.TryDequeue(out _))
        {
        }

        try
        {
            lock (_fileLock)
            {
                Directory.CreateDirectory(_logDirectory);
                string filePath = Path.Combine(_logDirectory, $"memorymagic-{entry.Timestamp:yyyyMMdd}.log");
                RotateIfNeeded(filePath);
                string line = $"{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] [{category}] {message}{Environment.NewLine}";
                File.AppendAllText(filePath, line);
            }
        }
        catch
        {
            // Logging must never crash the optimizer or daemon loop.
        }

        EntryLogged?.Invoke(entry);
    }

    private static void RotateIfNeeded(string filePath)
    {
        var info = new FileInfo(filePath);
        if (!info.Exists || info.Length < MaxLogFileBytes)
            return;

        string archivePath = Path.Combine(
            info.DirectoryName!,
            $"{Path.GetFileNameWithoutExtension(filePath)}-{DateTime.Now:HHmmss}.bak.log");
        File.Move(filePath, archivePath, overwrite: true);
    }
}
