# Produit la VSIX à installer dans VS 2022 ou VS 2026 : dist\ClaudeVS-<version>.vsix.
# Par défaut, version horodatée (cible StampVsixVersion du .csproj) pour qu'elle remplace celle déjà installée.
# -Release : version du manifeste (1.0.0), sans horodatage, pour l'asset d'une release GitHub.
# Le build de l'IDE (bin\Release\net472, instance expérimentale) n'horodate pas : il garde la version du manifeste.
param([switch]$Release)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$msbuild = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'MSBuild introuvable.' }

$stamp = if ($Release) { 'false' } else { 'true' }
& $msbuild "$root\src\ClaudeVS\ClaudeVS.csproj" /restore /t:Rebuild /p:Configuration=Release /p:DeployExtension=false "/p:StampVsix=$stamp" /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw "Échec de la compilation ($LASTEXITCODE)." }

$manifest = [xml](Get-Content "$root\src\ClaudeVS\obj\Release\net472\extension.vsixmanifest")
$version = $manifest.PackageManifest.Metadata.Identity.Version
$dist = New-Item -ItemType Directory -Force "$root\dist"
$target = Join-Path $dist "ClaudeVS-$version.vsix"
Copy-Item "$root\src\ClaudeVS\bin\Release\net472\ClaudeVS.vsix" $target -Force
Write-Host "VSIX : $target"
