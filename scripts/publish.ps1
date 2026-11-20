<#
.SYNOPSIS
  Publishes Pimmer to a fixed install folder and creates Start menu / desktop shortcuts.
.EXAMPLE
  .\scripts\publish.ps1
  .\scripts\publish.ps1 -SelfContained -NoDesktopShortcut
#>
param(
    [string]$InstallDir = "$env:LOCALAPPDATA\Programs\Pimmer",
    [switch]$SelfContained,
    [switch]$NoDesktopShortcut
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\Pimmer.App\Pimmer.App.csproj'
$exe = Join-Path $InstallDir 'Pimmer.exe'

# The installed exe is locked while running.
Get-Process Pimmer -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq $exe } |
    ForEach-Object { Write-Host "Stopping running Pimmer (PID $($_.Id))"; Stop-Process -Id $_.Id; $_.WaitForExit(5000) | Out-Null }

$staging = Join-Path $env:TEMP "pimmer-publish-$(Get-Random)"
dotnet publish $project -c Release -r win-x64 --self-contained:$($SelfContained.IsPresent) -o $staging -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

if (Test-Path $InstallDir) { Remove-Item $InstallDir -Recurse -Force }
New-Item -ItemType Directory -Path (Split-Path $InstallDir) -Force | Out-Null
Move-Item $staging $InstallDir

$shell = New-Object -ComObject WScript.Shell
function New-Shortcut([string]$path) {
    $lnk = $shell.CreateShortcut($path)
    $lnk.TargetPath = $exe
    $lnk.WorkingDirectory = $InstallDir
    $lnk.IconLocation = "$exe,0"
    $lnk.Description = 'Pimmer - PIM manager'
    $lnk.Save()
    Write-Host "Shortcut: $path"
}

New-Shortcut (Join-Path ([Environment]::GetFolderPath('Programs')) 'Pimmer.lnk')
if (-not $NoDesktopShortcut) {
    New-Shortcut (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Pimmer.lnk')
}

Write-Host "Installed: $exe"
