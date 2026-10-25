; System Compressor - Setup for Windows 10 (1809+) and Windows 11, 64 bit.
; Built by build-setup.ps1 (needs Inno Setup 6, the package from build-package.ps1 and redist\EqualizerAPO-x64.exe).

#define AppName "System Compressor"
#define AppVersion "1.0.0"
#define Dist "..\dist\SystemCompressor-win10-win11"
#define PowerShell "{sys}\WindowsPowerShell\v1.0\powershell.exe"

[Setup]
AppId={{B1D8E6A4-5C3F-4E0B-9B0E-6F2D7C1A9E55}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=System Compressor
DefaultDirName={autopf}\SystemCompressor
DisableProgramGroupPage=yes
DisableDirPage=auto
OutputDir=..\dist
OutputBaseFilename=SystemCompressor-Setup
SetupIconFile=..\src\CompressorTray\app.ico
UninstallDisplayIcon={app}\SystemCompressor.exe
UninstallDisplayName={#AppName}
InfoBeforeFile=notice.txt
Compression=lzma2/fast
SolidCompression=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
WizardStyle=modern
CloseApplications=yes
CloseApplicationsFilter=SystemCompressor.exe

[Tasks]
Name: "installeapo"; Description: "Install Equalizer APO 1.4.2 (required - it puts the compressor into the Windows audio path)"; Check: not EapoInstalled
Name: "desktopicon"; Description: "Create a desktop shortcut"

[Files]
Source: "{#Dist}\app\SystemCompressor.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Dist}\plugin\SystemCompressor.dll"; DestDir: "{app}\plugin"; Flags: ignoreversion
Source: "..\package\Apply.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\package\Remove.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\ANFORDERUNGEN.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "redist\EqualizerAPO-x64.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall; Tasks: installeapo

[Icons]
Name: "{autoprograms}\System Compressor"; Filename: "{app}\SystemCompressor.exe"
Name: "{autodesktop}\System Compressor"; Filename: "{app}\SystemCompressor.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\SystemCompressor.exe"; Description: "Open System Compressor"; Flags: postinstall nowait skipifsilent

[UninstallRun]
Filename: "{#PowerShell}"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Remove.ps1"""; Flags: runhidden waituntilterminated; RunOnceId: "RemovePlugin"

[UninstallDelete]
Type: filesandordirs; Name: "{app}"
Type: filesandordirs; Name: "{commonappdata}\SystemCompressor"

[Code]
var
  RestartNeeded: Boolean;

function EapoDir: String;
begin
  if not RegQueryStringValue(HKLM64, 'SOFTWARE\EqualizerAPO', 'InstallPath', Result) then
    Result := ExpandConstant('{commonpf64}\EqualizerAPO');
  if not FileExists(AddBackslash(Result) + 'EqualizerAPO.dll') then
    Result := '';
end;

function EapoInstalled: Boolean;
begin
  Result := EapoDir <> '';
end;

function NeedRestart: Boolean;
begin
  Result := RestartNeeded;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM SystemCompressor.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
end;

function ReadResult(const Key: String): String;
var
  Lines: TArrayOfString;
  I: Integer;
begin
  Result := '';
  if LoadStringsFromFile(ExpandConstant('{tmp}\apply.result'), Lines) then
    for I := 0 to GetArrayLength(Lines) - 1 do
      if Pos(Key + '=', Lines[I]) = 1 then
        Result := Copy(Lines[I], Length(Key) + 2, MaxInt);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  Devices, Selector: String;
begin
  if CurStep <> ssPostInstall then Exit;

  if WizardIsTaskSelected('installeapo') then
  begin
    WizardForm.StatusLabel.Caption := 'Installing Equalizer APO...';
    if Exec(ExpandConstant('{tmp}\EqualizerAPO-x64.exe'), '/S', '', SW_SHOW, ewWaitUntilTerminated, ResultCode) then
      RestartNeeded := True
    else
      MsgBox('Equalizer APO could not be started. Install it manually from https://sourceforge.net/projects/equalizerapo/ and run this setup again.', mbError, MB_OK);
  end;

  WizardForm.StatusLabel.Caption := 'Installing the plug-in (Windows audio pauses for about 2 seconds)...';
  if not Exec(ExpandConstant('{#PowerShell}'),
      '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\Apply.ps1') + '" -PluginDll "' +
      ExpandConstant('{app}\plugin\SystemCompressor.dll') + '" -ResultFile "' + ExpandConstant('{tmp}\apply.result') + '"',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    ResultCode := 1;

  case ResultCode of
    0: ;
    10: MsgBox('Equalizer APO is not installed, so the compressor cannot work yet.' + #13#10#13#10 +
               'Install it from https://sourceforge.net/projects/equalizerapo/ (tick your playback device in its setup), restart Windows and run this setup again.', mbError, MB_OK);
    11:
      begin
        Devices := ReadResult('DEVICES_MISSING');
        RestartNeeded := True;
        MsgBox('One more step: Equalizer APO is not yet enabled for your playback device(s):' + #13#10 + Devices + #13#10#13#10 +
               'In the window that opens next, tick the device you listen on, close it, and restart Windows when Setup asks.', mbInformation, MB_OK);
        Selector := AddBackslash(ReadResult('APO_DIR')) + 'DeviceSelector.exe';
        if FileExists(Selector) then
          Exec(Selector, '', '', SW_SHOW, ewWaitUntilTerminated, ResultCode);
      end;
  else
    MsgBox('Installing the plug-in failed: ' + ReadResult('ERROR'), mbError, MB_OK);
  end;
end;
