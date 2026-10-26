# clean-shots.ps1 — 清理截图暂存目录，只保留「被报告引用」和「最近的」
#
#   .\clean-shots.ps1              # 默认：保留 24 小时内的 + 被任何报告引用的
#   .\clean-shots.ps1 -KeepHours 2
#   .\clean-shots.ps1 -DryRun      # 只看会删什么，不动手
#
# 规则：
#   1. runs\*\report.md 里引用到的 shot-*.jpg 一律保留（那是证据）
#   2. 最近 N 小时内的文件保留（可能还有运行在进行）
#   3. 其余 shot-*.jpg 删除
#   4. 非 shot-* 的调试图：只保留最近 N 小时的

param(
    [double]$KeepHours = 24,
    [switch]$DryRun,
    [string]$ShotsDir = (Join-Path ($env:LOCALAPPDATA + '\dsh-vm-tester\shots') ''),
    [string]$RunsDir = '<工作目录>/runs'
)

$ErrorActionPreference = 'Continue'

# 1) 收集所有报告引用到的截图名
$referenced = New-Object System.Collections.Generic.HashSet[string]
if (Test-Path $RunsDir) {
    Get-ChildItem $RunsDir -Directory -ErrorAction SilentlyContinue | ForEach-Object {
        $rp = Join-Path $_.FullName 'report.md'
        if (Test-Path $rp) {
            $txt = Get-Content $rp -Raw -Encoding UTF8
            foreach ($m in [regex]::Matches($txt, 'shot-\d+\.(?:jpg|png)')) {
                [void]$referenced.Add($m.Value)
            }
        }
    }
}

$cutoff = (Get-Date).AddHours(-$KeepHours)
$del = @()
$keepRef = 0
$keepRecent = 0

if (Test-Path $ShotsDir) {
    foreach ($f in Get-ChildItem $ShotsDir -File) {
        $isShot = $f.Name -match '^shot-\d+\.jpg$'
        if ($isShot -and $referenced.Contains($f.Name)) { $keepRef++; continue }
        if ($f.LastWriteTime -ge $cutoff) { $keepRecent++; continue }
        $del += $f
    }
}

$sizeMB = if ($del.Count -gt 0) { [math]::Round((($del | Measure-Object Length -Sum).Sum) / 1MB, 1) } else { 0 }
Write-Host ("引用保留 {0} 个 · 近期保留 {1} 个 · 待删 {2} 个 ({3} MB)" -f $keepRef, $keepRecent, $del.Count, $sizeMB)

if ($DryRun) {
    $del | Sort-Object Length -Descending | Select-Object -First 10 Name, @{n='MB';e={'{0:N1}' -f ($_.Length/1MB)}} | Format-Table -AutoSize | Out-String
    Write-Host "（DryRun：未删除任何文件）" -ForegroundColor Yellow
    return
}

$del | ForEach-Object { Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue }
$rest = @(Get-ChildItem $ShotsDir -File -ErrorAction SilentlyContinue)
$restMB = if ($rest.Count -gt 0) { [math]::Round((($rest | Measure-Object Length -Sum).Sum) / 1MB, 1) } else { 0 }
Write-Host ("已删除。剩余 {0} 个文件，{1} MB" -f $rest.Count, $restMB) -ForegroundColor Green
