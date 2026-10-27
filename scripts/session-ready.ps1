<#
.SYNOPSIS
  会话开场一键就绪：确保虚拟机、访客代理、隧道全部可用，并输出耗时。

.DESCRIPTION
  新会话只需要这一条命令，然后就可以调用 vm_* 工具。
  内部复用 quick-setup.ps1（自动开机、修 NAT、校验/启动代理、连接失败自动重启），
  最后做一次真实回环校验（/info），并打印「就绪耗时」。

.PARAMETER VmName
  虚拟机名称（必填）

.PARAMETER GuestUser
  访客用户名，默认 qa

.PARAMETER GuestPass
  访客密码（必填）

.PARAMETER NoViewer
  不启动实时画面小窗

.PARAMETER TunnelUrl
  隧道地址，默认 http://127.0.0.1:8890
#>
param(
  [Parameter(Mandatory = $true)][string]$VmName,
  [string]$GuestUser = 'qa',
  [Parameter(Mandatory = $true)][string]$GuestPass,
  [switch]$NoViewer,
  [string]$TunnelUrl = 'http://127.0.0.1:8890'
)

$ErrorActionPreference = 'Stop'
$sw = [Diagnostics.Stopwatch]::StartNew()

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$setup = Join-Path $here 'quick-setup.ps1'
if (-not (Test-Path $setup)) { throw "找不到 quick-setup.ps1：$setup" }

Write-Host '=== 会话就绪检查 ===' -ForegroundColor Cyan

# 已经热着就秒过；否则交给 quick-setup 全量修复
$already = $false
try {
  $r = Invoke-RestMethod -Uri "$TunnelUrl/status" -TimeoutSec 3
  if ($r.connected) { $already = $true }
} catch { }

if ($already) {
  Write-Host '  环境已在运行（跳过部署）' -ForegroundColor Green
} else {
  $args = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$setup`"",
    '-VmName', "`"$VmName`"", '-GuestUser', "`"$GuestUser`"", '-GuestPass', "`"$GuestPass`"")
  if ($NoViewer) { $args += '-NoViewer' }
  $p = Start-Process -FilePath 'powershell.exe' -ArgumentList $args -NoNewWindow -Wait -PassThru
  if ($p.ExitCode -ne 0) { throw "部署脚本返回失败（退出码 $($p.ExitCode)）" }
}

# 真实回环校验：必须拿到 /info 才算真的就绪
$deadline = (Get-Date).AddSeconds(60)
$info = $null
while ((Get-Date) -lt $deadline) {
  try {
    $info = Invoke-RestMethod -Uri "$TunnelUrl/info" -TimeoutSec 5
    if ($info -and $info.w) { break }
  } catch { }
  Start-Sleep -Seconds 2
}
if (-not $info -or -not $info.w) { throw '回环校验失败：/info 没有返回有效分辨率' }

$sw.Stop()
Write-Host ''
Write-Host ("=== 就绪：{0}x{1}，用户 {2}，耗时 {3:N1} 秒 ===" -f $info.w, $info.h, $info.user, $sw.Elapsed.TotalSeconds) -ForegroundColor Green
Write-Host '现在可以直接调用 vm_status / vm_screenshot / vm_ui / vm_click ...' -ForegroundColor Gray
