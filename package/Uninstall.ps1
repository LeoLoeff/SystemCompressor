# System Compressor - uninstaller of the portable (ZIP) version. Removes the plug-in, the Equalizer APO config block,
# the app, the data folder and the shortcuts. Equalizer APO itself and your other settings stay untouched.
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Start-Process powershell -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    exit
}
& (Join-Path $PSScriptRoot 'Remove.ps1')
Remove-Item "$env:ProgramFiles\SystemCompressor" -Recurse -Force -ErrorAction SilentlyContinue
foreach ($dir in "$env:PUBLIC\Desktop", "$env:ProgramData\Microsoft\Windows\Start Menu\Programs") {
    Remove-Item (Join-Path $dir 'System Compressor.lnk') -Force -ErrorAction SilentlyContinue
}
Write-Host '  - removed app and shortcuts'
Write-Host 'Done.' -ForegroundColor Green
Read-Host 'Press Enter to close'
