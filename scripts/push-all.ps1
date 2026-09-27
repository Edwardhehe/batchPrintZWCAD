# 默认双平台推送：GitHub + Gitee（代码与 tag）。
# 用法：
#   pwsh scripts/push-all.ps1
#   pwsh scripts/push-all.ps1 -Branch main -Tags
param(
    [string]$Branch = "",
    [switch]$Tags
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $root

. (Join-Path $PSScriptRoot "remotes.ps1")
Ensure-DualRemotes

if ([string]::IsNullOrWhiteSpace($Branch)) {
    $Branch = (git rev-parse --abbrev-ref HEAD).Trim()
}

Write-Host "=== remotes ==="
git remote -v
Write-Host "=== push $Branch ==="

# 分别推一次便于定位哪边失败；origin 的 pushurl 仍含双地址作兜底。
git -c credential.helper=manager push gitee "refs/heads/${Branch}:refs/heads/${Branch}"
git -c credential.helper=manager push origin "refs/heads/${Branch}:refs/heads/${Branch}"

if ($Tags) {
    Write-Host "=== push tags ==="
    git -c credential.helper=manager push gitee --tags
    git -c credential.helper=manager push origin --tags
}

Write-Host "完成：$Branch 已同步到 GitHub 与 Gitee。"
