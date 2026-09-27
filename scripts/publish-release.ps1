# 默认双平台发布 Release：打包（可选）→ 打 tag → 推代码/tag → GitHub + Gitee 创建 Release 并上传 ZIP。
# 用法：
#   pwsh scripts/publish-release.ps1 -Version 1.15.7.8
#   pwsh scripts/publish-release.ps1 -Version 1.15.7.8 -SkipPackage   # 已有 release\v1.15.7.8\
#   pwsh scripts/publish-release.ps1 -Version 1.15.7.8 -SkipPush      # 只建 Release，不推 git
#
# Gitee：环境变量 GITEE_TOKEN，或本机已保存的 gitee.com git 凭据（密码=私人令牌）。
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [switch]$SkipPackage,
    [switch]$SkipPush,
    [string]$NotesFile = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $root

if ($Version -notmatch '^\d+\.\d+(?:\.\d+){0,2}$') {
    throw "Version must look like 1.15.7.8"
}

. (Join-Path $PSScriptRoot "remotes.ps1")
Ensure-DualRemotes

$tag = "v$Version"
$releaseRoot = Join-Path $root "release\$tag"
$githubRepo = "Edwardhehe/batchPrintZWCAD"

if ([string]::IsNullOrWhiteSpace($NotesFile)) {
    $NotesFile = Join-Path $root "docs\RELEASE_NOTES_v$Version.md"
}

function Get-ReleaseNotesBody {
    if (Test-Path -LiteralPath $NotesFile) {
        return [System.IO.File]::ReadAllText($NotesFile, [System.Text.UTF8Encoding]::new($false))
    }
    return "LA BatchPlot $tag"
}

if (-not $SkipPackage) {
    Write-Host "=== package-release $Version ==="
    & (Join-Path $PSScriptRoot "package-release.ps1") -Version $Version
}

if (-not (Test-Path -LiteralPath $releaseRoot)) {
    throw "Release directory not found: $releaseRoot"
}

$zips = @(Get-ChildItem -LiteralPath $releaseRoot -File -Filter "*.zip" | Sort-Object Name)
if ($zips.Count -eq 0) {
    throw "No zip archives under $releaseRoot"
}

# 打 annotated tag（已存在则复用）
$existingTag = git rev-parse -q --verify "refs/tags/$tag" 2>$null
if (-not $existingTag) {
    $msg = "Release $tag"
    git tag -a $tag -m $msg
    Write-Host "已创建 tag $tag"
}
else {
    Write-Host "tag $tag 已存在，跳过创建"
}

if (-not $SkipPush) {
    Write-Host "=== push code + tags (GitHub + Gitee) ==="
    & (Join-Path $PSScriptRoot "push-all.ps1") -Tags
}

$notesBody = Get-ReleaseNotesBody
$notesPath = Join-Path $env:TEMP "batchplot-release-notes-$tag.md"
[System.IO.File]::WriteAllText($notesPath, $notesBody, [System.Text.UTF8Encoding]::new($false))

# ---------- GitHub Release ----------
Write-Host "=== GitHub Release $tag ==="
$ghExisting = gh release view $tag --repo $githubRepo 2>$null
if ($LASTEXITCODE -eq 0) {
    Write-Host "GitHub Release $tag 已存在，更新附件…"
    foreach ($zip in $zips) {
        gh release upload $tag $zip.FullName --repo $githubRepo --clobber
    }
}
else {
    $ghArgs = @(
        "release", "create", $tag,
        "--repo", $githubRepo,
        "--title", "LA批打印 $tag",
        "--notes-file", $notesPath
    )
    foreach ($zip in $zips) {
        $ghArgs += $zip.FullName
    }
    & gh @ghArgs
    if ($LASTEXITCODE -ne 0) {
        throw "gh release create failed"
    }
}
Write-Host "GitHub: https://github.com/$githubRepo/releases/tag/$tag" -ForegroundColor Green

# ---------- Gitee Release ----------
Write-Host "=== Gitee Release $tag ==="
$token = Get-GiteeAccessToken
if ([string]::IsNullOrWhiteSpace($token)) {
    throw "未找到 Gitee 令牌。请设置环境变量 GITEE_TOKEN，或确保 git credential 已保存 gitee.com（密码=私人令牌）。"
}

$apiBase = "https://gitee.com/api/v5/repos/$script:BatchPlotGiteeOwner/$script:BatchPlotGiteeRepo"
$headers = @{ "Content-Type" = "application/json;charset=UTF-8" }

function Invoke-GiteeApi {
    param(
        [string]$Method,
        [string]$Url,
        [hashtable]$Body = $null
    )
    $uri = if ($Url -match '\?') { "$Url&access_token=$token" } else { "$Url`?access_token=$token" }
    if ($null -eq $Body) {
        return Invoke-RestMethod -Method $Method -Uri $uri -Headers $headers
    }
    $json = $Body | ConvertTo-Json -Depth 6 -Compress
    return Invoke-RestMethod -Method $Method -Uri $uri -Headers $headers -Body ([System.Text.Encoding]::UTF8.GetBytes($json)) -ContentType "application/json;charset=UTF-8"
}

$giteeRelease = $null
try {
    $giteeRelease = Invoke-GiteeApi -Method GET -Url "$apiBase/releases/tags/$tag"
}
catch {
    $giteeRelease = $null
}

if ($null -eq $giteeRelease) {
    $giteeRelease = Invoke-GiteeApi -Method POST -Url "$apiBase/releases" -Body @{
        tag_name         = $tag
        name             = "LA批打印 $tag"
        body             = $notesBody
        target_commitish = "main"
        prerelease       = $false
    }
    Write-Host "已创建 Gitee Release $tag"
}
else {
    Write-Host "Gitee Release $tag 已存在，准备上传附件…"
}

$releaseId = $giteeRelease.id
foreach ($zip in $zips) {
    Write-Host "上传 $($zip.Name) -> Gitee…"
    $uploadUrl = "$apiBase/releases/$releaseId/attach_files?access_token=$token"
    # multipart: file=@path
    curl.exe -sS -X POST $uploadUrl -F "file=@$($zip.FullName)" | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Gitee attach_files failed for $($zip.Name)"
    }
}

Write-Host "Gitee: https://gitee.com/$script:BatchPlotGiteeOwner/$script:BatchPlotGiteeRepo/releases/tag/$tag" -ForegroundColor Green
Write-Host "双平台 Release 发布完成：$tag" -ForegroundColor Green
