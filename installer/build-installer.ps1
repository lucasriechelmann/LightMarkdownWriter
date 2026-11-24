<#
.SYNOPSIS
  Builds the LightMarkdownWriter installer: publishes the self-contained app, then compiles the
  Inno Setup script. Output: artifacts/installer/LightMarkdownWriter-Setup.exe (+ .sha256). The version is in the file properties.

.PARAMETER Iscc
  Path to ISCC.exe (Inno Setup 6). Found automatically on PATH or in the usual install folders.

.EXAMPLE
  ./installer/build-installer.ps1
#>
param([string]$Iscc)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src/LightMarkdownWriter/LightMarkdownWriter.csproj'
$publishDir = Join-Path $root 'artifacts/installer-publish'
$outputDir = Join-Path $root 'artifacts/installer'

# Version comes from the project, so the app and the installer always agree.
$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> in $project" }

if (-not $Iscc) {
    $Iscc = @(
        (Get-Command ISCC.exe -ErrorAction SilentlyContinue)?.Source
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
}
if (-not $Iscc) { throw 'Inno Setup 6 (ISCC.exe) not found. Install it from https://jrsoftware.org/isinfo.php or pass -Iscc.' }

Write-Host "Publishing $version (self-contained)..."
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish $project -p:PublishProfile=win-x64-installer
if ($LASTEXITCODE) { throw 'dotnet publish failed' }

Write-Host 'Compiling installer...'
& $Iscc "/DAppVersion=$version" "/DSourceDir=$publishDir" "/O$outputDir" (Join-Path $PSScriptRoot 'LightMarkdownWriter.iss')
if ($LASTEXITCODE) { throw 'ISCC failed' }

$setup = Join-Path $outputDir "LightMarkdownWriter-Setup.exe"
$hash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $(Split-Path $setup -Leaf)" | Set-Content "$setup.sha256" -Encoding ascii -NoNewline
Write-Host ("Built {0} ({1:N1} MB), sha256 {2}" -f $setup, ((Get-Item $setup).Length / 1MB), $hash)
