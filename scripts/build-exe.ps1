<#
.SYNOPSIS
    Terminates any running MemoryMagic.exe, performs a clean single-file publish,
    atomically overwrites the root executable, and verifies the updated timestamp.
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$SelfContained = $true,
    [switch]$LaunchAfterBuild = $false
)

$ErrorActionPreference = "Stop"

$BuildStartTime = Get-Date
$ProjectRoot = Split-Path -Parent $PSScriptRoot
$ProjectFile = Join-Path $ProjectRoot "src\MemoryMagic\MemoryMagic.csproj"
$ProjectDir  = Split-Path -Parent $ProjectFile
$StagingDir  = Join-Path $ProjectRoot "artifacts\staging"
$LogDir      = Join-Path $ProjectRoot "logs"
$LogFile     = Join-Path $LogDir "build-pipeline.log"
$RootExe     = Join-Path $ProjectRoot "MemoryMagic.exe"
$StagedExe   = Join-Path $StagingDir "MemoryMagic.exe"

if (-not (Test-Path $LogDir)) {
    New-Item -ItemType Directory -Path $LogDir -Force | Out-Null
}

function Write-BuildLog {
    param(
        [string]$Level,
        [string]$Message
    )
    $ts = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $line = "[$ts] [$Level] $Message"
    Add-Content -Path $LogFile -Value $line -Encoding UTF8
    if ($Level -eq "ERROR") {
        Write-Error $line -ErrorAction Continue
    } elseif ($Level -eq "WARN") {
        Write-Warning $line
    } else {
        Write-Host $line
    }
}

function Resolve-DotnetCli {
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) {
        $sdks = & $cmd.Source --list-sdks 2>$null
        if ($sdks -match "^8\.") {
            return $cmd.Source
        }
    }

    $userDotnet = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"
    if (Test-Path $userDotnet) {
        return $userDotnet
    }

    throw "No .NET 8 SDK found on PATH or at '$userDotnet'."
}

function Stop-RunningMemoryMagic {
    $running = Get-Process -Name "MemoryMagic" -ErrorAction SilentlyContinue
    if (-not $running) {
        Write-BuildLog "INFO" "No running MemoryMagic.exe instance detected."
        return
    }

    $pids = ($running | ForEach-Object { $_.Id }) -join ", "
    Write-BuildLog "INFO" "Terminating running MemoryMagic.exe process(es) [PID(s): $pids] via taskkill..."
    & taskkill.exe /F /IM MemoryMagic.exe /T | Out-Null

    # Wait up to 5 seconds for OS file handle release
    for ($i = 0; $i -lt 20; $i++) {
        Start-Sleep -Milliseconds 250
        if (-not (Get-Process -Name "MemoryMagic" -ErrorAction SilentlyContinue)) {
            break
        }
    }

    if (Get-Process -Name "MemoryMagic" -ErrorAction SilentlyContinue) {
        throw "Failed to terminate running MemoryMagic.exe instance(s)."
    }
}

function Test-FileLocked {
    param([string]$Path)
    if (-not (Test-Path $Path)) {
        return $false
    }
    try {
        $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
        $stream.Close()
        $stream.Dispose()
        return $false
    } catch {
        return $true
    }
}

try {
    $dotnet = Resolve-DotnetCli
    Write-BuildLog "INFO" "Starting clean executable build & sync using '$dotnet' ($Configuration | $Runtime)."

    # 1. Terminate any running MemoryMagic.exe so Windows cannot lock the root executable
    Stop-RunningMemoryMagic

    if (Test-FileLocked -Path $RootExe) {
        Write-BuildLog "ERROR" "Root executable '$RootExe' remains locked by another handle after process termination."
        exit 3
    }

    if (Test-Path $RootExe) {
        Remove-Item -Path $RootExe -Force
        Write-BuildLog "INFO" "Deleted old executable '$RootExe'."
    }

    # 2. Clean stale publish caches and staging artifacts
    $stalePaths = @(
        $StagingDir,
        (Join-Path $ProjectDir "bin\$Configuration\net8.0-windows\$Runtime"),
        (Join-Path $ProjectDir "obj\$Configuration\net8.0-windows\$Runtime")
    )
    foreach ($p in $stalePaths) {
        if (Test-Path $p) {
            Remove-Item -Path $p -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    & $dotnet clean $ProjectFile -c $Configuration -r $Runtime -p:IsInnerSyncPublish=true --nologo | Out-Null
    New-Item -ItemType Directory -Path $StagingDir -Force | Out-Null

    # 3. Clean publish single-file self-contained executable
    $publishArgs = @(
        "publish", $ProjectFile,
        "-c", $Configuration,
        "-r", $Runtime,
        "--self-contained", ($SelfContained.IsPresent.ToString().ToLowerInvariant()),
        "-p:PublishSingleFile=true",
        "-p:IncludeNativeLibrariesForSelfExtract=true",
        "-p:EnableCompressionInSingleFile=true",
        "-p:DebugType=none",
        "-p:DebugSymbols=false",
        "-p:IsInnerSyncPublish=true",
        "-o", $StagingDir
    )

    $output = & $dotnet @publishArgs 2>&1
    if ($LASTEXITCODE -ne 0) {
        $details = ($output | Out-String).Trim()
        Write-BuildLog "ERROR" "dotnet publish failed (exit $LASTEXITCODE). Details:`n$details"
        exit $LASTEXITCODE
    }

    if (-not (Test-Path $StagedExe)) {
        Write-BuildLog "ERROR" "Publish succeeded but staged executable was not found at '$StagedExe'."
        exit 2
    }

    # 4. Unconditionally overwrite root executable and remove staging copy
    $stagedHash = (Get-FileHash -Path $StagedExe -Algorithm SHA256).Hash
    Copy-Item -Path $StagedExe -Destination $RootExe -Force
    $rootItem = Get-Item -Path $RootExe
    $rootItem.LastWriteTime = Get-Date
    $rootHash = (Get-FileHash -Path $RootExe -Algorithm SHA256).Hash

    if ($stagedHash -ne $rootHash) {
        Write-BuildLog "ERROR" "Hash mismatch after copying executable (staged=$stagedHash, root=$rootHash)."
        exit 4
    }

    Remove-Item -Path $StagingDir -Recurse -Force -ErrorAction SilentlyContinue

    # 5. Mandatory post-build timestamp & size verification
    $verified = Get-Item -Path $RootExe
    if ($verified.LastWriteTime -lt $BuildStartTime) {
        Write-BuildLog "ERROR" "Verification failed: '$RootExe' LastWriteTime ($($verified.LastWriteTime)) is older than build start ($BuildStartTime)."
        exit 5
    }

    if ($verified.Length -lt 50MB) {
        Write-BuildLog "ERROR" "Verification failed: '$RootExe' size ($($verified.Length) bytes) is smaller than expected self-contained single-file binary."
        exit 6
    }

    $sizeMb = [math]::Round($verified.Length / 1MB, 2)
    Write-BuildLog "INFO" "Verified '$RootExe' | LastWriteTime=$($verified.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss')) | Size=$sizeMb MB | SHA256=$rootHash"
    if ($LaunchAfterBuild) {
        Start-Process -FilePath $RootExe
        Write-BuildLog "INFO" "Launched '$RootExe'."
    }
    exit 0
}
catch {
    Write-BuildLog "ERROR" "Build pipeline exception: $($_.Exception.Message)"
    exit 1
}
