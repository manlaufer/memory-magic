using System.Runtime.InteropServices;
using MemoryMagic.Native;

namespace MemoryMagic.Native;

internal readonly record struct RawProcessEntry(
    int ProcessId,
    string Name,
    uint SessionId,
    long PrivateWorkingSetBytes,
    long TotalWorkingSetBytes,
    long CommitBytes,
    long PagedPoolBytes,
    long NonPagedPoolBytes,
    uint ThreadCount,
    uint HandleCount,
    long IoTransferBytes);

/// <summary>
/// Shared kernel-level process enumerator using NtQuerySystemInformation(SystemProcessInformation).
/// Retrieves working set, pool, and commit metrics in a single syscall without opening 300+ handles.
/// </summary>
internal static unsafe class SystemProcessReader
{
    public static List<RawProcessEntry> SnapshotProcesses()
    {
        uint bufferSize = 512 * 1024;
        IntPtr buffer = Marshal.AllocHGlobal((int)bufferSize);

        try
        {
            while (true)
            {
                int status = Win32Interop.NtQuerySystemInformation(
                    Win32Interop.SystemProcessInformation,
                    buffer,
                    bufferSize,
                    out uint requiredLength);

                if (status == Win32Interop.STATUS_SUCCESS)
                    break;

                if (status != Win32Interop.STATUS_INFO_LENGTH_MISMATCH)
                    throw new InvalidOperationException($"NtQuerySystemInformation failed with NTSTATUS 0x{status:X8}");

                Marshal.FreeHGlobal(buffer);
                buffer = IntPtr.Zero;
                bufferSize = Math.Max(requiredLength + 64 * 1024, bufferSize * 2);
                buffer = Marshal.AllocHGlobal((int)bufferSize);
            }

            var results = new List<RawProcessEntry>(256);
            byte* current = (byte*)buffer;

            while (true)
            {
                var info = Marshal.PtrToStructure<Win32Interop.SYSTEM_PROCESS_INFORMATION>((IntPtr)current);
                int pid = info.UniqueProcessId.ToInt32();

                string name = pid == 0
                    ? "System Idle Process"
                    : (info.ImageName.Length > 0 && info.ImageName.Buffer != IntPtr.Zero
                        ? Marshal.PtrToStringUni(info.ImageName.Buffer, info.ImageName.Length / 2) ?? $"PID {pid}"
                        : $"PID {pid}");

                long totalWs = (long)info.WorkingSetSize;
                long privateWs = info.WorkingSetPrivateSize > 0
                    ? Math.Min(info.WorkingSetPrivateSize, totalWs)
                    : 0L;

                results.Add(new RawProcessEntry(
                    ProcessId: pid,
                    Name: name,
                    SessionId: info.SessionId,
                    PrivateWorkingSetBytes: privateWs,
                    TotalWorkingSetBytes: totalWs,
                    CommitBytes: (long)info.PrivatePageCount,
                    PagedPoolBytes: (long)info.QuotaPagedPoolUsage,
                    NonPagedPoolBytes: (long)info.QuotaNonPagedPoolUsage,
                    ThreadCount: info.NumberOfThreads,
                    HandleCount: info.HandleCount,
                    IoTransferBytes: info.ReadTransferCount + info.WriteTransferCount));

                if (info.NextEntryOffset == 0)
                    break;

                current += info.NextEntryOffset;
            }

            return results;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
                Marshal.FreeHGlobal(buffer);
        }
    }
}
