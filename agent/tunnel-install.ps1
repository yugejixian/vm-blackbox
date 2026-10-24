$ErrorActionPreference = 'Continue'
New-Item -ItemType Directory -Force -Path 'C:\qa' | Out-Null
Invoke-WebRequest -UseBasicParsing -Uri 'http://10.0.2.2:8800/QaTunnel.exe' -OutFile 'C:\qa\QaTunnel.exe'
$ws = New-Object -ComObject WScript.Shell
$lnk = Join-Path ([Environment]::GetFolderPath('Startup')) 'QaTunnel.lnk'
$s = $ws.CreateShortcut($lnk)
$s.TargetPath = 'C:\qa\QaTunnel.exe'
$s.WorkingDirectory = 'C:\qa'
$s.WindowStyle = 7
$s.Save()
Get-Process QaTunnel -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Process 'C:\qa\QaTunnel.exe'
Start-Sleep -Seconds 2
try {
    Invoke-WebRequest -UseBasicParsing -TimeoutSec 8 -Uri ('http://10.0.2.2:8800/mark?r=' + [uri]::EscapeDataString('tunnel-installed')) | Out-Null
} catch { }
