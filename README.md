# System Compressor (Windows 10 / 11)

Systemweiter Wiedergabe-Kompressor, optimiert für **Filme und Sprachverständlichkeit**,
mit Bedienfenster (Drehregler für alle Einstellungen, Ein/Aus-Knopf, Pegelmeter für Eingang, Ausgang und Gain Reduction).

## Architektur

```
Browser / VLC / Windows-Audio (Shared Mode)
  -> Windows Audio Engine -> Equalizer APO -> SystemCompressor.dll (VST2) -> Lautsprecher
```

- Ein eigenes Windows-APO würde Treiber-Signierung erfordern; ein virtuelles Audiogerät
  oder Loopback würde Latenz erzeugen. Equalizer APO sitzt direkt im Wiedergabepfad.
- **Equalizer APO lädt nur VST2-Plugins** (auch 1.4.2). Das Plugin ist deshalb eine
  eigenständige VST2-ABI-DLL (`native/SystemCompressorVst2`), ohne Steinberg SDK.
- Das Plugin läuft im Windows-Audiodienst (Konto *Lokaler Dienst*) und liest deshalb
  `C:\ProgramData\SystemCompressor\settings.json` (die App legt den Ordner an und gibt ihn
  für den Audiodienst frei) in einem Hintergrund-Thread (alle ~250 ms). Dort schreibt es auch
  laufend `meter-*.txt`; die App zeigt daraus Meter und Status. Der Audio-Thread nutzt nur Atomics; kein Lock,
  keine Allokation, kein Dateizugriff. Änderungen in der App sind sofort hörbar.
- Ohne `settings.json` gelten die Film-Defaults (identisch in Plugin und App).

## Film-/Sprach-Optimierung

| Maßnahme | Wirkung |
|---|---|
| Defaults: Threshold -20 dB, Ratio 3.5:1, Attack 0.1 ms (Minimum), Release 250 ms, Gain 0 dB, Knee 5 dB | Flüstern und Explosionen rücken zusammen |
| Detektor mit 2-stufigem Hochpass (120 Hz) | Subbass/Explosionen drücken die Sprache nicht herunter |
| Einstellbares Soft Knee (Regler „Knee“, Standard 5 dB) | unauffälliger, weicher Einsatz der Kompression |
| Sprach-EQ: +2 dB bei 3 kHz (0–8 dB, 1–8 kHz einstellbar) | Konsonanten werden verständlicher; 0 dB = aus |
| Gain-Rad (Makeup, Standard 0 dB, einstellbar -24 bis **max. +5 dB**, geglättet) | Lautheit ohne Knackser einstellen |

## Klang und Latenz (im Smoke-Test geprüft)

- **Neutral:** unterhalb der Schwelle, EQ aus, Gain 0 dB: Signal bit-genau unverändert.
- **Latenz 0:** kein Look-ahead, der Impuls erscheint im selben Sample.
- **Verzerrung:** THD 0,002 % bei aktiver Kompression (1 kHz, -20 dBFS).
- **Kein Clipping:** Ein weicher Begrenzer (ab ca. -1 dBFS) rundet Spitzen ab, die vor dem Attack
  durchrutschen; der Ausgang bleibt unter Vollaussteuerung. Verarbeitung in 32-bit Float.
- Denormal-Schutz im Audio-Thread (keine CPU-Spitzen in Stille).

## Bedienung

- `SystemCompressor.exe` öffnet das Fenster (normaler Taskleisten-Button; per Rechtsklick auf die
  Verknüpfung „An Taskleiste anheften“, danach genügt ein Klick). Ein zweiter Start holt das laufende
  Fenster nur nach vorn.
- **Fenster öffnen = Kompressor an. Fenster schließen (X) = Kompressor aus.** Beim Herunterfahren bleibt
  der gespeicherte Zustand erhalten, der Kompressor wirkt dann auch nach dem Neustart ohne App.
