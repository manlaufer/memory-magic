<#
.SYNOPSIS
    Compresses MemoryMagic.exe into an embedded payload and builds the standalone
    self-contained WPF Windows Setup executable (MemoryMagic-Setup.exe) in the project root.
#>
[CmdletBinding()]
param(
    [string]$OutputInstaller = ""
)

$ErrorActionPreference = "Stop"

$ProjectRoot = Split-Path -Parent $PSScriptRoot
$RootExe = Join-Path $ProjectRoot "MemoryMagic.exe"
$SetupProject = Join-Path $ProjectRoot "src\MemoryMagic.Setup\MemoryMagic.Setup.csproj"
$SetupResourceDir = Join-Path $ProjectRoot "src\MemoryMagic.Setup\Resources"
$CompressedPayload = Join-Path $SetupResourceDir "MemoryMagic.exe.gz"
$StagingDir = Join-Path $ProjectRoot "artifacts\installer-staging"
$ArtifactsDir = Join-Path $ProjectRoot "artifacts"

if ([string]::IsNullOrWhiteSpace($OutputInstaller)) {
    $OutputInstaller = Join-Path $ProjectRoot "MemoryMagic-Setup.exe"
}

if (-not (Test-Path $RootExe)) {
    throw "Root executable not found at '$RootExe'. Run scripts\build-exe.ps1 first."
}
if (-not (Test-Path $SetupProject)) {
    throw "Setup project not found at '$SetupProject'."
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

$dotnet = Resolve-DotnetCli

# Remove previous installer and staging files
if (Test-Path $OutputInstaller) {
    Remove-Item -Path $OutputInstaller -Force
}
if (Test-Path $StagingDir) {
    Remove-Item -Path $StagingDir -Recurse -Force
}
New-Item -ItemType Directory -Path $StagingDir -Force | Out-Null
New-Item -ItemType Directory -Path $ArtifactsDir -Force | Out-Null
New-Item -ItemType Directory -Path $SetupResourceDir -Force | Out-Null

# Compress MemoryMagic.exe into embedded GZip resource
Write-Host "Compressing MemoryMagic.exe payload..."
$inStream = [System.IO.File]::OpenRead($RootExe)
try {
    $outStream = [System.IO.File]::Create($CompressedPayload)
    try {
        $gzipStream = New-Object System.IO.Compression.GZipStream($outStream, [System.IO.Compression.CompressionLevel]::Optimal)
        try {
            $inStream.CopyTo($gzipStream)
        } finally {
            $gzipStream.Dispose()
        }
    } finally {
        $outStream.Dispose()
    }
} finally {
    $inStream.Dispose()
}

Write-Host "Publishing standalone WPF Windows Setup: $OutputInstaller"
$publishArgs = @(
    "publish", $SetupProject,
    "-c", "Release",
    "-r", "win-x64",
    "--self-contained", "true",
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:EnableCompressionInSingleFile=true",
    "-p:DebugType=none",
    "-p:DebugSymbols=false",
    "-o", $StagingDir
)

$output = & $dotnet @publishArgs 2>&1
if ($LASTEXITCODE -ne 0) {
    $details = ($output | Out-String).Trim()
    throw "dotnet publish for MemoryMagic.Setup failed (exit $LASTEXITCODE):`n$details"
}

$stagedSetup = Join-Path $StagingDir "MemoryMagic-Setup.exe"
if (-not (Test-Path $stagedSetup)) {
    throw "Publish succeeded but MemoryMagic-Setup.exe was not found at '$stagedSetup'."
}

Copy-Item -Path $stagedSetup -Destination $OutputInstaller -Force

# Clean up staging and temporary compressed resource
Remove-Item -Path $StagingDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -Path $CompressedPayload -Force -ErrorAction SilentlyContinue

$info = Get-Item $OutputInstaller
Write-Host ("Installer built successfully: {0} ({1:N2} MB)" -f $info.FullName, ($info.Length / 1MB))
