# System Compressor – Funktionen und Anforderungen

Systemweiter Audio-Kompressor für Windows 10 und 11 (64 Bit), optimiert für Filme und Sprachverständlichkeit.
Dieses Dokument beschreibt Verhalten, Schnittstellen und Entscheidungen so genau, dass das Programm auf
einem anderen Rechner neu gebaut, installiert, geprüft und weiterentwickelt werden kann.

Sprachregel: Die Oberfläche des Programms ist **durchgehend englisch** (Beschriftungen, Tooltips, Statuszeilen).
Dieses Dokument ist deutsch; Oberflächentexte stehen in `"Anführungszeichen"` und sind verbindlich.

---

## 1. Zweck

| | |
|---|---|
| Ziel | Der gesamte Wiedergabeton (Browser, VLC, Videos, Spiele) wird in Echtzeit komprimiert, sodass leise Dialoge verständlich bleiben und laute Effekte nicht mehr herausstechen. |
| Bedienung | Ein kleines Fenster mit Drehreglern, Ein/Aus-Knopf und Pegelmeter. |
| Nicht-Ziele | Kein Aufnahme-/Mikrofonpfad, kein Mehrband-Kompressor, kein Limiter mit Look-ahead, keine Cloud-/Netzwerkfunktion. |

## 2. Zielsysteme und Voraussetzungen

| Anforderung | Wert |
|---|---|
| Betriebssystem | Windows 10 (ab Version 1809, Build 17763) und Windows 11, jeweils 64 Bit |
| Audiopfad | Windows-Shared-Mode. **ASIO, WASAPI-Exklusiv und DRM-geschützte Wege umgehen Systemeffekte** und werden nicht komprimiert. |
| Host für das Plugin | **Equalizer APO** (nur mit Version 1.4.2 getestet; sie lädt VST2-Plugins). Es muss für das gewünschte Wiedergabegerät aktiviert sein (`DeviceSelector.exe`). |
| Rechte zur Installation | Administrator (Dateien in `Program Files`, Neustart des Windows-Audiodienstes). |
| Rechte zur Laufzeit | Normaler Benutzer. |
| Laufzeitabhängigkeiten | Keine: App ist *self-contained* (enthält .NET 8), Plugin linkt die C++-Laufzeit statisch und benötigt nur Windows-Systembibliotheken (`kernel32`, `shell32`, `ole32`). |
| Netzwerk | Wird nicht benutzt (nur beim Bauen für den einmaligen Download der .NET-Laufzeit). |

## 3. Architektur

```
Browser / VLC / Windows-Audio (Shared Mode)
  -> Windows Audio Engine (audiodg.exe, Konto "LOKALER DIENST")
     -> Equalizer APO  ->  SystemCompressor.dll (VST2)  ->  Lautsprecher
                                  ^   |
              settings.json (liest) |   | meter-*.txt (schreibt, bis 20x/s)
                                  |   v
                       SystemCompressor.exe (Fenster, Benutzerkonto)
                       gemeinsamer Ordner: C:\ProgramData\SystemCompressor
```

### 3.1 Komponenten

