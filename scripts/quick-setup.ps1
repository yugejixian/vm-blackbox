<#
.SYNOPSIS
  一键部署 dsh-vm-tester：把访客代理送进 VirtualBox 虚拟机，并在宿主启动隧道。

.DESCRIPTION
  只做四件事，全部可重复执行（幂等）：
    1. 检查 VirtualBox / 虚拟机 / 所需文件
    2. 等访客就绪，停掉可能在运行的旧代理，再投递 QaTunnel.exe、RulerOverlay.exe
    3. 在访客里创建开机自启快捷方式，并启动代理
    4. 在宿主后台启动 tunnel-host.js，并等待隧道连通

  不写注册表、不装服务、不需要管理员权限。

.PARAMETER VmName
  VirtualBox 里的虚拟机名称（例如 qa-win11-zh）。

.PARAMETER GuestUser
  虚拟机内的 Windows 用户名。

.PARAMETER GuestPass
  虚拟机内该用户的密码。

.PARAMETER VBoxManage
  VBoxManage.exe 路径；默认取 VirtualBox 标准安装位置。

.PARAMETER ShotsDir
  截图落盘目录；默认 %LOCALAPPDATA%\dsh-vm-tester\shots。

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\scripts\quick-setup.ps1 `
    -VmName "qa-win11-zh" -GuestUser qa -GuestPass "你的密码"
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)][string]$VmName,
  [Parameter(Mandatory = $true)][string]$GuestUser,
  [Parameter(Mandatory = $true)][string]$GuestPass,
  [string]$VBoxManage = 'C:\Program Files\Oracle\VirtualBox\VBoxManage.exe',
  [string]$ShotsDir = '',
  [switch]$NoViewer   # 默认启动实时画面看板；加 -NoViewer 可关掉
  ,[switch]$NoStartVm   # 虚拟机没开时自动开（-NoStartVm 关闭该行为）
)

# 原生命令的 stderr 不应打断脚本，统一用 $LASTEXITCODE 判断
$ErrorActionPreference = 'Continue'

function Write-Step([string]$m) { Write-Host "[*] $m" }
function Write-Ok([string]$m) { Write-Host "[OK] $m" -ForegroundColor Green }
function Fail([string]$m) { Write-Host "[X] $m" -ForegroundColor Red; exit 1 }

$repoRoot = Split-Path -Parent $PSScriptRoot
$agentDir = Join-Path $repoRoot 'agent'

# ---------- 0. 前置检查 ----------
Write-Step '检查文件与环境'
foreach ($f in @('QaTunnel.exe', 'RulerOverlay.exe', 'tunnel-host.js', 'mk-agent-startup.ps1')) {
  if (-not (Test-Path (Join-Path $agentDir $f))) {
    Fail "缺少 agent\$f —— 请用完整仓库安装（dsh plugin add github:yugejixian/vm-blackbox）或升级到包含 agent/ 的 npm 版本"
  }
}
if (-not (Test-Path $VBoxManage)) {
  Fail "找不到 VBoxManage：$VBoxManage —— 请安装 VirtualBox 7.x，或用 -VBoxManage 指定路径"
}
$node = (Get-Command node -ErrorAction SilentlyContinue).Source
if (-not $node) { Fail '找不到 node —— 请安装 Node.js 20+ 并确保在 PATH 中' }
Write-Ok '文件与环境正常'

# ---------- 1. 虚拟机状态 ----------
$running = (& $VBoxManage list runningvms 2>$null | Out-String)
if ($running -notmatch [regex]::Escape($VmName)) {
  if ($NoStartVm) {
    Fail "虚拟机 `"$VmName`" 没有在运行。去掉 -NoStartVm 让脚本自动开机，或先在 VirtualBox 里启动。"
  }
  Write-Step "虚拟机未运行，正在后台开机（headless）"
  & $VBoxManage startvm $VmName --type headless 2>$null | Out-Null
  if ($LASTEXITCODE -ne 0) { Fail "虚拟机启动失败，请在 VirtualBox 里手动启动后再重跑" }
  for ($w = 0; $w -lt 30; $w++) {
    Start-Sleep -Seconds 2
    $now = (& $VBoxManage list runningvms 2>$null | Out-String)
    if ($now -match [regex]::Escape($VmName)) { break }
  }
  Write-Ok "虚拟机已启动"
}
Write-Ok "虚拟机 $VmName 正在运行"

function Invoke-Guest([string]$exe, [string[]]$guestArgs) {
  $out = & $VBoxManage guestcontrol $VmName run --username $GuestUser --password $GuestPass --wait-stdout --exe $exe -- @guestArgs 2>&1 | Out-String
  return $out
}

# ---------- 2. 等访客就绪（刚启动/恢复的虚拟机需要时间）----------
Write-Step '等待访客系统就绪'
$ready = $false
for ($i = 1; $i -le 40; $i++) {
  $probe = Invoke-Guest 'C:\Windows\System32\cmd.exe' @('/c', 'echo READY')
  if ($probe -match 'READY') { $ready = $true; break }
  Start-Sleep -Seconds 3
}
if (-not $ready) {
  Fail @'
访客系统 120 秒内没有响应。常见原因：
  1) 虚拟机刚启动还没进桌面 —— 等一会儿重跑
  2) 用户名/密码不对（GuestUser / GuestPass）
  3) Guest Additions 没装（VirtualBox 菜单：设备 → 插入增强功能光盘）
