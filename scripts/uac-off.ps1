<#
.SYNOPSIS
  关闭访客 Windows 的 UAC（测试环境常用），关闭后需要重启生效。

.DESCRIPTION
  为什么需要：
    * 被测软件要求管理员权限时，会在「安全桌面」弹 UAC。安全桌面上的窗口，
      非提权进程（包括 dsh-vm-tester 的访客代理）看不到也点不到，
      表现就是截图失败、pointerOk=dead。
    * 关闭 UAC 后，管理员用户启动的程序直接获得完整权限，代理与程序处于同一
      完整性级别，点击/输入才有效。

  本脚本需要以「管理员身份」在访客内运行。如果当前是普通权限：
    1) 在 VirtualBox 控制台里手动确认 UAC；
    2) 或先在宿主运行 scripts/uac-approve.ps1 批准弹出的 UAC，再用管理员重跑。

.PARAMETER NoReboot
  只改注册表，不自动重启
#>
param([switch]$NoReboot)

$ErrorActionPreference = 'Stop'

$id = [Security.Principal.WindowsIdentity]::GetCurrent()
$isAdmin = ([Security.Principal.WindowsPrincipal]$id).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
  Write-Host '需要在访客里以管理员身份运行本脚本（UAC 关闭后才能彻底免提示）。' -ForegroundColor Red
  Write-Host '提示：宿主可运行 scripts/uac-approve.ps1 批准 UAC。' -ForegroundColor Yellow
  exit 1
}

$key = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
Set-ItemProperty $key -Name EnableLUA -Value 0 -Type DWord
Set-ItemProperty $key -Name ConsentPromptBehaviorAdmin -Value 0 -Type DWord
Set-ItemProperty $key -Name PromptOnSecureDesktop -Value 0 -Type DWord
Set-ItemProperty $key -Name FilterAdministratorToken -Value 0 -Type DWord

Write-Host 'UAC 已关闭，需要重启虚拟机生效。' -ForegroundColor Green
if (-not $NoReboot) {
  Write-Host '5 秒后自动重启…' -ForegroundColor Yellow
  Start-Sleep -Seconds 5
  & shutdown.exe /r /f /t 0
}
