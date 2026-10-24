# vm-ui.ps1 — 在「无窗口」和「带窗口」两种模式间切换测试虚拟机
#
#   .\vm-ui.ps1 -Mode show       # 带窗口启动：你可以直接用鼠标键盘接管这台虚拟机
#   .\vm-ui.ps1 -Mode headless   # 无窗口启动：交给自动测试用
#   .\vm-ui.ps1 -Mode status     # 只看当前状态
#
# 两种模式下隧道和 vm_* 工具都可用；区别只是有没有一个你能看见、能点的窗口。
#
# 注意：切换会先关机（poweroff），再按目标模式启动。未保存的访客状态会丢失；
# 需要干净基线时先跑 snapshot restore。

param(
    [ValidateSet('show', 'headless', 'status')]
    [string]$Mode = 'status',
    [string]$VmName = 'qa-win11-zh',
    [string]$VBoxManage = 'C:\Program Files\Oracle\VirtualBox\VBoxManage.exe'
)

$ErrorActionPreference = 'Continue'
if (-not (Test-Path $VBoxManage)) { throw "找不到 VBoxManage：$VBoxManage" }

function Get-VmState {
    $line = (& $VBoxManage showvminfo $VmName --machinereadable 2>$null | Select-String '^VMState=').Line
    if (-not $line) { return 'unknown' }
    return ($line -replace '^VMState="?', '' -replace '"$', '')
}

function Test-HasWindow {
    $p = Get-Process VirtualBoxVM -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle -like "*$VmName*" }
    return ($p -ne $null)
}

function Show-Status {
    $state = Get-VmState
    $hasWindow = Test-HasWindow
    Write-Host "虚拟机 : $VmName"
    Write-Host "状态   : $state"
    Write-Host ("窗口   : " + $(if ($hasWindow) { '有（你可以直接操作）' } else { '无（headless）' }))
    try {
        $s = (Invoke-WebRequest -UseBasicParsing -TimeoutSec 4 'http://127.0.0.1:8890/status').Content
        Write-Host "隧道   : $s"
    } catch {
        Write-Host "隧道   : 未运行（先启动 tunnel-host.js）"
    }
}

if ($Mode -eq 'status') { Show-Status; return }

$state = Get-VmState
if ($state -ne 'poweroff' -and $state -ne 'saved') {
    Write-Host "当前状态 $state，先关机..."
    & $VBoxManage controlvm $VmName poweroff 2>&1 | Out-Null
    for ($i = 0; $i -lt 20; $i++) {
        Start-Sleep -Seconds 1
        if ((Get-VmState) -eq 'poweroff') { break }
    }
}

if ($Mode -eq 'show') {
    Write-Host "以带窗口模式启动（可直接接管）..."
    & $VBoxManage startvm $VmName | Out-Null
} else {
    Write-Host "以无窗口模式启动..."
    & $VBoxManage startvm $VmName --type headless | Out-Null
}

Write-Host "等待访客与隧道就绪（最多 120 秒）..."
for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Seconds 3
    try {
        $s = (Invoke-WebRequest -UseBasicParsing -TimeoutSec 3 'http://127.0.0.1:8890/status').Content
        if ($s -match '"connected":true') { Write-Host "隧道已连接：$s  ($([int](3 * ($i + 1))) 秒)"; break }
    } catch { }
}
Show-Status
if ($Mode -eq 'show') {
    Write-Host ""
    Write-Host "现在可以直接在 VirtualBox 窗口里操作这台虚拟机。" -ForegroundColor Green
    Write-Host "注意：你操作时自动测试的合成点击也会生效，两边同时动会打架。" -ForegroundColor Yellow
}
