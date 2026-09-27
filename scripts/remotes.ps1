# 双平台远程：GitHub（主拉取）+ Gitee（同步推送）。
# 由 push-all.ps1 / publish-release.ps1 点用，也可单独：. .\scripts\remotes.ps1; Ensure-DualRemotes

$script:BatchPlotGitHubUrl = "https://github.com/Edwardhehe/batchPrintZWCAD.git"
$script:BatchPlotGiteeUrl = "https://gitee.com/Edwardhehe/batchPrintLA.git"
$script:BatchPlotGiteeOwner = "Edwardhehe"
$script:BatchPlotGiteeRepo = "batchPrintLA"

function Ensure-DualRemotes {
    <#
    .SYNOPSIS
    确保 origin 拉取 GitHub、推送同时指向 GitHub+Gitee，并保留独立 gitee remote。
    #>
    $existing = @(git remote)
    if ($existing -notcontains "origin") {
        git remote add origin $script:BatchPlotGitHubUrl
    }
    else {
        git remote set-url origin $script:BatchPlotGitHubUrl
    }

    if ($existing -notcontains "gitee") {
        git remote add gitee $script:BatchPlotGiteeUrl
    }
    else {
        git remote set-url gitee $script:BatchPlotGiteeUrl
    }

    $pushUrls = @(git remote get-url --all --push origin 2>$null)
    $needRebuild = ($pushUrls -notcontains $script:BatchPlotGitHubUrl) `
        -or ($pushUrls -notcontains $script:BatchPlotGiteeUrl) `
        -or ($pushUrls.Count -lt 2)
    if ($needRebuild) {
        foreach ($u in @($pushUrls)) {
            if ($u) {
                git remote set-url --delete --push origin $u 2>$null
            }
        }
        git remote set-url --add --push origin $script:BatchPlotGitHubUrl
        git remote set-url --add --push origin $script:BatchPlotGiteeUrl
        Write-Host "已配置 origin 双推送：GitHub + Gitee"
    }
}

function Get-GiteeAccessToken {
    <#
    .SYNOPSIS
    读取 Gitee 私人令牌：优先环境变量 GITEE_TOKEN，否则从 git credential 取 password。
    #>
    if (-not [string]::IsNullOrWhiteSpace($env:GITEE_TOKEN)) {
        return $env:GITEE_TOKEN.Trim()
    }

    $input = "protocol=https`nhost=gitee.com`n`n"
    $filled = $input | git credential fill 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($filled)) {
        return $null
    }

    foreach ($line in ($filled -split "`n")) {
        if ($line -match '^password=(.+)$') {
            return $Matches[1].Trim()
        }
    }
    return $null
}
