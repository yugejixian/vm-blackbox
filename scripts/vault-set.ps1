param([Parameter(Mandatory = $true)][string]$Name)

# store a secret into the local DPAPI vault (bound to current Windows user)
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$path = Join-Path $scriptDir 'vault.dat'
if (Test-Path $path) {
    $raw = Get-Content $path -Raw -Encoding UTF8
    $map = if ([string]::IsNullOrWhiteSpace($raw)) { [ordered]@{} } else { $raw | ConvertFrom-Json }
} else {
    $map = [ordered]@{}
}
$sec = Read-Host -AsSecureString ("Value for '" + $Name + "'")
$enc = ConvertFrom-SecureString $sec
if ($map -is [System.Management.Automation.PSCustomObject]) {
    $map | Add-Member -NotePropertyName $Name -NotePropertyValue $enc -Force
} else {
    $map[$Name] = $enc
}
$map | ConvertTo-Json -Depth 4 | Set-Content $path -Encoding UTF8
Write-Output ("stored: " + $Name)
