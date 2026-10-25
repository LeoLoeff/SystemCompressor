# Builds the portable package for Windows 10 / 11 (64 bit): dist\SystemCompressor-win10-win11.zip
#   powershell -ExecutionPolicy Bypass -File .\build-package.ps1
# Needs: .NET 8 SDK, Visual Studio Build Tools (C++), CMake. Internet only for the first run
# (downloads the .NET runtime that gets bundled into the self-contained exe).
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = Join-Path $root 'dist\SystemCompressor-win10-win11'
$zip = Join-Path $root 'dist\SystemCompressor-win10-win11.zip'

Write-Host '1/4 Plug-in (statically linked C++ runtime)'
cmake -S "$root\native\SystemCompressorVst2" -B "$root\build\vst2" -G 'Visual Studio 17 2022' -A x64 | Out-Null
cmake --build "$root\build\vst2" --config Release | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Plug-in build failed' }

Write-Host '2/4 App (self-contained single file, no .NET needed on the target PC)'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force "$out\app", "$out\plugin" | Out-Null
$publish = @('publish', "$root\src\CompressorTray\CompressorTray.csproj", '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
    '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true',
    '-p:DebugType=none', '-o', "$out\app")
& dotnet @publish 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) { & dotnet @publish --source https://api.nuget.org/v3/index.json | Out-Null }   # first run: fetch runtime pack
if ($LASTEXITCODE -ne 0) { throw 'App publish failed' }
Get-ChildItem "$out\app" | Where-Object { $_.Name -ne 'SystemCompressor.exe' } | Remove-Item -Recurse -Force

Write-Host '3/4 Assemble'
Copy-Item "$root\build\vst2\Release\SystemCompressor.dll" "$out\plugin\SystemCompressor.dll"
Copy-Item "$root\package\*" $out -Include '*.ps1', '*.cmd' -Force
Copy-Item "$root\ANFORDERUNGEN.md" $out -ErrorAction SilentlyContinue
@"
SYSTEM COMPRESSOR  -  Windows 10 / 11 (64 bit)

1. Install Equalizer APO first (https://sourceforge.net/projects/equalizerapo/) and tick your playback device in its setup.
2. Double-click Install.cmd (asks for administrator rights).
3. Open "System Compressor" from the desktop / Start menu. Opening = compressor on, closing = compressor off.

Uninstall: Uninstall.cmd.   Details: ANFORDERUNGEN.md
"@ | Set-Content "$out\README.txt" -Encoding UTF8

Write-Host '4/4 Zip'
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$out\*" -DestinationPath $zip
Get-ChildItem $out -Recurse -File | ForEach-Object { '{0,10:N0}  {1}' -f $_.Length, $_.FullName.Substring($out.Length + 1) }
"ZIP: $zip  ({0:N1} MB)  SHA256 {1}" -f ((Get-Item $zip).Length / 1MB), (Get-FileHash $zip).Hash