| Komponente | Datei(en) | Aufgabe |
|---|---|---|
| **DSP-Kern (C#)** | `src/CompressorDsp/StereoCompressor.cs`, `CompressorSettings.cs` | Referenzimplementierung des Algorithmus und Definition aller Parameter, Grenzen und Standardwerte. Wird von den Tests geprüft. |
| **Plugin (C++)** | `native/SystemCompressorVst2/SystemCompressor.cpp` | Dieselbe Signalverarbeitung im Audiopfad. Implementiert die VST2-C-Schnittstelle selbst (kein SDK). Liest Einstellungen, schreibt Meterdaten. |
| **App (C#, WinForms)** | `src/CompressorTray/*.cs` (Projekt heißt historisch `CompressorTray`, Programmdatei `SystemCompressor.exe`) | Bedienfenster, speichert Einstellungen, zeigt Meter und Status. |
| **Installation (Skripte)** | `package/Apply.ps1` (gemeinsamer Kern), `package/Remove.ps1`, `package/Install.ps1/.cmd`, `package/Uninstall.ps1/.cmd`, `install.ps1` (Entwickler), `build-package.ps1` | Kopiert Plugin und App, legt den Datenordner an, ergänzt die Equalizer-APO-Konfiguration. |
| **Setup-Assistent** | `installer/SystemCompressor.iss`, `installer/notice.txt`, `installer/redist/EqualizerAPO-x64.exe` (+ `.sha256`), `build-setup.ps1` | Inno-Setup-Installer `dist/SystemCompressor-Setup.exe`; ruft dieselben Kernskripte auf. |
| **Tests** | `tests/CompressorDspSmokeTest`, `tests/PluginHostTest` | DSP-Test (inkl. Vergleich Plugin = Referenz) und Plugin-Host-Test inkl. Dauertest (siehe 9). |

### 3.2 Warum diese Architektur (Entscheidungen)

1. **Equalizer APO als Host.** Ein eigenes Windows-APO bräuchte Treiber-Signierung; ein virtuelles Audiogerät oder Loopback würde hörbare Latenz erzeugen (Lip-Sync bei Filmen). Equalizer APO sitzt direkt im Wiedergabepfad.
2. **VST2 statt VST3.** Equalizer APO (auch 1.4.2) lädt nur VST 2.x. Das Plugin implementiert deshalb die VST2-C-Struktur (`AEffect`, Dispatcher-Opcodes) selbst.
3. **Einstellungen in `C:\ProgramData\SystemCompressor`.** Equalizer APO und damit das Plugin laufen in `audiodg.exe` unter dem Konto *LOKALER DIENST*, das nicht auf `%LocalAppData%` des Benutzers zugreifen kann. Der gemeinsame Ordner bekommt per ACL: *Lokaler Dienst* = Ändern, *Jeder* = Lesen/Ausführen (die App setzt das selbst beim Start, `Install.ps1` ebenfalls).
4. **Plugin liest Datei, statt Parameter über Equalizer APO zu bekommen.** Die Parametersyntax von `VSTPlugin:` ist nicht verlässlich dokumentiert; die Datei erlaubt außerdem Live-Änderungen ohne Neuladen der Konfiguration.
5. **DPI-unaware.** Das Layout ist pixelbasiert. Windows skaliert das Fenster bei 125/150 % als Ganzes, dadurch sieht es auf jedem Rechner gleich aus (Text ist bei Skalierung leicht weich).
6. **Eigener Fensterrahmen** (ohne Systemrahmen) für das goldene Design; trotzdem normaler Taskleisten-Button.

## 4. Signalverarbeitung (verbindlich)

Eingang: 32-bit-Float-Stereo (Kanal 0/1), beliebige Blockgröße, Abtastrate vom Host. Pro Abtastwert (Frame):

1. **Eingangsschutz:** NaN/±Unendlich im Eingang werden durch 0 ersetzt (sonst würden die IIR-Zustände dauerhaft vergiftet).
2. **Detektor:** Beide Kanäle werden je durch zwei hintereinandergeschaltete Ein-Pol-Hochpässe (Grenzfrequenz 120 Hz, 12 dB/Oktave) geschickt; Detektorpegel = größter Betrag beider Kanäle (stereo-verkoppelt, **ohne Look-ahead**). `inputDb = 20·log10(max(peak, 1e-7))`.
3. **Kennlinie mit Soft Knee** (`over = inputDb − Threshold`, `slope = 1/Ratio − 1`, Breite `K = Knee`):
   - `2·over < −K` → Gain-Änderung 0 dB
   - `K > 0` und `2·over ≤ K` → `slope · (over + K/2)² / (2K)`
   - sonst → `slope · over`
   - `K = 0` ergibt ein hartes Knie.
4. **Hüllkurve** (Ein-Pol, in dB): Koeffizient `c = exp(−1 / (t · fs))` mit `t` = Attack bzw. Release in Sekunden. Ist das Ziel kleiner als der aktuelle Wert (mehr Dämpfung) gilt Attack, sonst Release: `gainDb = target + c·(gainDb − target)`.
5. **Gain-Regler (Makeup):** linearer Zielfaktor `10^(Gain/20)`, per Ein-Pol (10 ms) geglättet (kein Knacken beim Drehen). Gesamtgain `g = 10^(gainDb/20) · makeup`.
6. **Sprach-EQ** (nur wenn EQ Boost > 0,05 dB): RBJ-Peaking-Filter, Güte 0,9, Mittenfrequenz *Frequency*, Anhebung *EQ Boost*, Transposed-Direct-Form-II. Beim Einschalten werden die Filterzustände genullt (kein Klick).
7. **Sicherheits-Softclip:** Beträge bis 0,89 (≈ −1 dBFS) bleiben unverändert; darüber `0,89 + 0,11·tanh((|x| − 0,89)/0,11)` mit Vorzeichen. Ausgang bleibt ≤ 1,0.
8. **Aus-Zustand:** Ist *Enabled* = aus, wird der Eingang **bit-genau** unverändert ausgegeben.

Weitere Regeln:
- **Latenz 0 Samples:** Kein Puffer, kein Look-ahead; das Plugin meldet `initialDelay = 0`. Ein Impuls erscheint im selben Sample am Ausgang.
- **Neutralität:** Liegt der Pegel unter Threshold − Knee/2, EQ Boost = 0 und Gain = 0 dB, ist das Signal bit-genau unverändert.
- **Audio-Thread:** keine Speicherallokation, keine Locks, kein Dateizugriff, keine Logs. Denormale Zahlen werden per `MXCSR` (FTZ/DAZ) abgeschaltet und danach wiederhergestellt.
- **Blockgrößen-Unabhängigkeit:** Dasselbe Signal liefert in beliebiger Blockaufteilung identische Ausgabe.
- **Suspend/Resume** (`effMainsChanged` mit Wert 1) setzt alle Zustände zurück.

### 4.1 Stabilitätsregeln des Plugins (laufen im Windows-Audiodienst, ein Absturz würde den ganzen Ton beenden)

- **Kein Fehler verlässt das Plugin:** `VSTPluginMain` und der Hintergrund-Thread sind in `try/catch` gekapselt. Schlägt das Anlegen des Threads fehl, arbeitet das Plugin mit seinen aktuellen Werten weiter; schlägt das Laden fehl, liefert `VSTPluginMain` `nullptr` („Plugin konnte nicht geladen werden“) statt den Audiodienst abstürzen zu lassen.
- **Defensive Puffer:** Fehlen Zeiger oder der zweite Kanal (unerwartetes Mono-Layout), bleibt das Audio unverändert, es gibt keinen Nullzeiger-Zugriff. `frames <= 0` ist ein No-op.
- **Ungültige Zahlen werden überall abgefangen:** NaN/Inf im Audio (→ 0), in `settings.json` (NaN ignoriert, Unendlich wird begrenzt) und in Host-Parametern (ignoriert). Die Abtastrate muss 8 000 … 768 000 Hz betragen, sonst gilt 48 000 Hz. Parameter-Indizes und Textpuffer werden geprüft.
- **Doppeltes `effOpen`** startet keinen zweiten Thread (das würde den Prozess beenden).
- **Priorität:** Der Hintergrund-Thread läuft mit `THREAD_PRIORITY_BELOW_NORMAL` und konkurriert nie mit dem Audio-Thread.
- **Gleiches Ergebnis wie die Referenz:** C#-Referenz und Plugin rechnen bitgleich (siehe N13); die 10-ms-Glättung des Gain-Reglers ist in beiden enthalten.

## 5. Parameter

| Oberfläche | JSON-Schlüssel | Bereich | Standard | Skala | Einheit | Wirkung |
|---|---|---|---|---|---|---|
| (Knopf) "COMPRESSOR ON/OFF" | `Enabled` | an/aus | an | – | – | Aus = Durchleitung |
| "Threshold" | `ThresholdDb` | −60 … 0 | **−20** | linear, 1 dB | dB | Pegel, ab dem komprimiert wird |
| "Ratio" | `Ratio` | 1 … 20 | **3,5** | linear, 1 Nachkommastelle | :1 | Stärke der Dämpfung |
| "Attack" | `AttackMs` | 0,1 … 200 | **0,1 (Minimum)** | logarithmisch, 1 Nachkommastelle | ms | Reaktionszeit auf laute Stellen |
| "Release" | `ReleaseMs` | 10 … 2000 | **250** | logarithmisch, ganzzahlig | ms | Erholzeit |
| "Gain" | `MakeupGainDb` | −24 … **+5** | **0** | linear, 0,5 dB, mit Vorzeichen | dB | Lautstärke nach der Kompression; **maximal +5 dB** |
| "Knee" | `KneeDb` | 0 … 24 | **5** | linear, 1 Nachkommastelle | dB | Weichheit des Einsatzes |
| "EQ Boost" | `SpeechBoostDb` | 0 … 8 | **2** | linear, 0,5 dB, mit Vorzeichen | dB | Anhebung des Sprachbandes, 0 = aus |
| "Frequency" | `SpeechFreqHz` | 1000 … 8000 | **3000** | logarithmisch, ganzzahlig | Hz | Mitte des Sprach-EQ |

Regeln:
- Alle Werte werden beim Laden **begrenzt** (`CompressorSettings.Validate()` in der App, `std::clamp` im Plugin). Werte außerhalb des Bereichs sind nie wirksam.
- Die Standardwerte stehen **identisch** in `CompressorSettings.cs` (C#) und im Konstruktor von `Plugin` (C++). Wer einen ändert, ändert beide, plus Tests.
- Zahlen werden in der Oberfläche mit Punkt als Dezimaltrenner angezeigt (`InvariantCulture`), z. B. `3.5 :1`.
- Zusätzlich exponiert das Plugin die Parameter an den Host (VST2: Threshold, Ratio, Attack, Release, Gain, Enabled, Speech, SpeechFreq, Knee; normalisiert 0…1).

## 6. Dateien und Protokolle

### 6.1 `C:\ProgramData\SystemCompressor\settings.json` (App → Plugin)

```json
{
  "Settings": {
    "Enabled": true,
    "ThresholdDb": -20,
    "Ratio": 3.5,
    "AttackMs": 0.1,
    "ReleaseMs": 250,
    "MakeupGainDb": 0,
    "SpeechBoostDb": 2,
    "SpeechFreqHz": 3000,
    "KneeDb": 5
  }
}
```

- Geschrieben von `System.Text.Json` (Punkt als Dezimaltrenner). Das Plugin sucht jeden Schlüssel per Textsuche und liest die folgende Zahl bzw. `true`/`false`.
- **Atomar geschrieben:** zuerst `settings.json.new`, dann `File.Move(..., overwrite)`. Die App wiederholt das bis zu 10 × (30 ms Pause) und stürzt bei Fehlschlag nicht ab; `LastSaveSucceeded` steuert eine Fehlermeldung in der Statuszeile.
- **Gelesen** vom Plugin alle ~250 ms, nur wenn sich die Änderungszeit geändert hat; mit `FILE_SHARE_READ|WRITE|DELETE`, damit das Ersetzen nie blockiert wird.
- Fehlt die Datei oder ist sie unlesbar/abgeschnitten, bleiben die zuletzt gültigen Werte (beim Start die Standardwerte) in Kraft.

### 6.2 `C:\ProgramData\SystemCompressor\meter-<Zeiger>.txt` (Plugin → App)

Pro Plugin-Instanz eine Datei, **feste Länge** (97 Byte), an Ort und Stelle überschrieben (keine Kürzung, kein Umbenennen):

```
tick=00000000000016277031      GetTickCount64() des Schreibzeitpunkts (ms seit Systemstart)
blocks=0000000005              verarbeitete Audioblöcke seit dem letzten Schreiben
inDb=-00013.7                  Spitzenpegel Eingang seit dem letzten Schreiben (dBFS, Minimum -140)
outDb=-00016.7                 Spitzenpegel Ausgang
grDb=000007.3                  größte Gain Reduction (positiv, dB)
enabled=1
```

- Aktualisierung alle 50 ms, solange Audio läuft; im Leerlauf einmal pro Sekunde (Heartbeat mit `blocks=0`).
- Die App liest alle `meter-*.txt` (Dateiliste einmal pro Sekunde neu), wertet nur Dateien mit `tick`-Alter ≤ 3,5 s aus und fasst mehrere Instanzen per Maximum zusammen. „Audio läuft“ = `blocks > 0` und Alter < 400 ms. Dateien mit Alter > 60 s (Absturz des Audiodienstes) löscht die App.
- Das Plugin löscht seine Datei beim Schließen (`effClose`).

### 6.3 Equalizer-APO-Konfiguration

`C:\Program Files\EqualizerAPO\config\config.txt` erhält am Ende genau einen markierten Block; vorhandene Einstellungen bleiben unberührt, vorher entsteht `config.txt.bak-systemcompressor`:

```
# >>> SystemCompressor
VSTPlugin: Library "C:\Program Files\EqualizerAPO\VSTPlugins\SystemCompressor.dll"
# <<< SystemCompressor
```

Der Installer ersetzt einen vorhandenen Block, statt ihn zu verdoppeln. Der Uninstaller entfernt ihn.

### 6.4 VST2-Schnittstelle des Plugins

Exportiert werden `VSTPluginMain` (und `main_plugin`). Struktur `AEffect` mit Magic `'VstP'`, Flag `effFlagsCanReplacing`, 2 Eingänge, 2 Ausgänge, 1 Programm, `uniqueID = 'SCp1'`, Version 1000. Unterstützte Opcodes: `effOpen` (startet den Hintergrund-Thread), `effClose`, `effSetSampleRate` (Rate im Argument `opt`), `effMainsChanged`, `effGetProgram`, `effGetProgramName`, `effGetParamName/Label/Display`, `effGetEffectName`, `effGetProductString`, `effGetVendorString`, `effGetVendorVersion`, `effGetVstVersion` (2400), `effCanDo`. Parameter-Indizes außerhalb des Bereichs und leere Zeiger werden ignoriert.

### 6.5 Test-Schalter `SYSTEMCOMPRESSOR_DATA_DIR`

Ist diese Umgebungsvariable gesetzt, benutzt das Plugin diesen Ordner statt `C:\ProgramData\SystemCompressor` (für `settings.json` und `meter-*.txt`). Die Testprogramme setzen sie auf einen temporären Ordner, damit sie weder die Einstellungen noch den laufenden Ton einer installierten Kopie stören. Im Audiodienst ist sie nie gesetzt.

## 7. Funktionale Anforderungen der App

| Nr. | Anforderung |
|---|---|
| F1 | Die App ist ein normales Fenster mit **Taskleisten-Button** (kein Tray-Symbol, kein Autostart). Über eine Verknüpfung lässt sie sich an die Taskleiste anheften; ein Klick darauf öffnet das Fenster. |
| F2 | **Einzelinstanz:** Ein zweiter Start (z. B. Klick auf das angeheftete Symbol) startet nichts Neues, sondern holt das laufende Fenster nach vorn (benannter Mutex `Local\SystemCompressorApp`, Ereignis `Local\SystemCompressorApp.Show`). |
| F3 | **Fenster öffnen = Kompressor an** (`Enabled=true` wird beim Start gespeichert). **Fenster schließen = Kompressor aus** (`Enabled=false`) – bei X-Klick, Alt+F4, „Fenster schließen“ in der Taskleiste und Beenden von außen (ein hartes Beenden per `Stop-Process -Force` bzw. ein Absturz lässt den gespeicherten Zustand unverändert). **Ausnahme:** beim Herunterfahren/Abmelden von Windows (`CloseReason.WindowsShutDown`) bleibt der gespeicherte Zustand erhalten, damit der Kompressor nach dem Neustart auch ohne App wirkt. |
| F4 | Ein/Aus-Knopf oben: Beschriftung `"COMPRESSOR  ON"` (goldgefüllt) bzw. `"COMPRESSOR  OFF"` (nur Umriss). Wirkt sofort. |
| F5 | Acht Drehregler (siehe 5). Bedienung: senkrecht ziehen (150 px = voller Bereich), **Shift** = feiner (×0,2), Mausrad, Pfeiltasten, **Doppelklick = Standardwert**. Jede Änderung wird sofort gespeichert und ist über das Plugin live hörbar. Wertanzeige unter dem Regler, Beschriftung in Versalien. |
| F6 | Knopf `"Reset"`: setzt alle acht Regler auf die Standardwerte (nicht den Ein/Aus-Zustand). |
| F7 | **Meter** (rechts): drei segmentierte Balken **IN**, **OUT** (−60…0 dBFS, 30 Segmente) und **GR** (0…24 dB, hängt von oben herab), Peak-Hold-Strich, Zahlenwerte darunter (`"IN -13.0 dB"`, `"OUT -16.7 dB"`, `"GR 7.3 dB"`). Farben der Segmente: IN/OUT Gold, ab −12 dB Hellgold, ab −3 dB Rot; GR Gold, ab 6 dB Orange, ab 12 dB Rot. GR-Zahl färbt sich entsprechend. Ballistik: sofortiger Anstieg, Abfall 40 dB/s (GR 45 dB/s), Peak-Hold 1,5 s dann 12 dB/s. Aktualisierung 30 ×/s (Timer 33 ms). |
| F8 | **Statuszeile** (zweimal pro Sekunde): `"Plugin not responding – check Equalizer APO / output device"`, `"Ready – no audio playing"`, `"Off – audio passes through unchanged"`, `"Active – level is below the threshold"`, `"Working – level reduced by up to X.X dB"`; bei Speicherfehler `"Cannot save settings - check permissions of C:\ProgramData\SystemCompressor"`. |
| F9 | Tooltips an allen Reglern (englisch, erklären Wirkung) und am Reset-Knopf. |
| F10 | Beim Start: gemeinsamen Ordner anlegen und ACL setzen, Einstellungen laden (und begrenzen), nicht mehr benötigten Autostart-Eintrag `HKCU\...\Run\SystemCompressor` entfernen, Fenster zentriert öffnen. |
| F11 | Fenstersteuerung in der eigenen Titelleiste: Minimieren, Schließen (Hover: rot), Verschieben durch Ziehen der Titelleiste. Nicht maximierbar, feste Größe. |
| F12 | **Speichern entprellt:** Während ein Regler bewegt wird, schreibt die App höchstens alle 60 ms und einmal direkt nach der letzten Bewegung; Ein/Aus und Schließen schreiben sofort. Änderungen bleiben so in < 0,3 s hörbar, ohne dass pro Sekunde Dutzende Dateien geschrieben werden. |
| F13 | **Fehlerprotokoll:** Unerwartete Fehler werden nach `C:\ProgramData\SystemCompressor\app-error.log` geschrieben (höchstens 50 Einträge pro Lauf, Datei höchstens 100 KB) und die App läuft weiter. Schlägt der Start selbst fehl, zeigt ein Dialog die Meldung und den Pfad des Protokolls. |
| F14 | **Minimiert = keine Arbeit:** Solange das Fenster minimiert ist, liest die App keine Meterdateien und zeichnet nichts. |

## 8. Oberflächenspezifikation

- **Fenster:** 492 × 556 px (Client), ohne Systemrahmen, Schatten (`CS_DROPSHADOW`), Windows-11-Eckenrundung abgeschaltet (`DWMWA_WINDOW_CORNER_PREFERENCE = DONOTROUND`), damit der Rahmen rechteckig bleibt. Titelleiste 38 px, Titel `"S Y S T E M   C O M P R E S S O R"` in Gold, App-Symbol links.
- **Rahmen:** 2 px Gold, innen eine 1-px-Linie in 70 % Gold-Transparenz; Titelleiste mit dezentem Verlauf und goldener Haarlinie.
- **Farben:** Hintergrund `#1F2024`, Panels `#272830`, unbeleuchtete Segmente `#34353C`, Gold `#D4AF37`, helles Gold `#F0D37C`, dunkles Gold `#806926`, Text `#E8E5DE`, gedämpfter Text `#96969E`, Warnung `#E88C28`, Gefahr `#D6483C`.
- **Schrift:** Segoe UI (Normal 9 pt, Titel 10,5 pt fett, Beschriftungen 7,5–8 pt fett).
- **Raster:** Regler 110 × 120 px; Spalten bei x = 12, 130, 248; Reihe 1 bei y = 132, Reihe 2 bei y = 260; Abschnitt `"SPEECH EQ"` mit Trennlinie darunter, Reihe 3 bei y = 412 (EQ Boost, Frequency, Reset-Knopf). Meter rechts (108 × 404 px). Hinweiszeile unten: `"Drag or mouse wheel: change · Shift: fine · Double-click: default"`.
- **Regler-Zeichnung:** 270°-Bogen (Start 135°), grauer Hintergrundbogen, goldener Wertbogen, dunkler Knopfkörper mit Verlauf, heller Zeiger, gepunkteter Fokusring.
- **Symbol (`app.ico`):** dunkle Scheibe, goldener Ring, Regler-Bogen, heller Zeiger; Größen 16/24/32/48/64/256 px.

## 9. Nichtfunktionale Anforderungen und Nachweis

| Nr. | Anforderung | Nachweis |
|---|---|---|
| N1 | Latenz 0 Samples | DSP-Test: Impuls erscheint im selben Sample; Messung im Plugin selbst in 9.1 (11 Konfigurationen, `initialDelay` = 0, EQ-Gruppenlaufzeit ≤ 0,28 ms) |
| N2 | Neutral: bit-genau unverändert unter Schwelle/aus | DSP- und Plugin-Test |
| N3 | Verzerrung: THD ≤ 0,1 % bei aktiver Kompression (1 kHz, −20 dBFS); gemessen ≈ 0,002 % | DSP-Test (Goertzel, Oberwellen 2–6) |
| N4 | Kein Clipping: Ausgang ≤ 1,0 auch bei Vollaussteuerung, +5 dB Gain, +8 dB EQ | DSP-Test |
| N5 | Robust gegen NaN/Inf, zerstörte/abgeschnittene Einstellungsdatei, Parameter außerhalb des Bereichs, ungültige Host-Aufrufe, Suspend/Resume, schnelle Dateiänderungen (150 Neuschreibungen) | Plugin-Test |
| N6 | Blockgrößen-Unabhängigkeit (Differenz 0 bei 37 vs. 96000 Frames) | Plugin-Test |
| N7 | Live-Änderung wirkt ≤ ~0,3 s (z. B. Gain −6 dB → ×0,501) | Plugin-Test und Messung im Audiodienst |
| N8 | Keine Abhängigkeit von Visual-C++-Redistributable oder .NET-Installation | `dumpbin /dependents`, self-contained Veröffentlichung |
| N9 | Plugin und App überleben Absturz/Neustart des jeweils anderen | Heartbeat/Altersprüfung, Datei-Neuaufnahme |
| N10 | Die Installation verändert keine fremden Equalizer-APO-Einstellungen | markierter Block + Sicherung |
| N11 | Stabil im Dauerbetrieb: keine Abstürze, keine nichtendlichen oder übersteuerten Ausgaben, keine Handle- oder Speicherlecks bei zufälligen Blockgrößen (1–2048), zufälligen Signalen (inkl. NaN/Inf/Denormale), zufälligen und kaputten Einstellungen und ständigem Neuladen des Plugins | Plugin-Host-Test „soak“ (Standard 8 s, `--soak <Sekunden>`); 20-s-Lauf: 58 008 Blöcke, 20 Instanzen, Handles 115 → 115, Speicher 1,9 → 1,9 MB |
| N12 | Geringe CPU-Last: höchstens 2 % eines Kerns (der Test verlangt ≥ 50× Echtzeit) | soak-Test: gemessen 0,25–0,3 % bei 48 kHz Stereo (≈ 350–400× Echtzeit); Blockmessung in 9.1: im Mittel 0,29–0,38 % der Blockzeit, schlechtester Einzelaufruf 7,4 % |
| N13 | Plugin und C#-Referenz rechnen identisch | DSP-Test lädt die gebaute DLL aus C# und vergleicht 4 Szenarien über 6 s Filmton (größte Abweichung ≈ 3·10⁻³⁸); eine Gegenprobe mit absichtlich falscher Referenz schlägt an (Abweichung 0,32) |
| N14 | Tests stören eine installierte Kopie nicht | `SYSTEMCOMPRESSOR_DATA_DIR` (6.5); geprüft: echte `settings.json` unverändert (gleicher Zeitstempel) |

**Testbestand**
- `tests/CompressorDspSmokeTest` (`dotnet run --project tests/CompressorDspSmokeTest -c Release`): Kompression, Gain-Regler (+5 dB = ×1,78), Subbass wird nicht komprimiert, Sprach-EQ (+6 dB = ×2), Neutralität, Latenz, Clipping, THD, Knee (hart ×1,00 / 12 dB ×0,89), NaN-Robustheit, Bypass, **Vergleich Plugin = Referenz** mit Gegenprobe (lädt die gebaute DLL; übersprungen, falls sie fehlt).
- `tests/PluginHostTest` (lädt die DLL wie ein Host, 20 Prüfungen, läuft isoliert in `%TEMP%\SystemCompressorTest`; Bauen und Starten siehe Kopfkommentar in `host.cpp`; `--soak <Sekunden>` verlängert den Dauertest): Parameteranzahl, Standardverhalten, Blockgrößen, Live-Reload, Bypass, kaputte Datei, Wertebegrenzung, Stille, Dauerschreiben, Knee, NaN, ungültige Aufrufe, Suspend/Resume (inkl. doppeltem `effOpen`), Dauertest (soak), Meterdatei.
- Manuell: Systemton über `System.Media.SoundPlayer` abspielen und `meter-*.txt` bzw. Fenster prüfen (bisher auf Windows 10 durchgeführt: Ein/Aus, Gain, Schwelle/Ratio, X-Verhalten, Meter).

### 9.1 Messwerte: Latenz, Rechenaufwand und Windows-Grundlatenz (Messung vom 25.10.2026)

**Messumgebung:** Intel Core i5-7400 (3,0 GHz, 4 Kerne), Windows 10 Build 19045, Abtastrate 48 kHz, Plugin-Datei = installierte Version (SHA-256 `6A34066BDD2F7F64442018EBB1BDAA4AA525C29EF5F20DB45D82863E14011E5D`, identisch mit dem finalen Build). Das Plugin wurde von einem kleinen Messprogramm wie von einem Host geladen (isoliert über `SYSTEMCOMPRESSOR_DATA_DIR`); am Programm wurde nichts geändert. Die Messprogramme sind nach der Messung gelöscht worden (sie liegen nicht im Projekt).

**1. Latenz des Kompressors (gemessen)**

| Größe | Ergebnis |
|---|---|
| `initialDelay`, vom Plugin an den Host gemeldet | **0 Samples** |
| Erster Ausgangswert nach einem Einzelimpuls (Stereo, Amplitude 0,5) | **Sample 0 = 0,000 ms**, Maximum ebenfalls im selben Sample |
| Geprüfte Konfigurationen (11) | Standardwerte; Attack 0,1 und 200 ms; Release 10 und 2000 ms; Knee 0 und 24 dB; EQ Boost 0 und 8 dB; Enabled = aus; alle Extremwerte zugleich (Threshold −60, Ratio 20, Attack 200, Release 2000, Gain +5, EQ 8 dB bei 1 kHz, Knee 24) |
| Impuls an Position 0, 1, 479, 480, 1000, 3000; Blockgrößen 64, 480, 1024 | in allen 11 × 6 × 3 Fällen erscheint der Impuls **exakt im selben Sample**; **0 von 11 Konfigurationen weichen ab** |
| Gruppenlaufzeit des Sprach-EQ (aus der gemessenen Impulsantwort berechnet; sie ist der einzige Teil des Signalwegs mit Gedächtnis) | Standard +2 dB bei 3 kHz: **1,1 Samples = 0,023 ms**; Extremfall +8 dB bei 1 kHz: **13,4 Samples = 0,28 ms**; +8 dB bei 3 kHz: 0,095 ms; +8 dB bei 8 kHz: 0,042 ms; EQ Boost 0 dB: 0 |

Einordnung: Das Plugin hat **keinen Puffer und kein Look-ahead** (Latenz im Sinne einer Verzögerung = 0). Die Gruppenlaufzeit des EQ ist die frequenzabhängige Phasenverzögerung eines Minimalphasen-Filters in der Nähe der angehobenen Frequenz, keine Verzögerungsleitung; sie beträgt im Standard 0,02 ms und höchstens 0,28 ms.

**2. Rechenzeit pro `processReplacing`-Aufruf (gemessen)**

Messung mit `QueryPerformanceCounter` (Aufruf-Overhead 16 ns, Auflösung 100 ns), Film-ähnliches Stereosignal (leise Sprache, lauter Dialog, Explosionen, Subbass, Stille, Klicks), je Lauf 62,5 s Signal (2,9–3,0 Mio. Frames), 2 s Aufwärmen, Messthread mit hoher Priorität. Das Maximum enthält auch Störungen durch das Betriebssystem.

| Blockgröße (Blockzeit) | Modus | Aufrufe | Mittel | 99. Perzentil | Maximum | Mittel / p99 / Max in % der Blockzeit | Überschreitungen der Blockzeit |
|---|---|---|---|---|---|---|---|
| 64 (1 333 µs) | an, Standard | 46 875 | 3,95 µs | 5,0 µs | 99,0 µs | 0,30 % / 0,38 % / 7,4 % | 0 |
| 64 | aus | 46 875 | 0,54 µs | 0,6 µs | 12,1 µs | 0,04 % / 0,05 % / 0,9 % | 0 |
| 64 | an, Extremwerte | 46 875 | 4,64 µs | 6,4 µs | 35,2 µs | 0,35 % / 0,48 % / 2,6 % | 0 |
| 128 (2 667 µs) | an, Standard | 23 437 | 7,80 µs | 21,5 µs | 108,3 µs | 0,29 % / 0,81 % / 4,1 % | 0 |
| 128 | aus | 23 437 | 1,02 µs | 1,1 µs | 22,9 µs | 0,04 % / 0,04 % / 0,9 % | 0 |
| 128 | an, Extremwerte | 23 437 | 10,14 µs | 22,3 µs | 99,1 µs | 0,38 % / 0,84 % / 3,7 % | 0 |
| **480 (10 000 µs)** | an, Standard | 6 250 | 36,91 µs | 115,5 µs | 354,3 µs | 0,37 % / 1,16 % / 3,5 % | 0 |
| 480 | aus | 6 250 | 4,73 µs | 15,0 µs | 81,5 µs | 0,05 % / 0,15 % / 0,8 % | 0 |
| 480 | an, Extremwerte | 6 250 | 36,02 µs | 50,3 µs | 291,5 µs | 0,36 % / 0,50 % / 2,9 % | 0 |
| 1024 (21 333 µs) | an, Standard | 2 929 | 61,76 µs | 101,7 µs | 141,2 µs | 0,29 % / 0,48 % / 0,7 % | 0 |
| 1024 | aus | 2 929 | 7,91 µs | 22,3 µs | 40,7 µs | 0,04 % / 0,11 % / 0,2 % | 0 |
| 1024 | an, Extremwerte | 2 929 | 74,27 µs | 115,7 µs | 170,2 µs | 0,35 % / 0,54 % / 0,8 % | 0 |

Die Zeile **480 Frames (= 10 ms)** ist die praxisrelevante: Windows ruft Audio-Effekte im Shared Mode in Perioden von 10 ms ab (siehe Punkt 3), im Live-Betrieb meldete das Plugin rund 100 Blöcke pro Sekunde. Auslastung insgesamt: im Mittel etwa **0,3–0,4 % eines Kerns** (≈ 60–80 ns je Stereo-Frame, ≈ 260–350× Echtzeit); der schlechteste gemessene Einzelaufruf belegt 7,4 % der Blockzeit, also > 13 × Reserve. Mit `Enabled = aus` sinkt der Aufwand auf ≈ 0,04 %.

**3. Verzögerung im Windows-Audiopfad (gemessen, nur gelesen)**

Das aktive Wiedergabegerät wurde über WASAPI (Shared Mode) ausgelesen: ein `IAudioClient` wurde mit dem eigenen Mischformat des Geräts initialisiert und **nicht gestartet** (es wird nichts abgespielt, nichts verändert).

| Größe | Wert |
|---|---|
| Gerät (Standardgerät für Konsole und Multimedia, d. h. auch für Videoplayer) | „Lautsprecher (ASUS Xonar DX Audio Device)“ |
| Mischformat im Shared Mode | 48 000 Hz, **2 Kanäle**, 32 bit Float (passt zum Plugin: 2 Kanäle) |
| `GetDevicePeriod` | Standard **10,000 ms**, Minimum 3,000 ms |
| Shared-Engine-Periode (`IAudioClient3`) | Standard, Minimum und Maximum jeweils **480 Frames = 10,000 ms** (feste Periode, kein Low-Latency-Modus) |
| Puffergröße des Clients nach `Initialize` | 1 056 Frames = **22,0 ms** |
| `GetStreamLatency` | **0,000 ms** (der Treiber meldet keine zusätzliche Latenz; das ist **kein** Beweis, dass die Hardware keine hat) |

**4. Zusammenfassung**

| Größe | Wert | gemessen / abgeleitet |
|---|---|---|
| Latenz des Kompressors (Puffer) | 0 Samples = 0,000 ms | gemessen |
| Zusätzliche Phasenverzögerung des Sprach-EQ | Standard 0,023 ms, höchstens 0,28 ms | gemessen (aus der Impulsantwort berechnet) |
| Rechenzeit pro 10-ms-Block (480 Frames) | Mittel 37 µs, p99 116 µs, Maximum 354 µs (0,4 % / 1,2 % / 3,5 % der Blockzeit) | gemessen |
| Auslastung | ≈ 0,3–0,4 % eines Kerns | gemessen |
| Grundlatenz von Windows für dieses Gerät | Engine-Periode 10 ms, Client-Puffer 22 ms, Stream-Latenz laut Treiber 0 ms | gemessen (so, wie Windows sie meldet) |
| Gesamtverzögerung bis zum Lautsprecher | nicht gemessen; ohne Zusatzhardware (Mikrofon/Audio-Interface am Ausgang) nicht belastbar messbar. Eine Schätzung wird hier bewusst nicht angegeben. | nicht gemessen |
| Beitrag des Kompressors zur Gesamtverzögerung | höchstens 0,28 ms, im Standard 0,02 ms; Windows-Perioden von 10 ms werden nicht verlängert, weil das Plugin im selben Aufruf innerhalb der Periode rechnet | abgeleitet aus Messung 1 und der Plugin-Bauart |

**Lippensynchronität bei Filmen:** Nach den üblichen Normwerten wird ein Versatz erst ab etwa 40–45 ms (Ton vor Bild; ITU-R BT.1359-1: ≈ 45 ms, EBU R37: +40 ms) bzw. 60–125 ms (Ton nach Bild; EBU R37: −60 ms, ITU-R BT.1359-1: ≈ 125 ms) wahrnehmbar. Der Kompressor trägt höchstens 0,28 ms bei, im Standard 0,02 ms, das ist mehr als 140-mal weniger als die Schwelle. **Der Kompressor macht einen Lippensynchronitätsfehler daher nicht hörbar.** (Die Normwerte stammen aus der Literatur und wurden hier nicht nachgemessen; die Aussage ist daher eine Ableitung aus Messwerten und Normwerten.)

**Nicht gemessen / Grenzen der Messung:** die Gesamtverzögerung bis zum Lautsprecher; die Latenz der Soundkarte (Xonar DX) und ihres Treibers über das hinaus, was Windows meldet; die Eigenlatenz von Equalizer APO und seiner übrigen Filter (in der Konfiguration liegen zusätzlich die Filter aus `peace.txt`); das Verhalten unter hoher Systemlast und im MMCSS-Thread des Audiodienstes (die Messung lief offline mit hoher Priorität); Windows 11 und andere Geräte.

## 10. Installation, Deinstallation, Paket

1. **Voraussetzung:** Equalizer APO installieren, im Setup das Wiedergabegerät ankreuzen, Windows neu starten.
2. **`package/Install.ps1`** (über `Install.cmd`, fragt selbst nach Administratorrechten; `-DryRun` zeigt nur an):
   erkennt Windows 10/11, prüft Equalizer APO (Registry `HKLM\SOFTWARE\EqualizerAPO\InstallPath`), kopiert die App nach `C:\Program Files\SystemCompressor`, legt `C:\ProgramData\SystemCompressor` mit ACL an, hält den Windows-Audiodienst kurz an (≈ 2 s Tonpause; vorher laufende abhängige Dienste werden danach wieder gestartet), kopiert das Plugin nach `...\EqualizerAPO\VSTPlugins`, ergänzt den Konfigurationsblock, legt Verknüpfungen (Desktop für alle Benutzer, Startmenü) an, prüft per Registry (`MMDevices ... FxProperties`, APO-CLSID `{EC1CC9CE-FAED-4822-828A-82A81A6F018F}`), ob Equalizer APO an den aktiven Wiedergabegeräten hängt, und öffnet sonst `DeviceSelector.exe`.
3. **`package/Uninstall.ps1`:** entfernt Konfigurationsblock, Plugin, App, `ProgramData`-Ordner und Verknüpfungen; Equalizer APO bleibt.
4. **`build-package.ps1`:** baut Plugin (CMake, VS 2022 Build Tools), veröffentlicht die App (`dotnet publish -r win-x64 --self-contained -p:PublishSingleFile=true ...`), stellt `dist\SystemCompressor-win10-win11\` zusammen und erzeugt das ZIP (≈ 63 MB; `app\SystemCompressor.exe` ≈ 72 MB, `plugin\SystemCompressor.dll` ≈ 220 KB).
5. **Entwickler-Installation** auf dem Bau-Rechner: `install.ps1` im Projektordner (nimmt das Plugin aus `build\vst2\Release`); es ruft denselben Kern `package/Apply.ps1` auf wie der Setup, die Installationslogik existiert nur einmal.
6. **Gemeinsamer Kern `package/Apply.ps1`** (von `Install.ps1` und vom Setup benutzt; Parameter `-PluginDll`, `-ResultFile`, `-DryRun`): legt `C:\ProgramData\SystemCompressor` mit ACL an, installiert das Plugin (Audiodienst kurz anhalten, danach Dienst und vorher laufende abhängige Dienste starten), schreibt den Konfigurationsblock, prüft die aktiven Wiedergabegeräte. Exit-Codes: **0** fertig, **10** Equalizer APO fehlt, **11** fertig, aber Equalizer APO ist für mindestens ein aktives Gerät noch nicht aktiviert (`DEVICES_MISSING=...` in der Ergebnisdatei), **1** Fehler (`ERROR=...`). `package/Remove.ps1` ist das Gegenstück (Block, Plugin, Datenordner entfernen).
7. **Setup-Assistent `dist/SystemCompressor-Setup.exe`** (Version 1.0.0, Inno Setup 6, ca. 76 MB, Administratorrechte, nur 64 Bit, ab Windows 10 1809, englische Texte):
   - Seiten: Hinweistext (`notice.txt`), Aufgabenauswahl, Installation. Aufgabe *„Install Equalizer APO 1.4.2“* erscheint nur, wenn Equalizer APO fehlt (Registry `HKLM64\SOFTWARE\EqualizerAPO\InstallPath` + `EqualizerAPO.dll`); Aufgabe *„Create a desktop shortcut“* ist vorausgewählt.
   - Ablauf: laufende App beenden → Dateien nach `C:\Program Files\SystemCompressor` (App, `plugin\SystemCompressor.dll`, `Apply.ps1`, `Remove.ps1`, `ANFORDERUNGEN.md`) → ggf. gebündelten Equalizer-APO-Installer still starten (`/S`) → `Apply.ps1` ausführen → bei Exit-Code 11: Hinweis, `DeviceSelector.exe` öffnen, **Neustart anbieten**; bei 10 oder 1: Fehlermeldung → Option „Open System Compressor“ am Ende.
   - Verknüpfungen: Startmenü und (optional) Desktop für alle Benutzer.
   - Deinstallation (Apps & Features): `Remove.ps1` (Block, Plugin, Datenordner), danach Löschen von `{app}`. Equalizer APO und fremde Einstellungen bleiben.
   - Auch ohne Assistent nutzbar: Standard-Schalter von Inno (`/VERYSILENT /SUPPRESSMSGBOXES /NORESTART`).
   - **Nachweis:** Ein vollständiger Durchlauf *installieren → prüfen → deinstallieren → prüfen → neu installieren* lief auf Windows 10 (Build 19045) ohne Fehler (App, Plugin identisch mit Build, Konfigurationsblock, Deinstallationseintrag, Verknüpfung, Audiodienst läuft; nach der Deinstallation sind App, Plugin, Block und Datenordner weg, Equalizer APO und `Include: peace.txt` unberührt).

## 11. Bekannte Grenzen und Risiken

- **Nur zwei Kanäle:** Das Plugin deklariert 2 Ein-/Ausgänge. Bei 5.1/7.1-Ausgabe oder Mono-Geräten ist das Verhalten von Equalizer APO ungetestet. Gemessen (9.1): Das aktive Gerät des Entwicklungsrechners (Xonar DX, „Lautsprecher“) arbeitet im Shared Mode mit 48 kHz, 2 Kanälen, 32 bit Float.
- **Exklusiv-Modus / ASIO / DRM:** werden nicht erfasst.
- **Windows 11:** Aufbau und Skripte berücksichtigen Windows 11 (Versionserkennung, Eckenrundung), die Funktion wurde bisher **nur auf Windows 10 (Build 19045) praktisch geprüft**.
- **Unsignierte Programme:** App, Plugin, Skripte, Setup-Assistent und der mitgelieferte Equalizer-APO-Installer sind nicht signiert. Windows SmartScreen („Smart App Control“ bei neuen Windows-11-Installationen) kann sie blockieren; dann „Weitere Informationen → Trotzdem ausführen“ bzw. Smart App Control ausschalten, oder die Dateien signieren.
- **Skalierung:** DPI-unaware – bei 125 %/150 % leicht unscharfe Schrift.
- **Verwaiste Dateien:** Stürzt der Audiodienst ab, bleibt kurz eine `meter-*.txt` liegen (wird nach 60 s von der App entfernt).
- **Mehrere Benutzer:** `settings.json` ist rechnerweit; Änderungen eines Benutzers gelten für alle.
- **Equalizer-APO-Updates** können `config.txt` neu schreiben; dann `Install.ps1` erneut ausführen.

## 12. Bauen aus den Quellen (Neuaufbau auf einem anderen Rechner)

Werkzeuge: .NET 8 SDK, Visual Studio 2022 Build Tools mit „Desktop development with C++“, CMake ≥ 3.20, Equalizer APO.

```powershell
dotnet build .\src\CompressorTray\CompressorTray.csproj -c Release
cmake -S .\native\SystemCompressorVst2 -B .\build\vst2 -G "Visual Studio 17 2022" -A x64
cmake --build .\build\vst2 --config Release
dotnet run --project .\tests\CompressorDspSmokeTest -c Release
powershell -ExecutionPolicy Bypass -File .\build-package.ps1
powershell -ExecutionPolicy Bypass -File .\build-setup.ps1 -Rebuild   # braucht Inno Setup 6; baut das Paket mit
```

Projektstruktur:

```
src/CompressorDsp/        CompressorSettings.cs, StereoCompressor.cs   (Kern, Standardwerte, Grenzen)
src/CompressorTray/       Program.cs, MainForm.cs, KnobControl.cs, MeterControl.cs, GoldButton.cs, Theme.cs,
                          PluginStatus.cs, CompressorConfiguration.cs, ErrorLog.cs, app.ico, app.manifest, *.csproj
                          (die App kompiliert nur CompressorSettings.cs aus src/CompressorDsp; StereoCompressor.cs ist die Referenz für die Tests)
native/SystemCompressorVst2/  SystemCompressor.cpp, CMakeLists.txt     (statische C++-Laufzeit)
tests/CompressorDspSmokeTest/ Program.cs, PluginCrossCheck.cs
tests/PluginHostTest/     host.cpp, CMakeLists.txt   (lädt das Plugin wie ein Host)
package/                  Apply.ps1, Remove.ps1, Install.ps1/.cmd, Uninstall.ps1/.cmd
installer/                SystemCompressor.iss, notice.txt, redist/EqualizerAPO-x64.exe + .sha256
install.ps1, build-package.ps1, build-setup.ps1, NuGet.Config (leer = offline), README.md, ANFORDERUNGEN.md
dist/                     SystemCompressor-Setup.exe, SystemCompressor-win10-win11.zip   (Ergebnisse, nicht Quellcode)
(bin/, obj/, build/ sind Build-Ausgaben und jederzeit löschbar; `dist/SystemCompressor-win10-win11/` ist ein Zwischenstand von build-package.ps1)
```

Hinweise:
- `NuGet.Config` leert die Paketquellen absichtlich; nur `build-package.ps1` greift beim ersten Lauf auf `nuget.org` zu, um die .NET-Laufzeit zu laden.
- Der Ordner `third_party/` (CMake, VST3-SDK) wird **nicht** mehr benötigt und wurde beim Aufräumen entfernt; das frühere VST3-Konzept ist verworfen (siehe 3.2). Es wird das installierte CMake benutzt.
- `installer/redist/EqualizerAPO-x64.exe` ist der **unveränderte, unsignierte** Installer von Equalizer APO 1.4.2 (SourceForge, ca. 12,0 MB, NSIS). SourceForge nennt keine Prüfsumme; `build-setup.ps1` hält die SHA-256 beim ersten Lauf in `EqualizerAPO-x64.exe.sha256` fest (`7403be7427bbe1936a40dded082829b6e217fc4f5990fee5cba501f0ae055afa`) und bricht ab, wenn sich die Datei später ändert. Fehlt die Datei, lädt `build-setup.ps1` sie von `https://sourceforge.net/projects/equalizerapo/files/1.4.2/EqualizerAPO-x64-1.4.2.exe/download`.
- Inno Setup 6 wird nur zum Bauen des Assistenten gebraucht (`winget install JRSoftware.InnoSetup`, Benutzerinstallation).

## 13. Änderungen und Entscheidungen im Projektverlauf (Kurzfassung)

1. VST3-Plan verworfen, da Equalizer APO nur VST2 lädt → eigenes VST2-kompatibles Plugin.
2. Einstellungsort von `%LocalAppData%` nach `C:\ProgramData` verlegt (Audiodienst läuft als *LOKALER DIENST*) – das war die Ursache, warum Regler und Ein/Aus anfangs keine Wirkung hatten.
3. Film-Optimierung: Hochpass-Detektor, Soft Knee, Sprach-EQ, Softclip, Gain-Obergrenze +5 dB.
4. Taskleisten-Tray durch Fenster mit Taskleisten-Button ersetzt; Schließen schaltet aus.
5. Oberfläche komplett englisch; Standardwerte nach Benutzerwunsch: Threshold −20, Attack 0,1 ms (Minimum), Knee 5, Gain 0.
6. Statisch gelinkte C++-Laufzeit, self-contained App, Installer mit Windows-10/11-Erkennung.
7. Setup-Assistent (Inno Setup) mit optionaler Equalizer-APO-Installation, gemeinsamer Kern `Apply.ps1`/`Remove.ps1`; Installationszyklus getestet.
8. Projektordner aufgeräumt: `third_party/`, Build-Ausgaben und Zwischenstände entfernt (von 645 MB auf rund 150 MB), Neuaufbau mit `build-setup.ps1 -Rebuild` nachgewiesen.

9. Letzte Härtungs- und Entschlackungsrunde: Plugin-Entry und Thread gekapselt, Puffer- und Zahlenprüfungen, Vergleichstest Plugin = Referenz, Dauertest, Test-Isolation; App: entprelltes Speichern, Fehlerprotokoll, Schriftarten-Cache, nur noch `CompressorSettings` statt des Referenz-DSP, eine einzige Installationslogik.

## 14. Offene Punkte / mögliche Erweiterungen

- Praxistest auf Windows 11 und auf Geräten mit mehr als zwei Kanälen (ggf. Plugin mit variabler Kanalzahl).
- Look-ahead-Limiter als zuschaltbare Option (würde Latenz erzeugen).
- Echte High-DPI-Darstellung (Skalierungsfaktor in allen Zeichenroutinen).
- Signierte Installer-Datei (MSI/MSIX oder Inno Setup) statt Skripten.
- Voreinstellungen (Presets) für Film, Musik, Nachtmodus.

## 15. Abnahmeprotokoll (Stand 24.10.2026, Windows 10 Build 19045)

Alle Anforderungen aus dem Projektverlauf, einzeln gegen Code und Verhalten geprüft. „Nachweis“ nennt, womit es belegt ist.

| Nr. | Anforderung | Umsetzung | Nachweis | Stand |
|---|---|---|---|---|
| A1 | Kompressor für Windows 10 mit Gain-Regler | Plugin im Audiopfad (Equalizer APO) + Fenster mit Regler „Gain“ | Plugin-Test, Messung im Audiodienst (Gain +5 dB = Ausgang genau 5,0 dB lauter) | erfüllt |
| A2 | Optimiert für Filme / Sprachverständlichkeit | Film-Standardwerte, Hochpass-Detektor, Soft Knee, Sprach-EQ | DSP-Test (Subbass wird nicht komprimiert, EQ ×2 bei +6 dB) | erfüllt (Hörtest liegt beim Benutzer) |
| A3 | Sprach-EQ als EQ-Boost | Regler „EQ Boost“ (0–8 dB) und „Frequency“ (1–8 kHz) | DSP-Test | erfüllt |
| A4 | Immer verfügbar nach Neustart, ohne zusätzliche Programme | Plugin läuft im Windows-Audiodienst; Einstellungen in `ProgramData`; kein App-Zwang | Bauart, Messung im Audiodienst; Neustart selbst nicht gemessen | erfüllt (Neustart nicht gemessen) |
| A5 | Klang klar und neutral, gute Qualität | Neutral bit-genau unter Schwelle, THD 0,002 %, 32-bit-Float, Softclip | DSP-Test | erfüllt |
| A6 | Keine / nicht auffällige Latenz | Kein Look-ahead, 0 Samples | DSP-Test und Messung in 9.1 (Impuls im selben Sample, EQ-Gruppenlaufzeit ≤ 0,28 ms) | erfüllt |
| A7 | Gain höchstens +5 dB, Standard 0 dB | Grenze in App-Kern, Plugin und Oberfläche | Abgleichskript C#↔C++, Plugin-Test (Wert 500 wird begrenzt) | erfüllt |
| A8 | Mehrfaches Öffnen darf nicht mehrfach starten | Einzelinstanz per Mutex, zweiter Start holt das Fenster nach vorn | Test: 3 Zusatzstarts → 1 Prozess, 1 Fenster | erfüllt |
| A9 | Programm in der Taskleiste, Öffnen mit einem Klick | Normales Fenster mit Taskleisten-Button; Verknüpfung anheftbar; angeheftetes Symbol zeigt auf die installierte App | Fenstertitel/Prozess geprüft; Anheften macht der Benutzer (Windows erlaubt kein automatisches Anheften) | erfüllt (Klick selbst nicht simuliert) |
| A10 | Meter für Eingang, Ausgang und Gain Reduction (wie Stock-Plugins) | Segmentanzeigen IN/OUT/GR mit Peak-Hold und Zahlen | Screenshot der laufenden App mit Ton; Pegelwerte entsprechen der Größenordnung der vom Plugin gemeldeten Werte (`meter-*.txt`) | erfüllt |
| A11 | Fenster schließen (X) schaltet den Kompressor aus | `Enabled=false` beim Schließen (außer Windows-Herunterfahren) | Test: nach Schließen `Enabled=false`, Plugin meldet `enabled=0`, Ton unverändert | erfüllt |
| A12 | Dunkelgrauer Hintergrund, goldener Rahmen, stylisch | Eigener Rahmen, Goldtöne, goldene Regler, Symbol | Screenshots | erfüllt |
| A13 | Knee-Regler (weniger Pumpen), Standard 5 | Regler „Knee“ 0–24 dB, Standard 5 | DSP-Test (hart ×1,00 / 12 dB ×0,89), Plugin-Test | erfüllt |
| A14 | Attack-Standard auf dem niedrigsten Wert | `AttackMs` = 0,1 ms | Abgleichskript | erfüllt |
| A15 | Threshold-Standard −20 dB | `ThresholdDb` = −20 | Abgleichskript | erfüllt |
| A16 | Beschriftung „Ratio“, alle Begriffe englisch | Gesamte Oberfläche, Tooltips, Statuszeilen und Installer-Texte englisch; Zahlen mit Punkt | Screenshot, Quelltext-Durchsicht | erfüllt |
| A17 | Version für Windows 11 | Ein Programm für Windows 10 und 11: Versionserkennung, Manifest, Eckenrundung, DPI-Verhalten | Nur auf Windows 10 geprüft | **auf Windows 11 ungetestet** |
| A18 | Erneute Fehlersuche | Acht Schwachstellen behoben (NaN, EQ-Klick, ungültige Aufrufe, Resume, abhängige Dienste, verwaiste Dateien, Speicherfehler, Kommentare) | Plugin-Test (20 Prüfungen), DSP-Test | erfüllt |
| A19 | Datei mit allen Funktionen und Anforderungen | Dieses Dokument (`ANFORDERUNGEN.md`), in ZIP und Setup enthalten | – | erfüllt |
| A20 | Export auf anderen Rechner über das Netzwerk (NAS) | Eine Datei: `SystemCompressor-Setup.exe`; Prüfsumme nach jedem Bau neu berechnen | Installationszyklus (installieren → deinstallieren → neu installieren) auf diesem Rechner | erfüllt; **frischer Rechner ungetestet** |
| A21 | Setup-Assistent, der Equalizer APO bei Bedarf mitinstalliert | Inno-Setup-Installer mit Aufgabe „Install Equalizer APO 1.4.2“, Geräteauswahl, Neustart-Angebot | Installationszyklus; die Equalizer-APO-Installation selbst nicht durchlaufen, da es hier schon installiert ist | erfüllt (Mitinstallation ungetestet) |
| A22 | Projektordner aufräumen, Neuaufbau muss weiter gehen | `third_party`, Build-Ausgaben und Zwischenstände entfernt (645 MB → rund 150 MB); VST3-Reste liegen in `C:\Kompressor ChatGPT_aufgeraeumt` | `build-setup.ps1 -Rebuild` aus dem aufgeräumten Zustand: Plugin, App, ZIP, Setup entstehen; DSP- und Plugin-Test grün | erfüllt |
| A23 | Besonders stabil | Plugin kapselt alle Fehler, prüft Puffer/Parameter/Abtastrate, ignoriert NaN/Inf überall; App entprellt das Speichern, protokolliert Fehler und fängt Startfehler ab | Dauertest (soak: keine Abstürze, keine Handle-/Speicherlecks), Plugin-Host-Test (20 Prüfungen), Vergleichstest Plugin = Referenz | erfüllt |
| A24 | Entschlacken, ohne dass Latenz, Qualität oder Funktion leiden | App enthält nur noch die Einstellungsdefinition; eine Installationslogik (`Apply.ps1`); Schriftarten zwischengespeichert; minimiert = keine Arbeit. Klang unverändert | Latenz 0, bitgleich zur Referenz, THD 0,002 %, kein Clipping, CPU 0,25–0,3 % eines Kerns | erfüllt |

**Nicht belegt / bewusst offen:** Windows 11, frische Rechner ohne Vorinstallation, 5.1/7.1-Ausgabe oder Mono-Geräte, Verhalten nach einem echten Neustart, Hörqualität an echten Filmen (subjektiv).