- Meter: IN / OUT (Spitzenpegel, Peak-Hold) und GR (Gain Reduction, hängt von oben). Das Plugin meldet
  die Werte bis zu 20-mal pro Sekunde in `C:\ProgramData\SystemCompressor\meter-*.txt`.
- Die Statuszeile zeigt, ob das Plugin im Audiodienst läuft und wie stark es gerade arbeitet.

## Bauen und Installieren

Voraussetzungen: .NET 8 SDK, Visual Studio Build Tools (C++), CMake, Equalizer APO.

```powershell
dotnet build .\src\CompressorTray\CompressorTray.csproj -c Release
cmake -S .\native\SystemCompressorVst2 -B .\build\vst2 -G "Visual Studio 17 2022" -A x64
cmake --build .\build\vst2 --config Release
# Als Administrator (kopiert das Plugin, sichert config.txt, ergänzt einen markierten Block):
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

Danach `src\CompressorTray\bin\Release\net8.0-windows\SystemCompressor.exe` starten
(`bin\` und `obj\` sind Build-Ausgaben und dürfen jederzeit gelöscht werden; `dotnet build` erzeugt sie neu).

Grenzen: ASIO, WASAPI-Exclusive und DRM-Audiowege umgehen Systemeffekte.

## Tests

```powershell
dotnet run --project .\tests\CompressorDspSmokeTest -c Release   # DSP-Test + Vergleich Plugin = Referenz
```

## Auslieferung an andere Rechner (Windows 10 / 11)

```powershell
powershell -ExecutionPolicy Bypass -File .\build-package.ps1   # ZIP: dist\SystemCompressor-win10-win11.zip
powershell -ExecutionPolicy Bypass -File .\build-setup.ps1 -Rebuild   # Setup-Assistent: dist\SystemCompressor-Setup.exe (baut das Paket mit)
```

**Empfohlen: `SystemCompressor-Setup.exe`** (eine Datei, ca. 76 MB). Sie installiert App und Plugin, erkennt Equalizer APO und
installiert es auf Wunsch mit (gebündelter, unveränderter Installer 1.4.2, Prüfsumme in `installer\redist\EqualizerAPO-x64.exe.sha256`),
öffnet die Geräteauswahl, bietet den Neustart an und lässt sich über „Apps & Features“ sauber deinstallieren.
Baut man den Setup neu, ändert sich seine SHA-256-Prüfsumme (Zeitstempel); nach jedem Bau neu ausrechnen (`Get-FileHash`).
Bauen braucht zusätzlich Inno Setup 6 (`winget install JRSoftware.InnoSetup`).

Alternative ohne Assistent: ZIP entpacken, Equalizer APO vorher selbst installieren (Gerät ankreuzen), `Install.cmd` doppelklicken.
Die App ist eigenständig (kein .NET nötig), das Plugin braucht keine Visual-C++-Laufzeit.
Alle Funktionen, Schnittstellen und Entscheidungen: [ANFORDERUNGEN.md](ANFORDERUNGEN.md).

Plugin-Test (lädt die DLL wie ein Host, läuft isoliert in `%TEMP%` und stört eine installierte Kopie nicht; `--soak <Sekunden>` verlängert den Dauertest):

```powershell
cmake -S tests/PluginHostTest -B build/hosttest -G "Visual Studio 17 2022" -A x64
cmake --build build/hosttest --config Release
build\hosttest\Release\host.exe build\vst2\Release\SystemCompressor.dll
```

## Weitere Dokumentation

- [ANFORDERUNGEN.md](ANFORDERUNGEN.md): alle Funktionen, Schnittstellen, Entscheidungen, Tests und das Abnahmeprotokoll (Abschnitt 15).
- Hinweis zum Aufräumen: `bin\`, `obj\`, `build\` und `dist\SystemCompressor-win10-win11\` sind Build-Ausgaben und jederzeit löschbar; `build-setup.ps1 -Rebuild` erzeugt alles neu.
