# check-run.ps1 — 体检一个（或全部）测试运行目录
#
#   .\check-run.ps1 -RunDir '<工作目录>/runs\case002-naive-20261024-1612'
#   .\check-run.ps1 -All
#
# 硬性检查（不过就是问题）：
#   1. report.md 存在
#   2. 报告里有明确结论：PASS / FAIL / 被阻断
#   3. 报告引用的每个截图都能在磁盘上找到
# 软性提示（只是提醒，不算失败）：
#   - 报告里没有出现「步骤 / 可疑项 / 取证 / 截图」这些词
#   - 磁盘上有截图但没被报告引用（孤儿图）

param(
    [string]$RunDir,
    [switch]$All,
    [string]$Root = '<工作目录>/runs'
)

$ErrorActionPreference = 'Continue'

function Check-One {
    param([string]$Dir)
    $name = Split-Path $Dir -Leaf
    $report = Join-Path $Dir 'report.md'
    $row = [ordered]@{
        Run      = $name
        Verdict  = '-'
        Refs     = 0
        Missing  = 0
        Orphan   = 0
        Soft     = ''
        Hard     = ''
    }
    if (-not (Test-Path $report)) {
        $row.Hard = '没有 report.md'
        return [pscustomobject]$row
    }
    $text = Get-Content $report -Raw -Encoding UTF8

    $conclusion = $text
    $m2 = [regex]::Match($text, '(?ms)^##\s*结论\s*$(.*?)(?=^##\s|\z)')
    if ($m2.Success) { $conclusion = $m2.Groups[1].Value }
    if ($conclusion -match 'PASS') { $row.Verdict = 'PASS' }
    elseif ($conclusion -match 'FAIL') { $row.Verdict = 'FAIL' }
    elseif ($conclusion -match '被阻断|BLOCKED') { $row.Verdict = 'BLOCKED' }
    if ($row.Verdict -eq '-') { $row.Hard = '报告里没有明确结论' }

    $refs = [regex]::Matches($text, '\((?:shots/)?([^)\s]+\.(?:jpg|png))\)') | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique
    $row.Refs = @($refs).Count

    $missing = @()
    foreach ($f in $refs) {
        if (-not (Test-Path (Join-Path (Join-Path $Dir 'shots') $f)) -and -not (Test-Path (Join-Path $Dir $f))) {
            $missing += $f
        }
    }
    $row.Missing = $missing.Count
    if ($missing.Count -gt 0) { $row.Hard = (($row.Hard + ' 缺图:' + ($missing -join ','))).Trim() }

    $shotsDir = Join-Path $Dir 'shots'
    if (Test-Path $shotsDir) {
        $onDisk = Get-ChildItem $shotsDir -File -Include *.jpg, *.png -ErrorAction SilentlyContinue | ForEach-Object Name
        $row.Orphan = @($onDisk | Where-Object { $refs -notcontains $_ }).Count
    }

    $soft = @()
    foreach ($w in @('步骤', '可疑项', '取证', '截图')) {
        if ($text -notmatch [regex]::Escape($w)) { $soft += $w }
    }
    if ($soft.Count -gt 0) { $row.Soft = '缺措辞:' + ($soft -join '/') }
    return [pscustomobject]$row
}

$targets = @()
if ($All) {
    $targets = Get-ChildItem $Root -Directory -ErrorAction SilentlyContinue | Sort-Object Name | ForEach-Object FullName
} elseif ($RunDir) {
    $targets = @($RunDir)
} else {
    Write-Host "用法：-RunDir <目录> 或 -All" -ForegroundColor Yellow
    return
}

$results = foreach ($t in $targets) { Check-One -Dir $t }
$results | Format-Table -AutoSize | Out-String -Width 200

$hard = $results | Where-Object { $_.Hard -ne '' }
if ($hard) {
    Write-Host "硬性问题（必须处理）：" -ForegroundColor Red
    $hard | ForEach-Object { Write-Host ("  - " + $_.Run + "：" + $_.Hard) }
} else {
    Write-Host "硬性检查全部通过" -ForegroundColor Green
}
