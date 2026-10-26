<#
.SYNOPSIS
  为虚拟机应用/还原「物理机伪装」设置（SMBIOS、磁盘/光驱型号、MAC）。

.DESCRIPTION
  用途：测试会检测虚拟机环境的软件（反作弊/反调试/授权校验等）时，把自有虚拟机
  配置成一台常见的物理机，观察软件行为差异。

  注意：
    * 只应用于你自己的虚拟机；
    * 大多数设置需要虚拟机关机后生效（脚本会提示）；
    * 伪装后仍需要 Guest Additions + 本工具代理才能被 dsh-vm-tester 控制，
      所以不要卸载 Guest Additions。

.PARAMETER VmName
  虚拟机名称

.PARAMETER Revert
  清除伪装，恢复 VirtualBox 默认标识

.PARAMETER VBoxManage
  VBoxManage.exe 路径（默认自动查找）
#>
param(
  [Parameter(Mandatory = $true)][string]$VmName,
  [switch]$Revert
)

$ErrorActionPreference = 'Stop'

if (-not $VBoxManage) {
  $VBoxManage = @(
    'C:\Program Files\Oracle\VirtualBox\VBoxManage.exe',
    'C:\Program Files (x86)\Oracle\VirtualBox\VBoxManage.exe'
  ) | Where-Object { Test-Path $_ } | Select-Object -First 1
  if (-not $VBoxManage) { throw '找不到 VBoxManage.exe，请用 -VBoxManage 指定' }
}

# 一台常见家用机（ASUS B550M + Ryzen 5 5600H + 三星 SSD）的 SMBIOS 特征
$settings = [ordered]@{
  'VBoxInternal/Devices/efi/0/Config/DmiBIOSVendor'          = 'American Megatrends Inc.'
  'VBoxInternal/Devices/efi/0/Config/DmiBIOSVersion'         = 'string:3002'
  'VBoxInternal/Devices/efi/0/Config/DmiBIOSReleaseDate'     = '07/12/2023'
  'VBoxInternal/Devices/efi/0/Config/DmiSystemVendor'        = 'ASUSTeK COMPUTER INC.'
  'VBoxInternal/Devices/efi/0/Config/DmiSystemProduct'       = 'PRIME B550M-A'
  'VBoxInternal/Devices/efi/0/Config/DmiSystemVersion'       = 'Rev X.0x'
  'VBoxInternal/Devices/efi/0/Config/DmiSystemSerial'        = 'System Serial Number'
  'VBoxInternal/Devices/efi/0/Config/DmiSystemSKU'           = 'SKU'
  'VBoxInternal/Devices/efi/0/Config/DmiSystemFamily'        = 'To Be Filled By O.E.M.'
  'VBoxInternal/Devices/efi/0/Config/DmiSystemUuid'          = '5b1f9a3c-7d2e-4f86-9a11-c2d34e5f6a7b'
  'VBoxInternal/Devices/efi/0/Config/DmiBoardVendor'         = 'ASUSTeK COMPUTER INC.'
  'VBoxInternal/Devices/efi/0/Config/DmiBoardProduct'        = 'PRIME B550M-A'
  'VBoxInternal/Devices/efi/0/Config/DmiBoardVersion'        = 'Rev X.0x'
  'VBoxInternal/Devices/efi/0/Config/DmiBoardSerial'         = 'Board Serial'
  'VBoxInternal/Devices/efi/0/Config/DmiBoardAssetTag'       = '<EMPTY>'
  'VBoxInternal/Devices/efi/0/Config/DmiBoardLocInChass'     = 'Default string'
  'VBoxInternal/Devices/efi/0/Config/DmiBoardBoardType'      = '10'
  'VBoxInternal/Devices/efi/0/Config/DmiChassisVendor'       = 'Default string'
  'VBoxInternal/Devices/efi/0/Config/DmiChassisVersion'      = 'Default string'
  'VBoxInternal/Devices/efi/0/Config/DmiChassisSerial'       = 'Default string'
  'VBoxInternal/Devices/efi/0/Config/DmiChassisAssetTag'     = 'Default string'
  'VBoxInternal/Devices/efi/0/Config/DmiChassisType'         = '3'
  'VBoxInternal/Devices/efi/0/Config/DmiProcManufacturer'    = 'AuthenticAMD'
  'VBoxInternal/Devices/efi/0/Config/DmiProcVersion'         = 'AMD Ryzen 5 5600H with Radeon Graphics'
  'VBoxInternal/Devices/efi/0/Config/DmiOEMVBoxVer'          = 'Not Specified'
  'VBoxInternal/Devices/efi/0/Config/DmiOEMVBoxRev'          = 'Not Specified'
  'VBoxInternal/Devices/ahci/0/Config/Port0/ModelNumber'     = 'Samsung SSD 990 PRO 1TB'
  'VBoxInternal/Devices/ahci/0/Config/Port0/SerialNumber'    = 'S6Z1NL0W1234567890AB'
  'VBoxInternal/Devices/ahci/0/Config/Port0/FirmwareRevision' = '1B2QJXD7'
  'VBoxInternal/Devices/ahci/0/Config/Port0/NonRotational'   = '1'
  'VBoxInternal/Devices/ahci/0/Config/Port2/ATAPIVendorId'   = 'HL-DT-ST'
  'VBoxInternal/Devices/ahci/0/Config/Port2/ATAPIProductId'  = 'DVDRW GUD1N'
  'VBoxInternal/Devices/ahci/0/Config/Port2/ATAPIRevision'   = '1.00'
}

$state = (& $VBoxManage showvminfo $VmName --machinereadable 2>$null | Select-String '^VMState=').Line
if ($state -notmatch 'poweroff') {
  Write-Host "[!] 虚拟机不是关机状态（$state）。修改会在下次开机生效；建议先关机再跑本脚本。" -ForegroundColor Yellow
}

$applied = 0
foreach ($k in $settings.Keys) {
  if ($Revert) {
    & $VBoxManage setextradata $VmName $k 2>$null | Out-Null
  } else {
    & $VBoxManage setextradata $VmName $k $settings[$k] 2>$null | Out-Null
  }
  $applied++
}

if ($Revert) {
  & $VBoxManage modifyvm $VmName --macaddress1 auto 2>$null | Out-Null
  Write-Host "已清除 $applied 项伪装设置（MAC 恢复自动）" -ForegroundColor Green
} else {
  & $VBoxManage modifyvm $VmName --macaddress1 D85ED3A1B2C3 2>$null | Out-Null
  Write-Host "已应用 $applied 项伪装设置（含 MAC）" -ForegroundColor Green
  Write-Host "提示：开机后可在虚拟机里用 systeminfo / wmic csproduct get vendor,model 验证。" -ForegroundColor Gray
}
