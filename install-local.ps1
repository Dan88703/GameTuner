<#
.SYNOPSIS
    Builds GameTuner from source and installs it for the current user.

.DESCRIPTION
    1. Closes a running GameTuner (otherwise its files are locked).
    2. Publishes a self-contained Release build into
       %LOCALAPPDATA%\Programs\GameTuner  (same folder the installer uses).
    3. Creates a desktop shortcut (skip with -NoShortcut).
    4. Starts the installed GameTuner.

    Run from the repository root:
        powershell -ExecutionPolicy Bypass -File .\install-local.ps1

    Run it again after pulling new code to update the installed version.
    Autostart is enabled from inside the app (Settings), so it always points
    at the installed exe.
#>
param(
    [switch]$NoShortcut
)

$ErrorActionPreference = 'Stop'
$dest = Join-Path $env:LOCALAPPDATA 'Programs\GameTuner'
$exe  = Join-Path $dest 'GameTuner.exe'

Write-Host '==> Closing running GameTuner (if any)...'
Get-Process -Name 'GameTuner' -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500
if (Get-Process -Name 'GameTuner' -ErrorAction SilentlyContinue) {
    throw 'GameTuner is still running (probably as administrator). Exit it from the tray icon and run this script again.'
}

Write-Host "==> Building Release into $dest ..."
dotnet publish "$PSScriptRoot\src\GameTuner.App" -c Release -r win-x64 --self-contained true -p:Platform=x64 -o $dest
if ($LASTEXITCODE -ne 0) { throw 'Build failed - see the errors above.' }

if (-not $NoShortcut) {
    Write-Host '==> Creating desktop shortcut...'
    $desktop = [Environment]::GetFolderPath('DesktopDirectory')
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut((Join-Path $desktop 'GameTuner.lnk'))
    $link.TargetPath = $exe
    $link.WorkingDirectory = $dest
    $link.IconLocation = "$exe,0"
    $link.Description = 'GameTuner - game optimizer'
    $link.Save()
}

Write-Host '==> Done. Starting GameTuner...'
Start-Process $exe
