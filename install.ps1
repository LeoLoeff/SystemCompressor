# Developer install: puts the freshly built plug-in (build\vst2\Release) into Equalizer APO.
# Uses the same code as the Setup (package\Apply.ps1). Run once from an elevated PowerShell:
#   powershell -ExecutionPolicy Bypass -File .\install.ps1
$ErrorActionPreference = 'Stop'
$dll = Join-Path $PSScriptRoot 'build\vst2\Release\SystemCompressor.dll'
if (-not (Test-Path $dll)) { throw "Plug-in not built: $dll (see README: cmake --build build\vst2 --config Release)" }
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Please run this script as administrator.'
}
& (Join-Path $PSScriptRoot 'package\Apply.ps1') -PluginDll $dll
switch ($LASTEXITCODE) {
    0  { Write-Host 'Done. Open SystemCompressor.exe.' }
    11 { Write-Host 'Installed, but enable your playback device in Equalizer APO (Device Selector) and restart Windows.' }
    10 { throw 'Equalizer APO is not installed.' }
    default { throw 'Installing the plug-in failed (see message above).' }
}
