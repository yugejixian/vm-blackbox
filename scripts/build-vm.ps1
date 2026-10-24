# build-vm.ps1 — 一键重建测试虚拟机（qa-win11-zh）
#
# 用法：
#   .\build-vm.ps1 -Iso 'D:\assets\win11-zh-cn.iso'
#
# 做的事：
#   1. 建虚拟机（EFI + vTPM 2.0 + 4G/2核/64G 动态盘 + NAT）
#   2. 无人值守安装 Windows 11 专业版（中文）
#   3. 装 Guest Additions（含 WDDM 3D 驱动）
#   4. 投递并安装隧道代理（计划任务 + 最高权限 + 交互式）
#   5. 关掉 UAC 提权弹窗和防火墙拦截通知
#   6. 关机并打基线快照 zh-cn-clean
#
# 前置：
#   - VirtualBox 7.x（默认装在 C:\Program Files\Oracle\VirtualBox）
#   - 官方 zh-CN Windows 11 ISO（自行准备；本脚本不提供镜像）
#   - D:\null_work\agent 下有 QaTunnel.exe、install-task2.bat、uac-off.bat
#   - 宿主的 tunnel-host.js 已在跑（127.0.0.1:8890）

param(
    [Parameter(Mandatory = $true)][string]$Iso,
    [string]$VmName = 'qa-win11-zh',
    [string]$BaseFolder = 'D:\null_work\vms',
    [string]$GuestUser = 'qa',
    [string]$GuestPassword = 'Passw0rd!',
    [int]$MemoryMB = 4096,
    [int]$Cpus = 2,
    [int]$DiskGB = 64,
    [int]$ImageIndex = 4,          # 4 = Windows 11 专业版（中文 ISO 里通常是这个序号）
    [string]$Locale = 'zh_CN',
    [string]$SnapshotName = 'zh-cn-clean',
    [string]$AgentDir = 'D:\null_work\agent'
)

$ErrorActionPreference = 'Stop'
$vbm = 'C:\Program Files\Oracle\VirtualBox\VBoxManage.exe'
if (-not (Test-Path $vbm)) { throw "找不到 VBoxManage：$vbm" }
if (-not (Test-Path $Iso)) { throw "找不到 ISO：$Iso" }

function Step($msg) { Write-Host "`n=== $msg ===" -ForegroundColor Cyan }
function Vbm { & $vbm @args }

# ---------- 1. 建虚拟机 ----------
Step "创建虚拟机 $VmName"
$existing = Vbm list vms | Select-String "`"$VmName`""
if ($existing) { throw "虚拟机 $VmName 已存在，先手动删除或换名字" }

Vbm createvm --name $VmName --ostype Windows11_64 --register --basefolder $BaseFolder | Out-Null
Vbm modifyvm $VmName `
    --memory $MemoryMB --cpus $Cpus --vram 256 `
    --graphicscontroller vboxsvga --accelerate3d on `
    --firmware efi --tpm-type 2.0 `
    --nic1 nat --audio-enabled off --usb-xhci on --rtcuseutc on --ioapic on `
    --boot1 dvd --boot2 disk | Out-Null

$vdi = Join-Path (Join-Path $BaseFolder $VmName) "$VmName.vdi"
Vbm createmedium disk --filename $vdi --size ($DiskGB * 1024) --format VDI | Out-Null
Vbm storagectl $VmName --name SATA --add sata --controller IntelAhci --portcount 4 --bootable on | Out-Null
Vbm storageattach $VmName --storagectl SATA --port 0 --device 0 --type hdd --medium $vdi | Out-Null

# ---------- 2. 无人值守安装 ----------
Step "无人值守安装（镜像索引 $ImageIndex，语言 $Locale）"
Vbm unattended install $VmName `
    --iso=$Iso `
    --user=$GuestUser --password=$GuestPassword --full-user-name=$GuestUser `
    --locale=$Locale --country=CN `
    --image-index=$ImageIndex `
    --install-additions `
    --start-vm=headless | Out-Null

