<#
.SYNOPSIS
    Builds every distributable form of IPScaner WinUI from one source tree.

.DESCRIPTION
    Produces, into `artifacts/`:

      IPScaner-<ver>-win-x64-portable.zip        self-contained: .NET + Windows App SDK
                                                 bundled, unzip and run (免安装版)
      IPScaner-<ver>-win-x64-portable-lite.zip   framework-dependent: needs the .NET 8
                                                 Desktop Runtime + Windows App Runtime 1.8
      IPScaner-<ver>-win-x64.msi                 Windows Installer package (安装版)
      IPScaner-<ver>-win-x64-setup.exe           bootstrapper EXE that runs the MSI

    Publishing is intentionally done with `dotnet publish` rather than by zipping
    a `bin` folder: only publish resolves the Windows App SDK runtime, the
    self-contained .NET runtime and the RID-specific native assets.

.PARAMETER Version
    Product version stamped into the file names and the MSI.

.PARAMETER SkipSelfContained
    Skip the (slow, ~210 MB) self-contained publish and its archives.

.EXAMPLE
    pwsh -File build\package.ps1 -Version 1.28.2
#>
[CmdletBinding()]
param(
    [string]$Version = "1.28.2",
    [ValidateSet("win-x64", "win-arm64")]
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release",
    [string]$ArtifactsDir = "artifacts",
    [switch]$SkipSelfContained,
    [switch]$SkipInstaller
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

# Resolve everything relative to the repository root, not the caller's cwd.
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "src\IPScaner.WinUI\IPScaner.WinUI.csproj"
$artifacts = Join-Path $repoRoot $ArtifactsDir

if (-not (Test-Path $project)) { throw "project not found: $project" }

function Write-Step([string]$text) {
    Write-Host ""
    Write-Host "=== $text ===" -ForegroundColor Cyan
}

function Invoke-DotNet([string[]]$arguments, [string]$what) {
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit $LASTEXITCODE)" }
}

function Get-FolderSizeMb([string]$path) {
    if (-not (Test-Path $path)) { return 0 }
    $sum = (Get-ChildItem $path -Recurse -File | Measure-Object -Property Length -Sum).Sum
    return [math]::Round($sum / 1MB, 1)
}

function New-Zip([string]$sourceDir, [string]$zipPath) {
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    # Compress-Archive is slow on ~500 files but has no external dependency.
    Compress-Archive -Path (Join-Path $sourceDir "*") -DestinationPath $zipPath -CompressionLevel Optimal
    return (Get-Item $zipPath).Length
}

New-Item -ItemType Directory -Force -Path $artifacts | Out-Null

$payloadPortable = Join-Path $artifacts "payload-portable"
$payloadLite = Join-Path $artifacts "payload-lite"

$zipPortable = Join-Path $artifacts "IPScaner-$Version-$Runtime-portable.zip"
$zipLite = Join-Path $artifacts "IPScaner-$Version-$Runtime-portable-lite.zip"
$msiPath = Join-Path $artifacts "IPScaner-$Version-$Runtime.msi"
$setupPath = Join-Path $artifacts "IPScaner-$Version-$Runtime-setup.exe"

$summary = [ordered]@{}

# ---------------------------------------------------------------- portable (self-contained)
if (-not $SkipSelfContained) {
    Write-Step "publish: self-contained ($Runtime)"
    if (Test-Path $payloadPortable) { Remove-Item $payloadPortable -Recurse -Force }
    Invoke-DotNet @(
        "publish", $project,
        "-c", $Configuration,
        "-r", $Runtime,
        "--self-contained", "true",
        "-p:WindowsAppSDKSelfContained=true",
        "-p:DebugType=none",
        "-o", $payloadPortable,
        "--nologo"
    ) "self-contained publish"

    Write-Step "zip: portable"
    $size = New-Zip $payloadPortable $zipPortable
    $summary["portable.zip"] = "payload $(Get-FolderSizeMb $payloadPortable) MB -> zip $([math]::Round($size/1MB,1)) MB"
}

# ---------------------------------------------------------------- portable-lite (framework-dependent)
Write-Step "publish: framework-dependent ($Runtime)"
if (Test-Path $payloadLite) { Remove-Item $payloadLite -Recurse -Force }
Invoke-DotNet @(
    "publish", $project,
    "-c", $Configuration,
    "-r", $Runtime,
    "--self-contained", "false",
    "-p:DebugType=none",
    "-o", $payloadLite,
    "--nologo"
) "framework-dependent publish"

