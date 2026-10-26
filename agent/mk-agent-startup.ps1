param()
# 在访客虚拟机里创建 QaTunnel 的开机自启快捷方式。
# 由宿主脚本 quick-setup.ps1 投递并执行；也可以手动运行。
$startup = [Environment]::GetFolderPath('Startup')
$lnkPath = Join-Path $startup 'QaTunnel Autostart.lnk'
$ws = New-Object -ComObject WScript.Shell
$lnk = $ws.CreateShortcut($lnkPath)
$lnk.TargetPath = 'C:\qa\QaTunnel.exe'
$lnk.WorkingDirectory = 'C:\qa'
$lnk.WindowStyle = 7
$lnk.Save()
if (Test-Path $lnkPath) { 'startup=ok' } else { 'startup=failed' }
