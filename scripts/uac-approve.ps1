<#
.SYNOPSIS
  批准虚拟机里当前弹出的 UAC 窗口（在宿主侧注入键盘，能穿透安全桌面）。

.DESCRIPTION
  被测软件要求管理员权限时会弹 UAC。UAC 在「安全桌面」上，非提权进程（包括本工具的
  访客代理）既看不到也点不到——表现为截图失败、ptr=dead。

  本脚本在宿主通过 VBoxManage 注入键盘事件：
    左方向键（把焦点从「否」移到「是」） + 回车。

.PARAMETER VmName
  虚拟机名称

.PARAMETER VBoxManage
  VBoxManage.exe 路径（默认自动查找）

.PARAMETER Deny
  改为拒绝（直接回车，默认焦点在「否」）
#>
param(
  [Parameter(Mandatory = $true)][string]$VmName,
  [switch]$Deny
)

$ErrorActionPreference = 'Stop'

if (-not $VBoxManage) {
  $VBoxManage = @(
    'C:\Program Files\Oracle\VirtualBox\VBoxManage.exe',
    'C:\Program Files (x86)\Oracle\VirtualBox\VBoxManage.exe'
  ) | Where-Object { Test-Path $_ } | Select-Object -First 1
  if (-not $VBoxManage) { throw '找不到 VBoxManage.exe' }
}

function Send-ScanCodes([string[]]$codes) {
  foreach ($c in $codes) {
    & $VBoxManage controlvm $VmName keyboardputscancode $c 2>$null | Out-Null
    Start-Sleep -Milliseconds 60
  }
}

if ($Deny) {
  Write-Host '拒绝 UAC：回车（焦点默认在「否」）' -ForegroundColor Yellow
  Send-ScanCodes @('1c', '9c')            # Enter
} else {
  Write-Host '批准 UAC：左方向键 + 回车（把焦点移到「是」）' -ForegroundColor Green
  Send-ScanCodes @('e0', '4b', 'e0', 'cb')  # Left
  Start-Sleep -Milliseconds 200
  Send-ScanCodes @('1c', '9c')            # Enter
}

Write-Host '已发送按键。如果窗口还在，可能焦点位置不同——可在 VirtualBox 控制台手动点击。' -ForegroundColor Gray
