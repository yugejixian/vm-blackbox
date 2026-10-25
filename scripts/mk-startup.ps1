# 1) 写一个启动脚本：登录后触发已存在的提权任务
$cmd = "@echo off`r`nschtasks /Run /TN QaTunnel >> C:\qa\start-agent.log 2>&1`r`n"
Set-Content 'C:\qa\start-agent.cmd' $cmd -Encoding ASCII
# 2) 在「启动」文件夹创建快捷方式
$startup = [Environment]::GetFolderPath('Startup')
$ws = New-Object -ComObject WScript.Shell
$lnk = $ws.CreateShortcut((Join-Path $startup 'QaTunnel Autostart.lnk'))
$lnk.TargetPath = 'C:\qa\start-agent.cmd'
$lnk.WindowStyle = 7
$lnk.Description = 'Start QaTunnel agent at logon'
$lnk.Save()
"startup=" + $startup
"lnk_exists=" + (Test-Path (Join-Path $startup 'QaTunnel Autostart.lnk'))
