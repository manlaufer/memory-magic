# Memory Magic

A lightweight, native Windows system utility built on .NET 8 WPF and Win32/NT APIs with zero third-party runtime dependencies.

## Features

- **Memory Optimizer**: Safe working-set trimming, Deep Purge (Standby, Modified, and System File Cache), and automatic background RAM pressure monitoring.
- **Floating Performance Bar**: Live CPU, RAM, Network, Disk (Active %, Free Space, WMI Health), and Battery telemetry bar with smooth drag-to-move, position lock/pin, Always-on-Top, and taskbar pinning.
- **Process Profiler & Startup Manager**: Inspect per-process memory pools and toggle Windows startup entries safely.
- **Temp Cleaner & Windows Debloater**: Clean temporary caches and manage UWP/Win32 bloatware with System Restore Point protection and live progress reporting.
- **Windows Startup Integration**: Optional *Run at Windows Startup* setting launching minimized to the System Tray.

## Requirements

- Windows 10 or Windows 11 (x64)
- .NET 8 SDK (`net8.0-windows`) for building from source

## Building from Source

1. Build the standalone application executable (`MemoryMagic.exe`):
   ```powershell
   dotnet build src\MemoryMagic\MemoryMagic.csproj -c Release
   ```
2. Build the standalone Windows Setup installer (`MemoryMagic-Setup.exe`):
   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build-installer.ps1
   ```

## Documentation

See [UserGuide.html](UserGuide.html) for a complete beginner-friendly walkthrough of all features and settings.
