# Produit la VSIX à installer dans VS 2022 ou VS 2026 : dist\ClaudeVS-<version>.vsix.
# Version horodatée (cible StampVsixVersion du .csproj) pour qu'elle remplace celle déjà installée.
# La VSIX de bin\Release\net472 (build de l'IDE) garde la version 0.1.0 de l'instance expérimentale.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$msbuild = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'MSBuild introuvable.' }

& $msbuild "$root\src\ClaudeVS\ClaudeVS.csproj" /restore /t:Rebuild /p:Configuration=Release /p:DeployExtension=false /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw "Échec de la compilation ($LASTEXITCODE)." }

$manifest = [xml](Get-Content "$root\src\ClaudeVS\obj\Release\net472\extension.vsixmanifest")
$version = $manifest.PackageManifest.Metadata.Identity.Version
$dist = New-Item -ItemType Directory -Force "$root\dist"
$target = Join-Path $dist "ClaudeVS-$version.vsix"
Copy-Item "$root\src\ClaudeVS\bin\Release\net472\ClaudeVS.vsix" $target -Force
Write-Host "VSIX : $target"
