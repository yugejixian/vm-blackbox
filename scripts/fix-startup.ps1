$startup = [Environment]::GetFolderPath('Startup')
$lnkPath = Join-Path $startup 'QaTunnel Autostart.lnk'
$ws = New-Object -ComObject WScript.Shell
$lnk = $ws.CreateShortcut($lnkPath)
$lnk.TargetPath = 'C:\qa\QaTunnel.exe'
$lnk.WorkingDirectory = 'C:\qa'
$lnk.WindowStyle = 7
$lnk.Description = 'Start QaTunnel agent at logon'
$lnk.Save()
"lnk=" + $lnkPath
"target=" + $lnk.TargetPath
