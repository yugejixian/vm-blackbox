<#
.SYNOPSIS
  安全清理截图与抓帧临时文件（只删本工具自己生成、且严格匹配命名的文件）。

.DESCRIPTION
  安全护栏：
    * 只处理指定目录【顶层】的文件，不递归、不跟随重解析点；
    * 只删除严格匹配我们自己的命名格式的文件：
        - 抓帧临时文件  gc-<数字>-<数字>.jpg / gco-<数字>-<数字>.jpg / raw-<数字>-<数字>.png（超过 10 分钟）
        - 插件截图      shot-<数字>.jpg（超过 KeepHours 小时，或数量超过 MaxFiles）
    * 拒绝在系统/用户根目录这类受保护路径执行；
    * 非默认目录必须显式加 -AllowCustomDir，防止误传参数误删他人文件；
    * 默认只清理 %LOCALAPPDATA%\dsh-vm-tester\shots（路径跟随当前用户，不写死盘符）。

.PARAMETER ShotsDir
  截图目录，默认 %LOCALAPPDATA%\dsh-vm-tester\shots

.PARAMETER KeepHours
  插件截图保留小时数，默认 72

.PARAMETER MaxFiles
  插件截图最多保留张数，默认 300

.PARAMETER DryRun
  只打印将要删除的文件，不实际删除

.PARAMETER AllowCustomDir
  允许清理非默认目录（每日任务会带上，因为目录来自用户配置）
#>
param(
  [string]$ShotsDir = (Join-Path (Join-Path $env:LOCALAPPDATA 'dsh-vm-tester') 'shots'),
  [int]$KeepHours = 72,
  [int]$MaxFiles = 300,
  [switch]$DryRun,
  [switch]$AllowCustomDir
)

$ErrorActionPreference = 'Stop'

# ---------- 安全护栏 ----------
if ($ShotsDir -like '*<*>*') { throw "路径含占位符，拒绝执行：$ShotsDir" }
if (-not (Test-Path -LiteralPath $ShotsDir -PathType Container)) { "目录不存在，跳过：$ShotsDir"; exit 0 }

$full = (Resolve-Path -LiteralPath $ShotsDir).ProviderPath.TrimEnd('\')
$defaultDir = Join-Path (Join-Path $env:LOCALAPPDATA 'dsh-vm-tester') 'shots'
$defaultFull = if (Test-Path -LiteralPath $defaultDir) { (Resolve-Path -LiteralPath $defaultDir).ProviderPath.TrimEnd('\') } else { $defaultDir }

$risky = New-Object System.Collections.ArrayList
foreach ($p in @($env:SystemRoot, $env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:ProgramData, $env:USERPROFILE, $env:SystemDrive + '\', $env:LOCALAPPDATA)) {
  if ($p) { [void]$risky.Add(($p.TrimEnd('\')).ToLower()) }
}
if ($risky.Contains($full.ToLower())) { throw "拒绝在受保护目录执行：$full" }
if ($full.Length -lt 12) { throw "路径过短，拒绝执行：$full" }

$isDefault = ($full.ToLower() -eq $defaultFull.ToLower())
if (-not $isDefault -and -not $AllowCustomDir) {
  throw "非默认截图目录，需显式加 -AllowCustomDir：$full"
}

# ---------- 只处理顶层文件 ----------
$tempRe = '^(gc|gco)-\d+-\d+\.jpg$|^raw-\d+-\d+\.png$'
$shotRe = '^shot-\d+\.jpg$'
$now = Get-Date
$tempCutoff = $now.AddMinutes(-10)
$shotCutoff = $now.AddHours(-$KeepHours)

$all = @(Get-ChildItem -LiteralPath $full -File -ErrorAction SilentlyContinue)
$temp = @($all | Where-Object { $_.Name -match $tempRe -and $_.LastWriteTime -lt $tempCutoff })
$shots = @($all | Where-Object { $_.Name -match $shotRe } | Sort-Object LastWriteTime -Descending)
$oldShots = New-Object System.Collections.ArrayList
for ($i = 0; $i -lt $shots.Count; $i++) {
  if ($i -ge $MaxFiles -or $shots[$i].LastWriteTime -lt $shotCutoff) { [void]$oldShots.Add($shots[$i]) }
}

$del = @($temp) + @($oldShots)
$size = 0
if ($del.Count -gt 0) { $size = ($del | Measure-Object Length -Sum).Sum }
"保留 {0} 个 · 待删 {1} 个 ({2:N1} MB) · 目录 {3}" -f ($all.Count - $del.Count), $del.Count, ($size / 1MB), $full

if ($del.Count -eq 0) { exit 0 }
if ($DryRun) {
  $del | Select-Object -First 15 | ForEach-Object { "  [dry] " + $_.Name }
  exit 0
}

$failed = 0
foreach ($f in $del) {
  try { Remove-Item -LiteralPath $f.FullName -Force -ErrorAction Stop }
  catch { $failed++ }
}
$left = @(Get-ChildItem -LiteralPath $full -File -ErrorAction SilentlyContinue).Count
"已删除 $($del.Count - $failed) 个（失败 $failed），剩余 $left 个。"
