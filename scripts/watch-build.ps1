<#
.SYNOPSIS
    Watches src/MemoryMagic for source modifications and automatically rebuilds the root MemoryMagic.exe.
#>
[CmdletBinding()]
param(
    [int]$DebounceMs = 800
)

$ProjectRoot = Split-Path -Parent $PSScriptRoot
$WatchPath   = Join-Path $ProjectRoot "src\MemoryMagic"
$BuildScript = Join-Path $PSScriptRoot "build-exe.ps1"

Write-Host "[WATCH] Monitoring '$WatchPath' for changes. Press Ctrl+C to stop."
& powershell -NoProfile -ExecutionPolicy Bypass -File $BuildScript

$watcher = New-Object System.IO.FileSystemWatcher
$watcher.Path = $WatchPath
$watcher.IncludeSubdirectories = $true
$watcher.NotifyFilter = [System.IO.NotifyFilters]::LastWrite -bor [System.IO.NotifyFilters]::FileName -bor [System.IO.NotifyFilters]::CreationTime
$watcher.Filter = "*.*"
$watcher.EnableRaisingEvents = $true

$extensions = @(".cs", ".xaml", ".csproj", ".manifest")
$pendingBuild = $false
$lastChange = Get-Date

$onChange = {
    param($sender, $e)
    $full = $e.FullPath
    if ($full -match "\\(bin|obj)\\") { return }
    $ext = [System.IO.Path]::GetExtension($full)
    if ($extensions -contains $ext) {
        $script:pendingBuild = $true
        $script:lastChange = Get-Date
        Write-Host "[WATCH] Change detected: $($e.Name)"
    }
}

$handlers = @(
    (Register-ObjectEvent $watcher Changed -Action $onChange),
    (Register-ObjectEvent $watcher Created -Action $onChange),
    (Register-ObjectEvent $watcher Deleted -Action $onChange),
    (Register-ObjectEvent $watcher Renamed -Action $onChange)
)

try {
    while ($true) {
        Start-Sleep -Milliseconds 250
        if ($pendingBuild -and ((Get-Date) - $lastChange).TotalMilliseconds -ge $DebounceMs) {
            $pendingBuild = $false
            Write-Host "[WATCH] Rebuilding root executable..."
            & powershell -NoProfile -ExecutionPolicy Bypass -File $BuildScript
            if ($LASTEXITCODE -ne 0) {
                Write-Warning "[WATCH] Sync failed (exit $LASTEXITCODE). Check logs\build-pipeline.log."
            }
        }
    }
}
finally {
    $watcher.EnableRaisingEvents = $false
    foreach ($h in $handlers) { Unregister-Event -SubscriptionId $h.Id -ErrorAction SilentlyContinue }
    $watcher.Dispose()
}
