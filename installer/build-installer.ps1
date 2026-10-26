# Builds OwnIDM in Release and packs it into dist\OwnIDM-Setup-<version>.exe with Inno Setup 6,
# plus dist\latest.json, the update feed OwnIDM reads (publish both files together).
#   powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1 [-Version 1.2.3] [-Notes "Cambios…"]
#
# Code signing (optional, recommended): pass the signtool.exe arguments that pick your certificate,
# in -SignArgs or the OWNIDM_SIGN_ARGS environment variable, e.g.
#   certificate in the Windows store (Certum, SSL.com…):
#     /sha1 <huella> /fd sha256 /tr http://time.certum.pl /td sha256
#   Azure Artifact Signing (Trusted Signing):
#     /fd sha256 /tr http://timestamp.acs.microsoft.com /td sha256 /dlib <ruta>\Azure.CodeSigning.Dlib.dll /dmdf <ruta>\metadata.json
# OwnIDM's own binaries, the installer and its uninstaller are then signed.
#
# The release workflow signs with SignPath between two runs: -BuildOnly compiles, -SkipBuild packs
# the (by then signed) build\Release; it writes latest.json itself, after signing the installer.
param(
    [string]$Version,
    [string]$Notes = '',
    [string]$SignArgs = $env:OWNIDM_SIGN_ARGS,
    [switch]$BuildOnly,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

if (-not $Version) {
    $props = [xml](Get-Content (Join-Path $root 'Directory.Build.props'))
    $Version = @($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]
}

$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc -and -not $BuildOnly) { throw 'Falta Inno Setup 6. Instálalo con: winget install JRSoftware.InnoSetup' }

$signtool = $null
if ($SignArgs) {
    $signtool = (Get-Command signtool.exe -ErrorAction SilentlyContinue).Source
    if (-not $signtool) {
        $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
            Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
    }
    if (-not $signtool) { throw 'Falta signtool.exe (Windows SDK). Instálalo con: winget install Microsoft.WindowsSDK.10.0.26100' }
}

$release = Join-Path $root 'build\Release'
if (-not $SkipBuild) {
    # A clean output folder, so files of older builds do not end up in the installer.
    if (Test-Path $release) { Remove-Item $release -Recurse -Force }
    dotnet build (Join-Path $root 'src\OwnIDM.App\OwnIDM.App.csproj') -c Release "-p:Version=$Version"
    if ($LASTEXITCODE -ne 0) { throw 'La compilación falló.' }
}

# An antivirus that dislikes a fresh, unsigned build deletes it right after the compiler writes it;
# without this check the installer would silently ship without the program.
foreach ($file in 'OwnIDM.exe', 'OwnIDM.dll', 'OwnIDM.Core.dll', 'OwnIDM.NativeHost.exe', 'OwnIDM.NativeHost.dll') {
    if (-not (Test-Path (Join-Path $release $file))) {
        throw "Falta build\Release\$file tras compilar. Probablemente lo ha borrado el antivirus: revisa su cuarentena y añade una exclusión para la carpeta build."
    }
}
if ($BuildOnly) { return }

$isccArgs = @("/DAppVersion=$Version")
if ($signtool) {
    $ours = Get-ChildItem $release -File | Where-Object { $_.Name -like 'OwnIDM*.exe' -or $_.Name -like 'OwnIDM*.dll' }
    & $signtool sign $SignArgs.Split(' ', [StringSplitOptions]::RemoveEmptyEntries) $ours.FullName
    if ($LASTEXITCODE -ne 0) { throw 'signtool no pudo firmar los binarios.' }
    # Inno Setup calls it for the installer and the uninstaller ($f = the file, quoted).
    $isccArgs += "/Sownidm=`"$signtool`" sign $SignArgs `$f", '/DSignTool=ownidm'
}

& $iscc @isccArgs (Join-Path $PSScriptRoot 'OwnIDM.iss')
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup no pudo generar el instalador.' }

$setup = Get-Item (Join-Path $root "dist\OwnIDM-Setup-$Version.exe")
& (Join-Path $PSScriptRoot 'write-feed.ps1') -Setup $setup.FullName -Version $Version -Notes $Notes | Out-Null

$setup | Select-Object FullName, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }, @{ n = 'Firmado'; e = { [bool]$signtool } }
