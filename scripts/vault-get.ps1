param([Parameter(Mandatory = $true)][string]$Name)

# read a secret to stdout (called by tunnel-host.js only; never logged)
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$path = Join-Path $scriptDir 'vault.dat'
if (-not (Test-Path $path)) { exit 2 }
$map = Get-Content $path -Raw -Encoding UTF8 | ConvertFrom-Json
$enc = $map.$Name
if ([string]::IsNullOrWhiteSpace($enc)) { exit 3 }
$ss = ConvertTo-SecureString $enc
$bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($ss)
try { [Console]::Out.Write([Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)) }
finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
