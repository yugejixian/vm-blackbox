$ws = New-Object -ComObject WScript.Shell
$lnk = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Plague Inc.lnk'
$s = $ws.CreateShortcut($lnk)
$s.TargetPath = 'C:\Users\qa\Desktop\plague\PlagueIncEvolved.exe'
$s.WorkingDirectory = 'C:\Users\qa\Desktop\plague'
$s.Save()
'Plague Inc'
