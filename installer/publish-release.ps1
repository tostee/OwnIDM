# Publishes the version in Directory.Build.props: tags the current commit v<version> and pushes it.
# GitHub Actions (.github/workflows/release.yml) then tests, builds, signs and creates the release.
#   powershell -ExecutionPolicy Bypass -File installer/publish-release.ps1 -Notes "Qué cambia"
# Raise <Version> and commit first: a release with the same version is not offered to anyone.
param([string]$Notes = '')

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$props = [xml](Get-Content 'Directory.Build.props')
$version = @($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]
$tag = "v$version"

if (git status --porcelain) { throw 'Hay cambios sin commit: haz commit (con la versión nueva) antes de publicar.' }
# Native commands with redirected stderr raise errors in Windows PowerShell; these failures are expected.
$ErrorActionPreference = 'Continue'
git fetch --tags --quiet 2>&1 | Out-Null
git rev-parse -q --verify "refs/tags/$tag" 2>&1 | Out-Null
$exists = $LASTEXITCODE -eq 0
$ErrorActionPreference = 'Stop'
if ($exists) { throw "La versión $tag ya está publicada. Sube <Version> en Directory.Build.props." }

git tag -a $tag -m $(if ($Notes) { $Notes } else { "OwnIDM $version" })
git push origin HEAD $tag
if ($LASTEXITCODE -ne 0) { throw 'No se pudo subir la etiqueta a GitHub.' }

$repo = (gh repo view --json nameWithOwner --jq .nameWithOwner)
"Etiqueta $tag subida. Compilación y firma: https://github.com/$repo/actions"
"Cuando termine (las firmas se aprueban en SignPath): https://github.com/$repo/releases/tag/$tag"