'@
}
Write-Ok '访客系统就绪'

# ---------- 3. 投递访客代理 ----------
Write-Step '投递访客代理到 C:\qa'
Invoke-Guest 'C:\Windows\System32\cmd.exe' @('/c', 'if not exist C:\qa mkdir C:\qa') | Out-Null

$localExe = Join-Path $agentDir 'QaTunnel.exe'
$localSize = (Get-Item $localExe).Length
$listing = Invoke-Guest 'C:\Windows\System32\cmd.exe' @('/c', 'dir C:\qa\QaTunnel.exe')
$m = [regex]::Match($listing, '([\d,]+)\s+QaTunnel\.exe')
$guestSize = if ($m.Success) { [int]($m.Groups[1].Value -replace ',', '') } else { 0 }
$procList = Invoke-Guest 'C:\Windows\System32\cmd.exe' @('/c', 'tasklist | findstr /i QaTunnel')
$agentRunning = $procList -match 'QaTunnel\.exe'
$needCopy = $true

if ($agentRunning -and $guestSize -eq $localSize) {
  Write-Ok "访客代理已在运行且版本一致（$localSize 字节），跳过复制"
  $needCopy = $false
}
elseif ($agentRunning) {
  Write-Step "访客代理版本不一致（运行中 $guestSize 字节 / 本地 $localSize 字节），尝试停止旧代理"
  # 代理可能由计划任务以高权限启动：先结束任务（普通权限也能操作自己的任务），再 taskkill
  $null = Invoke-Guest 'C:\Windows\System32\schtasks.exe' @('/End', '/TN', 'QaTunnel')
  Start-Sleep -Seconds 1
  $null = Invoke-Guest 'C:\Windows\System32\cmd.exe' @('/c', 'taskkill /f /im QaTunnel.exe')
  Start-Sleep -Seconds 2
  $after = Invoke-Guest 'C:\Windows\System32\cmd.exe' @('/c', 'tasklist | findstr /i QaTunnel')
  if ($after -match 'QaTunnel\.exe') {
    Write-Host ''
    Fail @"
访客里的旧代理以高权限运行，普通权限无法结束。
升级方式：重启虚拟机（或注销后重新登录），然后立刻重跑本脚本即可。
"@
  }
  Write-Ok '旧代理已停止，继续替换'
}

if ($needCopy) {
  foreach ($f in @('QaTunnel.exe', 'RulerOverlay.exe', 'mk-agent-startup.ps1')) {
    $copied = $false
    for ($try = 1; $try -le 3; $try++) {
      & $VBoxManage guestcontrol $VmName copyto --username $GuestUser --password $GuestPass (Join-Path $agentDir $f) "C:\qa\$f" 2>$null | Out-Null
      if ($LASTEXITCODE -eq 0) { $copied = $true; break }
      Start-Sleep -Seconds 2
    }
    if (-not $copied) { Fail "复制 $f 失败 —— 检查用户名/密码是否正确、Guest Additions 是否已安装" }
  }
  Write-Ok '代理文件已就位'
}

# ---------- 4. 开机自启 + 启动代理 ----------
Write-Step '设置开机自启并启动代理'
$boot = Invoke-Guest 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe' @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', 'C:\qa\mk-agent-startup.ps1')
if ($boot -notmatch 'startup=ok') { Fail "创建开机自启失败：$boot" }
Write-Ok '开机自启已设置'

  # 启动代理并确认真的起来了（guestcontrol start 可能静默失败）
  $psList = Invoke-Guest 'C:\Windows\System32\cmd.exe' @('/c', 'tasklist | findstr /i QaTunnel')
  if ($psList -notmatch 'QaTunnel\.exe') {
    & $VBoxManage guestcontrol $VmName start --username $GuestUser --password $GuestPass --exe 'C:\qa\QaTunnel.exe' 2>$null | Out-Null
    Start-Sleep -Seconds 2
    $psList = Invoke-Guest 'C:\Windows\System32\cmd.exe' @('/c', 'tasklist | findstr /i QaTunnel')
  }
  if ($psList -notmatch 'QaTunnel\.exe') {
    Write-Step "直接启动没成功，尝试通过计划任务启动"
    $null = Invoke-Guest 'C:\Windows\System32\schtasks.exe' @('/Run', '/TN', 'QaTunnel')
    Start-Sleep -Seconds 3
    $psList = Invoke-Guest 'C:\Windows\System32\cmd.exe' @('/c', 'tasklist | findstr /i QaTunnel')
  }
  if ($psList -notmatch 'QaTunnel\.exe') {
    Fail "代理没有启动成功。请在虚拟机里手动运行 C:\qa\QaTunnel.exe 后重跑本脚本。"
  }
  Write-Ok '代理已启动'

