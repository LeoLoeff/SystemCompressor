# System Compressor - installer of the portable (ZIP) version for Windows 10 / 11 (64 bit).
# Run via Install.cmd (double-click). Needs administrator rights and Equalizer APO.
#   -DryRun   shows what would happen without changing anything
param([switch]$DryRun)
$ErrorActionPreference = 'Stop'

function Test-Admin {
    ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}
function Step($text) { Write-Host "  - $text" }
function Run([scriptblock]$action, $text) { Step $text; if (-not $DryRun) { & $action } }

if (-not (Test-Admin)) {
    if ($DryRun) { Write-Host '(dry run without administrator rights - nothing is changed anyway)' }
    else {
        Start-Process powershell -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
        exit
    }
}

try {
    $source = $PSScriptRoot
    $build = [Environment]::OSVersion.Version.Build
    $osName = if ($build -ge 22000) { 'Windows 11' } else { 'Windows 10' }
    Write-Host ''
    Write-Host "System Compressor installer  ($osName, build $build)" -ForegroundColor Yellow
    if ($DryRun) { Write-Host '*** DRY RUN: nothing will be changed ***' -ForegroundColor Cyan }
    if (-not [Environment]::Is64BitOperatingSystem) { throw '64-bit Windows is required.' }
    if ($build -lt 17763) { throw 'Windows 10 (version 1809) or newer is required.' }
    foreach ($file in 'app\SystemCompressor.exe', 'plugin\SystemCompressor.dll', 'Apply.ps1') {
        if (-not (Test-Path (Join-Path $source $file))) { throw "Package incomplete, missing: $file" }
    }

    # Application
    $appDir = "$env:ProgramFiles\SystemCompressor"
    Run { Get-Process SystemCompressor -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep -Milliseconds 500
          New-Item -ItemType Directory -Force $appDir | Out-Null
          Copy-Item (Join-Path $source 'app\SystemCompressor.exe') "$appDir\SystemCompressor.exe" -Force } "Install app to $appDir"

    # Plug-in, Equalizer APO configuration, data folder (shared with the Setup.exe)
    $apply = @{ PluginDll = (Join-Path $source 'plugin\SystemCompressor.dll') }
    if ($DryRun) { $apply.DryRun = $true }
    & (Join-Path $source 'Apply.ps1') @apply
    $code = $LASTEXITCODE

    if ($code -eq 10) {
        Write-Host ''
        Write-Host 'Equalizer APO is not installed.' -ForegroundColor Red
        Write-Host '  1. Download it from https://sourceforge.net/projects/equalizerapo/ and install it.'
        Write-Host '  2. In its setup, tick your playback device (e.g. "Speakers"), finish, restart Windows.'
        Write-Host '  3. Run this installer again.'
        if (-not $DryRun) { Read-Host 'Press Enter to close' }
        exit 1
    }
    if ($code -eq 1) { throw 'Applying the plug-in failed (see message above).' }

    # Shortcuts
    $exe = "$appDir\SystemCompressor.exe"
    Run { $shell = New-Object -ComObject WScript.Shell
          foreach ($dir in "$env:PUBLIC\Desktop", "$env:ProgramData\Microsoft\Windows\Start Menu\Programs") {
              $link = $shell.CreateShortcut((Join-Path $dir 'System Compressor.lnk'))
              $link.TargetPath = $exe; $link.WorkingDirectory = $appDir; $link.IconLocation = "$exe,0"; $link.Save() } } 'Create desktop and Start menu shortcuts'

    Write-Host ''
    if ($code -eq 11) {
        Write-Host 'Equalizer APO is not yet enabled for all active playback devices (see list above).' -ForegroundColor Yellow
        Write-Host '  Tick your playback device in "Equalizer APO > Device Selector" (Start menu), then restart Windows.'
        $apo = (Get-ItemProperty 'HKLM:\SOFTWARE\EqualizerAPO' -ErrorAction SilentlyContinue).InstallPath
        if (-not $DryRun -and $apo -and (Test-Path "$apo\DeviceSelector.exe")) { Start-Process "$apo\DeviceSelector.exe" }
    } else { Write-Host 'Equalizer APO is attached to all active playback devices.' -ForegroundColor Green }

    Write-Host ''
    Write-Host 'Done. Open "System Compressor" from the desktop or Start menu.' -ForegroundColor Green
    Write-Host 'Opening the window switches the compressor on, closing it switches it off.'
}
catch {
    Write-Host ''
    Write-Host "Installation failed: $($_.Exception.Message)" -ForegroundColor Red
}
if (-not $DryRun) { Read-Host 'Press Enter to close' }
