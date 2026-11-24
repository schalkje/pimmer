<#
.SYNOPSIS
  Builds the Pimmer Windows installer (artifacts\Pimmer-Setup-<version>.exe) with Inno Setup 6.
.EXAMPLE
  .\scripts\build-installer.ps1 -Version 1.2.0
#>
param(
    [string]$Version = '1.0.0'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\Pimmer.App\Pimmer.App.csproj'
$staging = Join-Path $root 'artifacts\publish'

$candidates = @((Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source)
foreach ($v in 7, 6) {
    $candidates += "$env:LOCALAPPDATA\Programs\Inno Setup $v\ISCC.exe", "$env:ProgramFiles\Inno Setup $v\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup $v\ISCC.exe"
}
$iscc = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6/7 not found. Install it from https://jrsoftware.org/inno-setup/downloads (or: choco install innosetup).' }

if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
dotnet publish $project -c Release -r win-x64 --self-contained true -p:Version=$Version -o $staging -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

& $iscc "/DAppVersion=$Version" (Join-Path $root 'installer\Pimmer.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)" }

Write-Host "Installer: $(Join-Path $root "artifacts\Pimmer-Setup-$Version.exe")"
