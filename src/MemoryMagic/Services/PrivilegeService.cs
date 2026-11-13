using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using MemoryMagic.Native;

namespace MemoryMagic.Services;

/// <summary>
/// Manages UAC elevation checks, Win32 token privileges, and critical system process guardrails.
/// </summary>
public sealed class PrivilegeService
{
    private static readonly HashSet<string> ProtectedProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System Idle Process",
        "Idle",
        "System",
        "Registry",
        "Secure System",
        "Memory Compression",
        "smss",
        "smss.exe",
        "csrss",
        "csrss.exe",
        "wininit",
        "wininit.exe",
        "services",
        "services.exe",
        "lsass",
        "lsass.exe",
        "lsaiso",
        "lsaiso.exe",
        "winlogon",
        "winlogon.exe",
        "fontdrvhost",
        "fontdrvhost.exe",
        "dwm",
        "dwm.exe",
        "audiodg",
        "audiodg.exe",
        "MsMpEng",
        "MsMpEng.exe",
        "NisSrv",
        "NisSrv.exe",
        "vmmem",
        "vmmemWSL"
    };

    private readonly AuditLogger _logger;

    public PrivilegeService(AuditLogger logger)
    {
        _logger = logger;
    }

    public bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Attempts to enable a named Windows privilege on the current process token.
    /// Returns false gracefully if the token does not possess the privilege (e.g., unelevated).
    /// </summary>
    public bool TryEnablePrivilege(string privilegeName)
    {
        IntPtr tokenHandle = IntPtr.Zero;
        try
        {
            if (!Win32Interop.OpenProcessToken(
                    Win32Interop.GetCurrentProcess(),
                    Win32Interop.TOKEN_ADJUST_PRIVILEGES | Win32Interop.TOKEN_QUERY,
                    out tokenHandle))
            {
                return false;
            }

            if (!Win32Interop.LookupPrivilegeValueW(null, privilegeName, out var luid))
            {
                return false;
            }

            var tp = new Win32Interop.TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Privileges = new Win32Interop.LUID_AND_ATTRIBUTES
                {
                    Luid = luid,
                    Attributes = Win32Interop.SE_PRIVILEGE_ENABLED
                }
            };

            if (!Win32Interop.AdjustTokenPrivileges(tokenHandle, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero))
            {
                return false;
            }

            int lastError = Marshal.GetLastWin32Error();
            return lastError != Win32Interop.ERROR_NOT_ALL_ASSIGNED;
        }
        finally
        {
            if (tokenHandle != IntPtr.Zero)
                Win32Interop.CloseHandle(tokenHandle);
        }
    }

    /// <summary>
    /// Guardrail: determines whether a process is a kernel/critical OS component that must never be
    /// terminated or forcibly destabilized.
    /// </summary>
    public bool IsProtectedOrCriticalProcess(int pid, string processName, out bool isKernelCritical)
    {
        isKernelCritical = false;

        if (pid <= 4)
        {
            isKernelCritical = true;
            return true;
        }

        if (ProtectedProcessNames.Contains(processName))
        {
            isKernelCritical = true;
            return true;
        }

        IntPtr hProcess = Win32Interop.OpenProcess(Win32Interop.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProcess == IntPtr.Zero)
        {
            // Cannot even query limited info (often PPL/Protected Process Light like csrss/antimalware).
            return true;
        }

        try
        {
            if (Win32Interop.IsProcessCritical(hProcess, out bool critical) && critical)
            {
                isKernelCritical = true;
                return true;
            }
        }
        finally
        {
            Win32Interop.CloseHandle(hProcess);
        }

        return false;
    }

    /// <summary>
    /// Relaunches Memory Magic with an elevated UAC prompt.
    /// </summary>
    public bool RestartElevated()
    {
        try
        {
            string? exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath))
                return false;

            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
                Verb = "runas"
            };

            _logger.Info("UAC", "Initiating elevated relaunch via UAC.");
            Process.Start(startInfo);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warn("UAC", $"Elevation canceled or failed: {ex.Message}");
            return false;
        }
    }
}
