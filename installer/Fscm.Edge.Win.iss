#ifndef MyAppVersion
  #define MyAppVersion "0.1.0"
#endif

#ifndef SourceDir
  #define SourceDir "..\artifacts\Fscm.Edge.Win"
#endif

#define MyAppName "FSCM Edge"
#define MyAppPublisher "FSCM"
#define MyAppExeName "Fscm.Edge.Win.exe"

[Setup]
AppId={{B858B5EF-5B16-4F7B-8B23-C4B0C84CCAA9}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\FSCM Edge
DisableDirPage=no
AllowUNCPath=no
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts
OutputBaseFilename=FSCM-Edge-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#MyAppExeName}
CloseApplications=yes
CloseApplicationsFilter=Fscm.Edge.Win.exe,fscm-edge.exe
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
; Application files are replaced on every upgrade. Runtime state is handled below.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "EdgeRuntime\*,win-x64\*"
Source: "{#SourceDir}\EdgeRuntime\fscm-edge.exe"; DestDir: "{app}\EdgeRuntime"; Flags: ignoreversion
Source: "{#SourceDir}\EdgeRuntime\edge-runtime-manifest.json"; DestDir: "{app}\EdgeRuntime"; Flags: ignoreversion
Source: "{#SourceDir}\EdgeRuntime\README.md"; DestDir: "{app}\EdgeRuntime"; Flags: ignoreversion
Source: "install-edge-service.ps1"; Flags: dontcopy

; User-managed state is installed once under ProgramData and never replaced or removed by an upgrade.
Source: "{#SourceDir}\EdgeRuntime\edge.config.yaml"; DestDir: "{commonappdata}\FSCM Edge\EdgeRuntime"; Flags: onlyifdoesntexist uninsneveruninstall
Source: "{#SourceDir}\EdgeRuntime\print-templates.json"; DestDir: "{commonappdata}\FSCM Edge\EdgeRuntime"; Flags: onlyifdoesntexist uninsneveruninstall

[Dirs]
Name: "{commonappdata}\FSCM Edge\EdgeRuntime"; Permissions: users-modify; Flags: uninsneveruninstall
Name: "{commonappdata}\FSCM Edge\EdgeRuntime\data"; Permissions: users-modify; Flags: uninsneveruninstall
Name: "{commonappdata}\FSCM Edge\EdgeRuntime\logs"; Permissions: users-modify; Flags: uninsneveruninstall

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\EdgeRuntime\fscm-edge.exe"; Parameters: "--service-control=uninstall --config=""{commonappdata}\FSCM Edge\EdgeRuntime\edge.config.yaml"""; Flags: runhidden waituntilterminated; RunOnceId: "RemoveFscmEdgeService"

[Code]
procedure CopyRuntimeFile(const SourcePath, DestinationPath: String);
begin
  if FileExists(SourcePath) and not FileExists(DestinationPath) then
  begin
    ForceDirectories(ExtractFileDir(DestinationPath));
    CopyFile(SourcePath, DestinationPath, False);
  end;
end;

procedure CopyRuntimeTree(const SourceDir, DestinationDir: String);
var
  FindRec: TFindRec;
  SourcePath, DestinationPath: String;
begin
  if not DirExists(SourceDir) then
    Exit;
  ForceDirectories(DestinationDir);
  if FindFirst(AddBackslash(SourceDir) + '*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Name <> '.') and (FindRec.Name <> '..') then
        begin
          SourcePath := AddBackslash(SourceDir) + FindRec.Name;
          DestinationPath := AddBackslash(DestinationDir) + FindRec.Name;
          if FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0 then
            CopyRuntimeTree(SourcePath, DestinationPath)
          else if not FileExists(DestinationPath) then
            CopyFile(SourcePath, DestinationPath, False);
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

function DirectoryIsEmpty(const Directory: String): Boolean;
var
  FindRec: TFindRec;
begin
  Result := True;
  if not DirExists(Directory) then
    Exit;
  if FindFirst(AddBackslash(Directory) + '*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Name <> '.') and (FindRec.Name <> '..') then
        begin
          Result := False;
          Exit;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  SelectedDir: String;
begin
  Result := True;
  if CurPageID <> wpSelectDir then
    Exit;

  SelectedDir := WizardDirValue;
  if CompareText(AddBackslash(SelectedDir), AddBackslash(ExtractFileDrive(SelectedDir))) = 0 then
  begin
    MsgBox('Please select a dedicated application directory, not a drive root.', mbError, MB_OK);
    Result := False;
    Exit;
  end;

  if DirExists(SelectedDir) and
     not DirectoryIsEmpty(SelectedDir) and
     not FileExists(AddBackslash(SelectedDir) + '{#MyAppExeName}') then
  begin
    MsgBox(
      'The selected directory is not empty and is not an existing FSCM Edge installation. ' +
      'Please select a new or empty directory.',
      mbError, MB_OK);
    Result := False;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  LegacyRuntime, SharedRuntime: String;
begin
  if CurStep = ssInstall then
  begin
    Exec(
      ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      '-NoLogo -NoProfile -NonInteractive -Command "Stop-Service -Name ''FscmEdge'' -Force -ErrorAction SilentlyContinue"',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    LegacyRuntime := ExpandConstant('{app}\EdgeRuntime');
    SharedRuntime := ExpandConstant('{commonappdata}\FSCM Edge\EdgeRuntime');
    CopyRuntimeFile(AddBackslash(LegacyRuntime) + 'edge.config.yaml', AddBackslash(SharedRuntime) + 'edge.config.yaml');
    CopyRuntimeFile(AddBackslash(LegacyRuntime) + 'print-templates.json', AddBackslash(SharedRuntime) + 'print-templates.json');
    CopyRuntimeTree(AddBackslash(LegacyRuntime) + 'data', AddBackslash(SharedRuntime) + 'data');
    CopyRuntimeTree(AddBackslash(LegacyRuntime) + 'logs', AddBackslash(SharedRuntime) + 'logs');
    LegacyRuntime := ExpandConstant('{localappdata}\Programs\FSCM Edge\EdgeRuntime');
    CopyRuntimeFile(AddBackslash(LegacyRuntime) + 'edge.config.yaml', AddBackslash(SharedRuntime) + 'edge.config.yaml');
    CopyRuntimeFile(AddBackslash(LegacyRuntime) + 'print-templates.json', AddBackslash(SharedRuntime) + 'print-templates.json');
    CopyRuntimeTree(AddBackslash(LegacyRuntime) + 'data', AddBackslash(SharedRuntime) + 'data');
    CopyRuntimeTree(AddBackslash(LegacyRuntime) + 'logs', AddBackslash(SharedRuntime) + 'logs');
  end;
  if CurStep = ssPostInstall then
  begin
    if not FileExists(ExpandConstant('{app}\EdgeRuntime\fscm-edge.exe')) then
      RaiseException('FSCM Edge service executable was not installed.');

    ExtractTemporaryFile('install-edge-service.ps1');
    if not Exec(
      ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' +
        ExpandConstant('{tmp}\install-edge-service.ps1') + '" -ServiceExecutable "' +
        ExpandConstant('{app}\EdgeRuntime\fscm-edge.exe') + '" -ConfigPath "' +
        ExpandConstant('{commonappdata}\FSCM Edge\EdgeRuntime\edge.config.yaml') + '" -LogPath "' +
        ExpandConstant('{commonappdata}\FSCM Edge\EdgeRuntime\logs\service-install.log') + '"',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
      RaiseException('Could not start FSCM Edge service installation.');
    if ResultCode <> 0 then
      RaiseException(
        'FSCM Edge service installation failed. See ' +
        ExpandConstant('{commonappdata}\FSCM Edge\EdgeRuntime\logs\service-install.log'));
  end;
end;
