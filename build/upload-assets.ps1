<#
.SYNOPSIS    Uploads the packaged artifacts to a GitHub release, one at a time, verifying
    each upload before moving on.

.DESCRIPTION
    build\publish-github.ps1 does the repository + source + release work. This is a
    narrower, more defensive tool for the binaries alone.

    It exists because a batched upload over a throttled link proved destructive:
    GitHub's asset API is eventually consistent, so deleting an asset and
    immediately re-posting the same name races, and an upload interrupted mid-body
    can leave the release with fewer assets than the run reported. Here every asset
    is confirmed present (name, size and sha256 digest) before the next one starts,
    so a failure can never go unnoticed.
#>
[CmdletBinding()]
param(
    [string]$Token = $env:GITHUB_TOKEN,
    [string]$Owner = "cx928",
    [string]$Repo = "IPScaner-WinUI",
    [string]$Tag = "v1.28.2",
    [string]$ArtifactsDir,
    [int]$MaxAttempts = 4
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

if ([string]::IsNullOrWhiteSpace($Token)) { throw "No token. Pass -Token or set GITHUB_TOKEN." }

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $ArtifactsDir) { $ArtifactsDir = Join-Path $repoRoot "artifacts" }

$apiBase = "https://api.github.com"
$jsonHeaders = @{
    Authorization          = "Bearer $Token"
    Accept                 = "application/vnd.github+json"
    "X-GitHub-Api-Version" = "2022-11-28"
    "User-Agent"           = "IPScaner-Packager"
}

function Get-Release {
    Invoke-RestMethod -Method GET -Uri "$apiBase/repos/$Owner/$Repo/releases/tags/$Tag" -Headers $jsonHeaders
}

function Get-Assets([long]$releaseId) {
    $list = Invoke-RestMethod -Method GET -Uri "$apiBase/repos/$Owner/$Repo/releases/$releaseId/assets" -Headers $jsonHeaders
    return @($list)
}

function Remove-Asset([long]$assetId) {
    try {
        Invoke-RestMethod -Method DELETE -Uri "$apiBase/repos/$Owner/$Repo/releases/assets/$assetId" -Headers $jsonHeaders | Out-Null
        return $true
    }
    catch {
        $status = $_.Exception.Response.StatusCode.value__
        if ($status -eq 404) { return $true }   # already gone
        throw
    }
}

function Send-Asset([long]$releaseId, [string]$path) {
    $name = Split-Path $path -Leaf
    $uri = "https://uploads.github.com/repos/$Owner/$Repo/releases/$releaseId/assets?name=$([uri]::EscapeDataString($name))"

    # Invoke-RestMethod with -InFile streams the body and honours -TimeoutSec; it is
    # what has actually moved these 65-85 MB files over this link. (An HttpClient
    # variant was tried and dropped: the `using namespace` form does not resolve
    # [Http.StreamContent] inside a function here.)
    $params = @{
        Method        = "POST"
        Uri           = $uri
        Headers       = $jsonHeaders
        ContentType   = "application/octet-stream"
        InFile        = $path
        TimeoutSec    = 7200
        ErrorAction   = "Stop"
    }
    return Invoke-RestMethod @params
}

$release = Get-Release
Write-Host "release : $Tag (id $($release.id))"
Write-Host "uploading from: $ArtifactsDir"
Write-Host ""

$artifacts = Get-ChildItem $ArtifactsDir -File |
    Where-Object { $_.Extension -in @(".zip", ".msi", ".exe") } |
    Sort-Object Name

if ($artifacts.Count -eq 0) { throw "no artifacts found in $ArtifactsDir" }

$results = @()

foreach ($artifact in $artifacts) {
    $localHash = (Get-FileHash $artifact.FullName -Algorithm SHA256).Hash.ToLower()
    $sizeMb = [math]::Round($artifact.Length / 1MB, 1)
    Write-Host ("--- {0} ({1} MB)" -f $artifact.Name, $sizeMb)

    $done = $false
    for ($attempt = 1; $attempt -le $MaxAttempts -and -not $done; $attempt++) {
        $assets = Get-Assets $release.id
        $existing = $assets | Where-Object { $_.name -eq $artifact.Name } | Select-Object -First 1

        if ($existing -and $existing.size -eq $artifact.Length -and
            ($existing.digest -replace '^sha256:', '') -eq $localHash) {
            Write-Host "    already published and identical"
            $done = $true
            break
        }

        if ($existing) {
            Write-Host "    removing previous copy (id $($existing.id))"
            Remove-Asset $existing.id | Out-Null
            Start-Sleep -Seconds 5   # let the delete settle before re-posting the name
        }

        try {
            Write-Host "    uploading (attempt $attempt/$MaxAttempts)"
            $created = Send-Asset $release.id $artifact.FullName
            Write-Host "    server accepted: id=$($created.id) size=$($created.size)"

            # Never trust the POST response alone — read the release back.
            Start-Sleep -Seconds 3
            $verify = (Get-Assets $release.id) | Where-Object { $_.name -eq $artifact.Name } | Select-Object -First 1
            if ($verify -and $verify.size -eq $artifact.Length -and
                ($verify.digest -replace '^sha256:', '') -eq $localHash) {
                Write-Host "    verified on the release (size and sha256 match)"
                $done = $true
            }
            else {
                Write-Host "    verification failed after upload; will retry"
                if ($verify) { Remove-Asset $verify.id | Out-Null }
                Start-Sleep -Seconds 5
            }
        }
        catch {
            Write-Host "    upload error: $($_.Exception.Message)"
            Start-Sleep -Seconds (5 * $attempt)
        }
    }

    $results += [PSCustomObject]@{ Name = $artifact.Name; Ok = $done }
    if (-not $done) { Write-Warning "$($artifact.Name) could NOT be published" }
}

Write-Host ""
Write-Host "=== summary ==="
$results | ForEach-Object { "  {0,-46} {1}" -f $_.Name, $(if ($_.Ok) { "已发布并校验" } else { "失败" }) }

$final = Get-Assets $release.id
Write-Host ""
Write-Host "release now carries $($final.Count) asset(s):"
$final | Sort-Object name | ForEach-Object { "  {0,-46} {1,12} B" -f $_.name, $_.size }

if ($results.Ok -contains $false) { exit 1 }
