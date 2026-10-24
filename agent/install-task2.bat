@echo off
rem Recreate the tunnel task so it runs INTERACTIVE (console session) with highest privileges.
schtasks /Delete /TN "QaTunnel" /F > C:\qa\t2-del.log 2>&1
taskkill /f /im QaTunnel.exe > C:\qa\t2-kill.log 2>&1
timeout /t 1 /nobreak >nul
schtasks /Create /TN "QaTunnel" /TR "C:\qa\QaTunnel.exe" /SC ONLOGON /RL HIGHEST /IT /F /RU qa /RP Passw0rd! > C:\qa\t2-create.log 2>&1
timeout /t 1 /nobreak >nul
schtasks /Run /TN "QaTunnel" > C:\qa\t2-run.log 2>&1
echo DONE > C:\qa\t2-done.txt