Write-Step "zip: portable-lite"
$sizeLite = New-Zip $payloadLite $zipLite
$summary["portable-lite.zip"] = "payload $(Get-FolderSizeMb $payloadLite) MB -> zip $([math]::Round($sizeLite/1MB,1)) MB"

# ---------------------------------------------------------------- MSI
if (-not $SkipInstaller) {
    Write-Step "msi: WiX"

    $wix = Join-Path $env:USERPROFILE ".dotnet\tools\wix.exe"
    if (-not (Test-Path $wix)) { throw "WiX not found. Install it with: dotnet tool install --global wix" }

    # Build the MSI from the self-contained payload so the installed app needs no
    # prerequisites; fall back to the lite payload when that was skipped.
    $msiPayload = if (Test-Path $payloadPortable) { $payloadPortable } else { $payloadLite }
    if (-not (Test-Path $msiPayload)) { throw "no publish payload available for the MSI" }

    $wxsPath = Join-Path $artifacts "IPScaner.wxs"
    $templatePath = Join-Path $PSScriptRoot "installer\IPScaner.wxs.template"
    if (-not (Test-Path $templatePath)) { throw "missing template: $templatePath" }

    $wxs = Get-Content $templatePath -Raw
    $wxs = $wxs.Replace("@VERSION@", $Version)
    $wxs = $wxs.Replace("@PAYLOAD@", $msiPayload)
    $wxs = $wxs.Replace("@ICON@", (Join-Path $msiPayload "Assets\app.ico"))
    Set-Content -Path $wxsPath -Value $wxs -Encoding UTF8

    if (Test-Path $msiPath) { Remove-Item $msiPath -Force }
    & $wix build $wxsPath -arch x64 -o $msiPath
    if ($LASTEXITCODE -ne 0) { throw "wix build failed (exit $LASTEXITCODE)" }
    $summary["installer.msi"] = "$([math]::Round((Get-Item $msiPath).Length/1MB,1)) MB ($(Split-Path $msiPayload -Leaf) payload)"

    # ------------------------------------------------------------ setup.exe bootstrapper
    Write-Step "exe: IExpress bootstrapper around the MSI"
    $sedPath = Join-Path $artifacts "setup.sed"
    $sedTemplate = Join-Path $PSScriptRoot "installer\setup.sed.template"
    if (Test-Path $sedTemplate) {
        $msiFileName = Split-Path $msiPath -Leaf
        $sed = Get-Content $sedTemplate -Raw
        $sed = $sed.Replace("@SETUPEXE@", $setupPath)
        $sed = $sed.Replace("@FRIENDLYNAME@", "IPScaner Setup")
        $sed = $sed.Replace("@MSIFILE@", $msiFileName)
        $sed = $sed.Replace("@MSIDIR@", (Split-Path $msiPath -Parent))
        # IExpress is a legacy ANSI tool: the SED has to be in the system code page.
        [System.IO.File]::WriteAllText($sedPath, $sed, [System.Text.Encoding]::GetEncoding(936))

        if (Test-Path $setupPath) { Remove-Item $setupPath -Force }
        # IExpress is part of Windows and needs no extra tooling. It gives no
        # console output, takes ~30 s to compress a 65 MB payload, and offers no
        # progress signal, so poll for the output file instead of guessing a sleep.
        $iexpress = Join-Path $env:SystemRoot "System32\iexpress.exe"
        $proc = Start-Process $iexpress -ArgumentList @("/N", $sedPath) -PassThru
        $deadline = (Get-Date).AddMinutes(4)
        while ((Get-Date) -lt $deadline -and -not $proc.HasExited) { Start-Sleep -Seconds 3 }
        while ((Get-Date) -lt $deadline -and -not (Test-Path $setupPath)) { Start-Sleep -Seconds 3; if ($proc.HasExited) { break } }

        if (Test-Path $setupPath) {
            $summary["setup.exe"] = "$([math]::Round((Get-Item $setupPath).Length/1MB,1)) MB"
        } else {
            $summary["setup.exe"] = "FAILED - inspect $sedPath"
        }
    } else {
        $summary["setup.exe"] = "skipped (no setup.sed.template)"
    }
}

# ---------------------------------------------------------------- report
Write-Step "artifacts"
Get-ChildItem $artifacts -File | Sort-Object Name |
    Select-Object Name, @{ n = "MB"; e = { [math]::Round($_.Length / 1MB, 1) } } |
    Format-Table -AutoSize

Write-Host "summary"
foreach ($k in $summary.Keys) { Write-Host ("  {0,-22} {1}" -f $k, $summary[$k]) }
