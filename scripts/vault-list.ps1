param()

# list credential names only (never values)
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$path = Join-Path $scriptDir 'vault.dat'
if (-not (Test-Path $path)) { Write-Output '(vault empty)'; exit 0 }
$map = Get-Content $path -Raw -Encoding UTF8 | ConvertFrom-Json
$map.PSObject.Properties | ForEach-Object { Write-Output $_.Name }
