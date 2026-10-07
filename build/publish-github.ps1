<#
.SYNOPSIS
    Creates the GitHub repository, pushes the source tree, and publishes a release
    with the packaged installers.

.DESCRIPTION
    Deliberately uses the GitHub REST API rather than git: this machine has no git
    (or gh) installed, and the API can create a repository, build a commit from a
    file list, and upload release assets without one.

    Steps:
      1. verify the token and resolve the login
      2. create the repository (skip when it already exists)
      3. create a git tree + commit + refs/heads/main from the tracked source files
      4. create the release for the tag
      5. upload every artifact under artifacts/ as a release asset

    The token needs: Administration:write (to create the repo) and
    Contents:write (to push and to upload assets).

.PARAMETER Token
    Personal access token. If omitted, $env:GITHUB_TOKEN is used.

.PARAMETER Owner
    Account or organisation that will own the repository. Defaults to the
    authenticated user.

.PARAMETER Repo
    Repository name.

.PARAMETER Visibility
    public or private.

.EXAMPLE
    pwsh -File build\publish-github.ps1 -Token $env:GH_PAT -Repo IPScaner-WinUI -Visibility public
#>
[CmdletBinding()]
param(
    [string]$Token = $env:GITHUB_TOKEN,
    [string]$Owner,
    [string]$Repo = "IPScaner-WinUI",
    [ValidateSet("public", "private")]
    [string]$Visibility = "public",
    [string]$Version = "1.28.2",
    [string]$Branch = "main",
    [string]$CommitMessage = "IPScaner WinUI v1.28.2 — WinUI 3 重构版"
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

if ([string]::IsNullOrWhiteSpace($Token)) {
    throw "No token. Pass -Token or set GITHUB_TOKEN."
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$artifactsDir = Join-Path $repoRoot "artifacts"

$apiBase = "https://api.github.com"
$headers = @{
    Authorization          = "Bearer $Token"
    Accept                 = "application/vnd.github+json"
    "X-GitHub-Api-Version" = "2022-11-28"
    "User-Agent"           = "IPScaner-Packager"
}

function Write-Step([string]$t) { Write-Host ""; Write-Host "=== $t ===" -ForegroundColor Cyan }

function Invoke-Api {
    param(
        [string]$Method,
        [string]$Uri,
        $Body,
        [string]$ContentType = "application/json"
    )
    $params = @{ Method = $Method; Uri = $Uri; Headers = $headers; ErrorAction = "Stop" }
    if ($null -ne $Body) {
        if ($Body -is [string]) { $params.Body = $Body }
        else { $params.Body = ($Body | ConvertTo-Json -Depth 100 -Compress) }
    }
    if ($ContentType) { $params.ContentType = $ContentType }

    # A publish run makes ~130 API calls (one blob per file). GitHub is reached
    # across a heavily throttled link here, so a bare connection reset ("远程主机强迫
    # 关闭了一个现有的连接") is routine rather than exceptional and would otherwise
    # throw away the whole run. Retry transient failures — no HTTP status (network),
    # 5xx, 429 — with a growing backoff. A 4xx that carries a status is a real
    # answer (404, 422, ...) and is rethrown immediately.
    $attempts = 5
    for ($attempt = 1; $attempt -le $attempts; $attempt++) {
        try {
            return Invoke-RestMethod @params
        }
        catch {
            $status = $_.Exception.Response.StatusCode.value__
            $message = "$($_.Exception.Message)"

            # 400 usually means a real client error, but GitHub answers a truncated
            # body with "We received a malformed request from your client", and this
            # link resets mid-request often enough that it is worth another try.
            $isTruncated = ($status -eq 400) -and ($message -match 'malformed')

            # 401/403/404/409/422 are real answers; retrying them just wastes time.
            $permanent = $status -in @(401, 403, 404, 409, 422)
            $transient = (-not $status) -or ($status -ge 500) -or ($status -eq 429) -or $isTruncated

            if ($attempt -ge $attempts -or $permanent -or -not $transient) { throw }
            $wait = [math]::Min(2 * $attempt, 10)
            Write-Host ("    transient failure ({0}); retry {1}/{2} in {3}s" -f `
                $(if ($status) { "HTTP $status" } else { "network" }), $attempt, $attempts, $wait)
            Start-Sleep -Seconds $wait
        }
    }
}

# ---------------------------------------------------------------- 1. identity
Write-Step "verify token"
$me = Invoke-Api -Method GET -Uri "$apiBase/user"
if (-not $Owner) { $Owner = $me.login }
Write-Host "authenticated as : $($me.login)"
Write-Host "target repository: $Owner/$Repo ($Visibility)"

# ---------------------------------------------------------------- 2. repository
Write-Step "create repository"
$repoUri = "$apiBase/repos/$Owner/$Repo"
try {
    $existing = Invoke-Api -Method GET -Uri $repoUri
    Write-Host "repository already exists, reusing it"
    $repoInfo = $existing
}
catch {
    $status = $_.Exception.Response.StatusCode.value__
    if ($status -ne 404) { throw }
    $repoInfo = Invoke-Api -Method POST -Uri "$apiBase/user/repos" -Body @{
        name        = $Repo
        description = "局域网IP扫描工具 —— IPScaner V1.28.2 的 WinUI 3 重构版"
        private     = ($Visibility -eq "private")
        has_issues  = $true
        has_wiki    = $false
        auto_init   = $false
    }
    Write-Host "created: $($repoInfo.html_url)"
}
$repoUri = $repoInfo.url

# ---------------------------------------------------------------- 3. source tree
Write-Step "build source commit"

# A brand-new repository has no refs, and the Git Data API answers
# "Git Repository is empty" (409) when asked to create a tree there. Bootstrap one
# commit through the Contents API first; after that the tree API behaves normally.
$ref = $null
try {
    $ref = Invoke-Api -Method GET -Uri "$repoUri/git/ref/heads/$Branch"
    Write-Host "branch $Branch already exists at $($ref.object.sha)"
}
catch {
    $status = $_.Exception.Response.StatusCode.value__
    if ($status -notin @(404, 409)) { throw }
    Write-Host "repository is empty; creating an initial commit to bootstrap $Branch"

    $seed = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(
        "# IPScaner-WinUI`n`n局域网IP扫描工具 —— IPScaner V1.28.2 的 WinUI 3 重构版。`n"))

    Invoke-Api -Method PUT -Uri "$repoUri/contents/README.md" -Body @{
        message = "chore: initialize repository"
        content = $seed
        branch  = $Branch
    } | Out-Null

    # The ref may take a moment to become visible to the Git Data API.
    for ($attempt = 0; $attempt -lt 10 -and $null -eq $ref; $attempt++) {
        Start-Sleep -Seconds 2
        try { $ref = Invoke-Api -Method GET -Uri "$repoUri/git/ref/heads/$Branch" } catch { }
    }
    if ($null -eq $ref) { throw "bootstrap commit did not create refs/heads/$Branch" }
    Write-Host "bootstrapped at $($ref.object.sha)"
}

# Everything except build output and the packaged binaries; artifacts ship as
# release assets instead of being committed.
$exclude = '\\(bin|obj|artifacts|_work|\.git|\.vs)\\'
$files = Get-ChildItem $repoRoot -Recurse -File -Force |
    Where-Object { $_.FullName -notmatch $exclude }

Write-Host "tracked files: $($files.Count)"

# Each file becomes a blob first, then the tree references it by sha.
#
# Do NOT try to inline the bytes in the tree entry: tree[].content only accepts
# UTF-8 *text*, and there is no encoding parameter for it on the Trees API. Passing
# base64 there stores the base64 string itself as the file body (which is exactly
# what happened on the first publish — every file in the repo was its own base64
# text). The base64 option belongs to the Blobs API, which is what we use here.
#
# Unchanged files reuse the blob already in the tree. That is not just an
# optimisation: this link drops connections often, and cutting ~130 POSTs down to
# only the files that actually changed is what makes a re-run reliable. A git blob
# id is sha1("blob <length>\0<content>"), so it can be computed locally.
$existingBlobs = @{}
try {
    $currentTree = Invoke-Api -Method GET -Uri "$repoUri/git/trees/$($ref.object.sha)?recursive=1"
    foreach ($entry in $currentTree.tree) {
        if ($entry.type -eq 'blob') { $existingBlobs[$entry.path] = $entry.sha }
    }
    Write-Host "existing blobs available for reuse: $($existingBlobs.Count)"
} catch {
    Write-Warning "could not read the current tree; every file will be uploaded"
}

function Get-GitBlobSha([byte[]]$bytes) {
    $header = [System.Text.Encoding]::ASCII.GetBytes("blob $($bytes.Length)`0")
    $buffer = New-Object byte[] ($header.Length + $bytes.Length)
    [Array]::Copy($header, 0, $buffer, 0, $header.Length)
    [Array]::Copy($bytes, 0, $buffer, $header.Length, $bytes.Length)
    $sha1 = [System.Security.Cryptography.SHA1]::Create()
    try { return (($sha1.ComputeHash($buffer) | ForEach-Object { $_.ToString('x2') }) -join '') }
    finally { $sha1.Dispose() }
}

$tree = @()
$blobIndex = 0
$created = 0
$reused = 0
foreach ($f in $files) {
    $rel = $f.FullName.Substring($repoRoot.Length + 1) -replace '\\', '/'
    $blobIndex++

    $bytes = [System.IO.File]::ReadAllBytes($f.FullName)
    $localSha = Get-GitBlobSha $bytes

    if ($existingBlobs[$rel] -eq $localSha) {
        $tree += @{ path = $rel; mode = "100644"; type = "blob"; sha = $localSha }
        $reused++
        continue
    }

    $blob = Invoke-Api -Method POST -Uri "$repoUri/git/blobs" -Body @{
        content  = [System.Convert]::ToBase64String($bytes)
        encoding = "base64"
    }
    $created++

    if (($created % 10) -eq 0) { Write-Host "  ... $created new blobs ($blobIndex / $($files.Count) files)" }

    $tree += @{
        path = $rel
        mode = "100644"
        type = "blob"
        sha  = $blob.sha
    }
}
Write-Host "blobs: $created created, $reused reused -> tree of $($tree.Count) entries"

$treeResult = Invoke-Api -Method POST -Uri "$repoUri/git/trees" -Body @{ tree = $tree }
Write-Host "tree: $($treeResult.sha)"

$parents = @($ref.object.sha)
$commitBody = @{ message = $CommitMessage; tree = $treeResult.sha; parents = $parents }
$commit = Invoke-Api -Method POST -Uri "$repoUri/git/commits" -Body $commitBody
Write-Host "commit: $($commit.sha)"

Invoke-Api -Method PATCH -Uri "$repoUri/git/refs/heads/$Branch" -Body @{ sha = $commit.sha; force = $false } | Out-Null
Write-Host "branch $Branch -> $($commit.sha)"

# ---------------------------------------------------------------- 4. release
Write-Step "create release v$Version"

$notes = @"
## 局域网IP扫描工具 —— WinUI 3 重构版 v$Version

[IPScaner V1.28.2](https://github.com/$Owner/$Repo) 的 WinUI 3 重构版本：界面层使用 Windows App SDK / WinUI 3，
扫描引擎拆分为可独立测试的 ``IPScaner.Core`` 类库。

### 下载哪个？

| 文件 | 适用场景 |
|---|---|
| ``IPScaner-$Version-win-x64-portable.zip`` | **免安装（推荐）**。已内置 .NET 8 与 Windows App SDK，解压即用，不写注册表。 |
| ``IPScaner-$Version-win-x64-portable-lite.zip`` | 免安装，体积小。需预先安装 .NET 8 桌面运行时与 Windows App Runtime 1.8。 |
| ``IPScaner-$Version-win-x64.msi`` | 安装版（Windows Installer）。安装到 Program Files，创建开始菜单与桌面快捷方式。 |
| ``IPScaner-$Version-win-x64-setup.exe`` | 安装版（EXE 引导程序，内部调用上面的 MSI）。 |

免安装版把配置保存在程序目录，安装版保存在 ``%APPDATA%\IPScaner``（Program Files 不可写时的自动回退）。

### 数据文件兼容

``IPScaner.cfg`` / ``IPScanerMemo.dat`` / ``command.txt`` / ``ipScaner_his.xml`` 与旧版格式**完全一致**，
可直接沿用原有文件。配置文件已验证为**逐字节往返一致**。

### 主要修复（相对旧版）

- ARP 表读取由子串匹配改为精确匹配（``192.168.1.5`` 不再误命中 ``192.168.1.50``）
- 停止扫描会立即取消并丢弃在途结果，不再出现「停止后色块仍被改写」
- IP 地址逐段范围校验（旧版会放行 ``999.999.999.1`` 并抛异常）
- 配置数值越界自动钳位（旧版会导致选项窗口无法打开）
- 本机端口占用改用 GetExtendedTcpTable/UDP，可见 TCP 状态与 IPv6
- WiFi 密码解析不再依赖中文标记（旧版在英文系统上取不到任何结果）
- 修改本地IP 按 netsh 真实退出码报告（旧版无条件提示成功）
- 端口列表支持范围写法；掩码位开放 /31 与 /32
- 导出 CSV 带 UTF-8 BOM，并新增真正的 .xlsx

### 已知限制

- ICMP 被安全软件拦截时，需在【选项配置】中启用「Ping失败时侦测端口」；程序会在整段扫描失败时主动提示
- 主界面的 IP 异动监测与旧版 29 项菜单树尚未移植
"@

try {
    $release = Invoke-Api -Method POST -Uri "$repoUri/releases" -Body @{
        tag_name         = "v$Version"
        target_commitish = $Branch
        name             = "IPScaner WinUI v$Version"
        body             = $notes
        draft            = $false
        prerelease       = $false
    }
    Write-Host "release: $($release.html_url)"
}
catch {
    $status = $_.Exception.Response.StatusCode.value__
    if ($status -eq 422) {
        Write-Host "release v$Version already exists, reusing it"
        $release = Invoke-Api -Method GET -Uri "$repoUri/releases/tags/v$Version"
    } else { throw }
}

# ---------------------------------------------------------------- 5. assets
Write-Step "upload release assets"

$assetFiles = Get-ChildItem $artifactsDir -File |
    Where-Object { $_.Extension -in @(".zip", ".msi", ".exe") } |
    Sort-Object Name

if ($assetFiles.Count -eq 0) { Write-Warning "no artifacts found in $artifactsDir" }

# The release may already carry assets from an earlier run; uploading the same
# name again fails with 422 already_exists. Skip identical ones and replace
# changed ones so the script stays re-runnable.
#
# This endpoint returns a JSON *array*, not an object with a `value` property.
# Reading `.value` here silently yields nothing, and every re-run then dies on
# already_exists. The @() also collapses PowerShell's single-element unwrapping
# so the Where-Object below always sees a collection.
$existingAssets = @()
try {
    $existingAssets = @(Invoke-Api -Method GET -Uri "$repoUri/releases/$($release.id)/assets")
} catch {
    Write-Warning "could not list existing release assets: $($_.Exception.Message)"
}

$headers = @{
    Authorization          = "Bearer $Token"
    Accept                 = "application/vnd.github+json"
    "X-GitHub-Api-Version" = "2022-11-28"
    "User-Agent"           = "IPScaner-Packager"
}

foreach ($asset in $assetFiles) {
    $sizeMb = [math]::Round($asset.Length / 1MB, 1)

    $already = $existingAssets | Where-Object { $_.name -eq $asset.Name } | Select-Object -First 1
    if ($already) {
        if ($already.size -eq $asset.Length) {
            Write-Host ("skipping {0} ({1} MB) — identical asset already published" -f $asset.Name, $sizeMb)
            continue
        }
        Write-Host ("replacing {0} (published size differs)" -f $asset.Name)
        Invoke-Api -Method DELETE -Uri "$repoUri/releases/assets/$($already.id)" -Body $null | Out-Null
    }

    Write-Host ("uploading {0} ({1} MB)..." -f $asset.Name, $sizeMb)

    # Assets upload to a different host, and the body must be raw bytes.
    $uploadUri = "https://uploads.github.com/repos/$Owner/$Repo/releases/$($release.id)/assets?name=$([uri]::EscapeDataString($asset.Name))"
    $resp = Invoke-RestMethod -Method POST -Uri $uploadUri -Headers $headers `
        -ContentType "application/octet-stream" -InFile $asset.FullName -TimeoutSec 1800
    Write-Host ("  -> {0} ({1} bytes)" -f $resp.state, $resp.size)
}

Write-Step "done"
Write-Host "repository : $($repoInfo.html_url)"
Write-Host "release    : $($release.html_url)"

# ---------------------------------------------------------------- 6. verify
# Read a couple of files back and compare bytes with the working tree. Without
# this the base64-as-content mistake is invisible: the repo looks populated and
# every file silently contains its own encoded form.
Write-Step "verify committed content"

$verifyList = @("README.md", ".gitignore", "build\package.ps1")
$verifyFailed = 0
foreach ($rel in $verifyList) {
    $localPath = Join-Path $repoRoot $rel
    if (-not (Test-Path $localPath)) { continue }

    $apiPath = $rel -replace '\\', '/'
    try {
        $fileInfo = Invoke-Api -Method GET -Uri "$repoUri/contents/$apiPath" -Body $null
        $remoteBytes = [System.Convert]::FromBase64String(($fileInfo.content -replace '\s', ''))
        $localBytes = [System.IO.File]::ReadAllBytes($localPath)

        $same = $remoteBytes.Length -eq $localBytes.Length
        if ($same) {
            for ($i = 0; $i -lt $localBytes.Length; $i++) {
                if ($remoteBytes[$i] -ne $localBytes[$i]) { $same = $false; break }
            }
        }

        if ($same) {
            Write-Host ("  {0,-28} OK  ({1} bytes)" -f $rel, $localBytes.Length)
        } else {
            $verifyFailed++
            Write-Host ("  {0,-28} MISMATCH  local={1} remote={2}" -f $rel, $localBytes.Length, $remoteBytes.Length)
        }
    }
    catch {
        $verifyFailed++
        Write-Host ("  {0,-28} could not verify: {1}" -f $rel, $_.Exception.Message)
    }
}

if ($verifyFailed -gt 0) {
    Write-Warning "$verifyFailed file(s) did not round-trip. The commit content is suspect."
    exit 1
}
Write-Host "committed content matches the working tree"