Write-Host "安装已启动，等待系统就绪（最长 40 分钟）..." 
$deadline = (Get-Date).AddMinutes(40)
$ready = $false
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 30
    $st = Vbm guestproperty get $VmName '/VirtualBox/GuestInfo/OS/LoggedInUsers' 2>$null
    if ($st -match 'Value: [1-9]') { $ready = $true; break }
}
if (-not $ready) { throw "等待超时：系统没在 40 分钟内登录" }
Write-Host "系统已登录"

# ---------- 3. 关键收尾：摘光驱 + 改启动顺序 ----------
# 不做这一步，下次重启会从安装 ISO 重新进安装程序（踩过）
Step "摘掉安装 ISO、改从硬盘启动"
Vbm storageattach $VmName --storagectl SATA --port 1 --device 0 --type dvddrive --medium none | Out-Null
Vbm modifyvm $VmName --boot1 disk --boot2 none | Out-Null

# ---------- 4. 投递并安装代理 ----------
Step "安装隧道代理（计划任务 + 最高权限 + 交互式）"
Start-Sleep -Seconds 20
foreach ($f in @('QaTunnel.exe', 'install-task2.bat', 'uac-off.bat')) {
    $src = Join-Path $AgentDir $f
    if (-not (Test-Path $src)) { throw "缺少代理文件：$src" }
    Vbm guestcontrol $VmName copyto --username $GuestUser --password $GuestPassword $src "C:\qa\$f"
}

# 提权运行安装脚本（此时 UAC 弹窗还没关，需要物理键盘点一次「是」）
Write-Host "接下来会弹出一次 UAC，请在 VirtualBox 窗口里用键盘点「是」（Tab → 空格）"
Vbm controlvm $VmName keyboardputscancode e0 5b 13 93 e0 db
Start-Sleep -Milliseconds 1200
Vbm controlvm $VmName keyboardputstring 'powershell -c Start-Process C:\qa\uac-off.bat -Verb RunAs'
Start-Sleep -Milliseconds 400
Vbm controlvm $VmName keyboardputscancode 1c 9c
Write-Host "等待 15 秒..." ; Start-Sleep -Seconds 15

Vbm controlvm $VmName keyboardputscancode e0 5b 13 93 e0 db
Start-Sleep -Milliseconds 1200
Vbm controlvm $VmName keyboardputstring 'powershell -c Start-Process C:\qa\install-task2.bat -Verb RunAs'
Start-Sleep -Milliseconds 400
Vbm controlvm $VmName keyboardputscancode 1c 9c
Start-Sleep -Seconds 20

# ---------- 5. 防火墙通知关掉 ----------
Step "关闭防火墙拦截通知（避免游戏/联网程序弹窗挡路）"
$psArgs = "Start-Process -FilePath 'C:\Windows\System32\netsh.exe' -ArgumentList 'advfirewall','set','allprofiles','settings','inboundusernotification','disable' -Verb RunAs -Wait"
Vbm guestcontrol $VmName run --exe 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe' `
    --username $GuestUser --password $GuestPassword --wait-stdout -- '-NoProfile' '-Command' $psArgs | Out-Null

# ---------- 6. 校验 + 打快照 ----------
Step "校验"
$status = (Invoke-WebRequest -UseBasicParsing -TimeoutSec 5 'http://127.0.0.1:8890/status').Content
Write-Host "隧道状态：$status"

Step "关机并打基线快照 $SnapshotName"
Vbm controlvm $VmName acpipowerbutton | Out-Null
Start-Sleep -Seconds 30
$state = (Vbm showvminfo $VmName --machinereadable | Select-String '^VMState=').Line
if ($state -notmatch 'poweroff') { Vbm controlvm $VmName poweroff | Out-Null; Start-Sleep -Seconds 10 }
Vbm snapshot $VmName take $SnapshotName --description "built by build-vm.ps1" | Out-Null

Write-Host "`n完成。日常开测：restore $SnapshotName → startvm --type headless" -ForegroundColor Green
