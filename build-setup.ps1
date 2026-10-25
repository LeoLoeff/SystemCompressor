# Builds the Setup wizard: dist\SystemCompressor-Setup.exe (Inno Setup).
#   powershell -ExecutionPolicy Bypass -File .\build-setup.ps1 [-Rebuild]
#   -Rebuild   also rebuilds the package (plug-in + app) first
# Needs Inno Setup 6 (winget install JRSoftware.InnoSetup).
param([switch]$Rebuild)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup' }

if ($Rebuild -or -not (Test-Path "$root\dist\SystemCompressor-win10-win11\app\SystemCompressor.exe")) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File "$root\build-package.ps1"
    if ($LASTEXITCODE -ne 0) { throw 'Package build failed' }
}

# The bundled Equalizer APO installer is third-party and unsigned: download once, pin its SHA-256.
$eapo = "$root\installer\redist\EqualizerAPO-x64.exe"
$pin = "$eapo.sha256"
if (-not (Test-Path $eapo)) {
    New-Item -ItemType Directory -Force "$root\installer\redist" | Out-Null
    Invoke-WebRequest 'https://sourceforge.net/projects/equalizerapo/files/1.4.2/EqualizerAPO-x64-1.4.2.exe/download' -OutFile $eapo -UserAgent 'curl/8'
}
$hash = (Get-FileHash $eapo -Algorithm SHA256).Hash
if (Test-Path $pin) {
    if ((Get-Content $pin -Raw).Trim() -ne $hash) { throw "Equalizer APO installer changed (SHA-256 $hash does not match $pin). Check it before bundling." }
} else { Set-Content $pin $hash }
Write-Host "Bundled Equalizer APO installer SHA-256: $hash"

& $iscc "$root\installer\SystemCompressor.iss"
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compile failed' }
$setup = Get-Item "$root\dist\SystemCompressor-Setup.exe"
"Setup: {0}  ({1:N1} MB)  SHA256 {2}" -f $setup.FullName, ($setup.Length / 1MB), (Get-FileHash $setup.FullName).Hash