if (-not $ShotsDir) { $ShotsDir = Join-Path $env:LOCALAPPDATA 'dsh-vm-tester\shots' }

# ---------- 5. 宿主隧道 ----------
Write-Step '启动宿主隧道'
$tunnelUp = $false
try {
  $probe = Invoke-WebRequest -Uri 'http://127.0.0.1:8890/status' -TimeoutSec 3 -UseBasicParsing -ErrorAction Stop
  if ($probe.StatusCode -eq 200) { $tunnelUp = $true }
} catch { }
if ($tunnelUp) {
  Write-Ok '隧道已在运行，跳过启动'
} else {
  New-Item -ItemType Directory -Force -Path $ShotsDir | Out-Null
  $env:QA_SHOTS_DIR = $ShotsDir
  $env:QA_VM_NAME = $VmName
  $tunnelJs = Join-Path $agentDir 'tunnel-host.js'
  # PowerShell 5.1 不会给参数加引号，含空格/中文的路径必须显式加
  Start-Process -FilePath $node -ArgumentList @("`"$tunnelJs`"") -WindowStyle Hidden
  Write-Ok '隧道已在后台启动'
}

# ---------- 6. 等待连通 ----------
Write-Step '等待隧道连接（最多 90 秒）'
$connected = $false
for ($i = 1; $i -le 30; $i++) {
  Start-Sleep -Seconds 3
  try {
    $r = Invoke-RestMethod -Uri 'http://127.0.0.1:8890/status' -TimeoutSec 3
    if ($r.connected) { $connected = $true; break }
  } catch { }
}
if (-not $connected) {
  Write-Host ''
  Fail @'
90 秒内没有连上。按顺序检查：
  1) 虚拟机是否已登录桌面（没登录时开机自启不会跑）
  2) 访客里 C:\qa\QaTunnel.exe 是否在运行（任务管理器查 QaTunnel）
  3) 虚拟机 NAT 网络是否正常（能上网即可）
  4) 防火墙是否拦了出站（默认不拦）
排查完重跑本脚本即可。
'@
}
Write-Ok '隧道已连接'

# ---------- 6.5 自动清理（截图不无限增长；路径全部跟随当前用户）----------
try {
  $cleanSource = Join-Path $repoRoot 'scripts\clean-shots.ps1'
  if (Test-Path $cleanSource) {
    $cleanDir = Join-Path $env:LOCALAPPDATA 'dsh-vm-tester'
    New-Item -ItemType Directory -Force -Path $cleanDir | Out-Null
    $cleanLocal = Join-Path $cleanDir 'clean-shots.ps1'
    Copy-Item $cleanSource $cleanLocal -Force
    & powershell -NoProfile -ExecutionPolicy Bypass -File $cleanLocal -ShotsDir $ShotsDir -AllowCustomDir 2>$null | Out-Null
    $taskCmd = 'powershell -NoProfile -ExecutionPolicy Bypass -File "' + $cleanLocal + '" -ShotsDir "' + $ShotsDir + '" -AllowCustomDir'
    & schtasks /Create /TN 'dsh-vm-tester-cleanup' /TR $taskCmd /SC DAILY /ST 03:30 /F 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) {
      Write-Ok '已安装每日截图清理任务（03:30）'
    } else {
      # 没有管理员权限时退化为「登录时自动清理」（启动文件夹，用户级）
      $cleanupCmd = Join-Path $cleanDir 'cleanup.cmd'
      $cmdText = '@echo off' + [Environment]::NewLine + 'powershell -NoProfile -ExecutionPolicy Bypass -File "' + $cleanLocal + '" -ShotsDir "' + $ShotsDir + '" -AllowCustomDir >nul 2>&1' + [Environment]::NewLine + 'exit /b 0'
      Set-Content -Path $cleanupCmd -Value $cmdText -Encoding ASCII
      $startupDir = [Environment]::GetFolderPath('Startup')
      Copy-Item $cleanupCmd (Join-Path $startupDir 'dsh-vm-tester-cleanup.cmd') -Force
      Write-Ok '已安装登录时自动清理（启动文件夹，无需管理员）'
    }
  }
} catch { Write-Step '自动清理安装失败（不影响使用）' }


Write-Host ''
# ---------- 7. 实时画面看板（可选，不装自启）----------
$viewerExe = Join-Path $repoRoot 'scripts\VmWatch.exe'
if (-not $NoViewer -and (Test-Path $viewerExe)) {
  try { $null = ([wmiclass]'Win32_Process').Create($viewerExe); Write-Ok '已启动实时画面看板（关闭：Esc）' } catch { }
}

Write-Host '================ 部署完成 ================' -ForegroundColor Cyan
Write-Host "  虚拟机     : $VmName"
Write-Host "  访客用户   : $GuestUser"
if ($info) { Write-Host "  分辨率     : $($info.w)x$($info.h)" }
Write-Host "  截图目录   : $ShotsDir"
Write-Host '  接下来     : 重启 DSH，然后调用 vm_status / vm_screenshot 验证'
Write-Host '==========================================' -ForegroundColor Cyan
