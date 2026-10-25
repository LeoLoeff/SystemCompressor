# Removes the plug-in, the Equalizer APO config block and the shared data folder. Needs administrator rights.
# Equalizer APO itself and the user's other Equalizer APO settings stay untouched.
$ErrorActionPreference = 'Stop'
try {
    $apo = (Get-ItemProperty 'HKLM:\SOFTWARE\EqualizerAPO' -ErrorAction SilentlyContinue).InstallPath
    if (-not $apo) { $apo = "$env:ProgramFiles\EqualizerAPO" }
    Get-Process SystemCompressor -ErrorAction SilentlyContinue | Stop-Process -Force

    $config = "$apo\config\config.txt"
    if (Test-Path $config) {
        $text = Get-Content $config -Raw
        $text = [regex]::Replace($text, '(?s)\r?\n?# >>> SystemCompressor.*?# <<< SystemCompressor\r?\n?', '')
        Set-Content $config ($text.TrimEnd() + "`r`n") -Encoding ASCII
        Write-Host '  - removed config block'
    }
    $dependents = @((Get-Service audiosrv).DependentServices | Where-Object Status -eq 'Running')
    Stop-Service audiosrv -Force
    try { Remove-Item "$apo\VSTPlugins\SystemCompressor.dll" -Force -ErrorAction SilentlyContinue }
    finally { Start-Service audiosrv; foreach ($d in $dependents) { Start-Service $d.Name -ErrorAction SilentlyContinue } }
    Write-Host '  - removed plug-in'
    Remove-Item "$env:ProgramData\SystemCompressor" -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host '  - removed data folder'
    exit 0
}
catch { Write-Host "Remove failed: $($_.Exception.Message)"; exit 1 }
