# Writes dist\latest.json, the update feed OwnIDM reads, for an installer that is already final
# (signed, if it is going to be: signing changes its SHA-256).
#   installer\write-feed.ps1 -Setup dist\OwnIDM-Setup-1.2.3.exe -Version 1.2.3 [-Notes "Cambios…"]
param(
    [Parameter(Mandatory)][string]$Setup,
    [Parameter(Mandatory)][string]$Version,
    [string]$Notes = ''
)

$ErrorActionPreference = 'Stop'
$file = Get-Item $Setup
# "url" is relative: it resolves next to latest.json (same folder or same GitHub release).
$feed = [ordered]@{
    version = $Version
    url = $file.Name
    size = $file.Length
    sha256 = (Get-FileHash $file.FullName -Algorithm SHA256).Hash
    notes = $Notes
}
$out = Join-Path $file.DirectoryName 'latest.json'
[IO.File]::WriteAllText($out, ($feed | ConvertTo-Json), (New-Object Text.UTF8Encoding $false))
Get-Item $out
