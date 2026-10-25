# Core installation steps shared by Install.ps1 (portable ZIP) and the Setup.exe. Needs administrator rights.
#   -PluginDll   path of SystemCompressor.dll to install
#   -ResultFile  optional: text file receiving "KEY=value" lines for the caller
#   -DryRun      only report, change nothing
# Exit codes: 0 = installed, 10 = Equalizer APO missing, 11 = installed but Equalizer APO is not attached to all active
# playback devices (names in DEVICES_MISSING), 1 = error.
param([Parameter(Mandatory)][string]$PluginDll, [string]$ResultFile, [switch]$DryRun)
$ErrorActionPreference = 'Stop'

function Step($text) { Write-Host "  - $text" }
function Run([scriptblock]$action, $text) { Step $text; if (-not $DryRun) { & $action } }
function Report($line) { if ($ResultFile) { Add-Content -Path $ResultFile -Value $line -Encoding UTF8 } }

try {
    if ($ResultFile) { Set-Content -Path $ResultFile -Value '' -Encoding UTF8 }
    if (-not (Test-Path $PluginDll)) { throw "Plug-in not found: $PluginDll" }

    $apo = (Get-ItemProperty 'HKLM:\SOFTWARE\EqualizerAPO' -ErrorAction SilentlyContinue).InstallPath
    if (-not $apo) { $apo = "$env:ProgramFiles\EqualizerAPO" }
    if (-not (Test-Path "$apo\EqualizerAPO.dll")) { Report 'EQUALIZER_APO=missing'; exit 10 }
    Step "Equalizer APO found: $apo"

    # Shared data folder: the plug-in runs as LOCAL SERVICE inside the Windows audio service.
    $data = "$env:ProgramData\SystemCompressor"
    Run { New-Item -ItemType Directory -Force $data | Out-Null
          & icacls $data /grant '*S-1-5-19:(OI)(CI)M' '*S-1-1-0:(OI)(CI)RX' '*S-1-5-32-545:(OI)(CI)M' | Out-Null } "Prepare data folder $data"

    # Plug-in: the running audio engine keeps the old DLL locked, so restart it briefly.
    $dll = "$apo\VSTPlugins\SystemCompressor.dll"
    Run { New-Item -ItemType Directory -Force "$apo\VSTPlugins" | Out-Null
          $dependents = @((Get-Service audiosrv).DependentServices | Where-Object Status -eq 'Running')
          Stop-Service audiosrv -Force
          try { Copy-Item $PluginDll $dll -Force }
          finally { Start-Service audiosrv; foreach ($d in $dependents) { Start-Service $d.Name -ErrorAction SilentlyContinue } } } `
        'Install plug-in (Windows audio pauses ~2 seconds)'

    # Equalizer APO configuration: one marked block, existing settings stay untouched.
    $config = "$apo\config\config.txt"
    Run { Copy-Item $config "$config.bak-systemcompressor" -Force
          $text = Get-Content $config -Raw
          $text = [regex]::Replace($text, '(?s)\r?\n?# >>> SystemCompressor.*?# <<< SystemCompressor\r?\n?', '')
          $block = "`r`n# >>> SystemCompressor`r`nVSTPlugin: Library `"$dll`"`r`n# <<< SystemCompressor`r`n"
          Set-Content $config ($text.TrimEnd() + "`r`n" + $block) -Encoding ASCII } 'Add plug-in to Equalizer APO config (backup: config.txt.bak-systemcompressor)'

    # Is Equalizer APO attached to the active playback devices?
    $missing = @()
    $render = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render'
    foreach ($device in Get-ChildItem $render -ErrorAction SilentlyContinue) {
        if ((Get-ItemProperty $device.PSPath).DeviceState -ne 1) { continue }
        $fx = Get-ItemProperty "$($device.PSPath)\FxProperties" -ErrorAction SilentlyContinue
        $attached = $fx -and ($fx.PSObject.Properties | Where-Object { "$($_.Value)" -match 'EC1CC9CE-FAED-4822-828A-82A81A6F018F' })
        if (-not $attached) { $missing += (Get-ItemProperty "$($device.PSPath)\Properties").'{a45c254e-df1c-4efd-8020-67d146a850e0},2' }
    }
    $missing = @($missing | Where-Object { $_ } | Select-Object -Unique)
    Report "APO_DIR=$apo"
    if ($missing.Count -gt 0) { Report "DEVICES_MISSING=$($missing -join ', ')"; Write-Host "Equalizer APO not enabled for: $($missing -join ', ')"; exit 11 }
    Report 'RESULT=ok'
    exit 0
}
catch {
    Report "ERROR=$($_.Exception.Message)"
    Write-Host "Error: $($_.Exception.Message)"
    exit 1
}
