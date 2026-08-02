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
Source: "{#SourceDir}\EdgeRuntime\fscm-edge.exe"; DestDir: "{commonappdata}\FSCM Edge\Service"; Flags: ignoreversion
Source: "{#SourceDir}\EdgeRuntime\edge-runtime-manifest.json"; DestDir: "{app}\EdgeRuntime"; Flags: ignoreversion
Source: "{#SourceDir}\EdgeRuntime\README.md"; DestDir: "{app}\EdgeRuntime"; Flags: ignoreversion

; User-managed state is installed once under ProgramData and never replaced or removed by an upgrade.
Source: "{#SourceDir}\EdgeRuntime\edge.config.yaml"; DestDir: "{commonappdata}\FSCM Edge\EdgeRuntime"; Flags: onlyifdoesntexist uninsneveruninstall
Source: "{#SourceDir}\EdgeRuntime\print-templates.json"; DestDir: "{commonappdata}\FSCM Edge\EdgeRuntime"; Flags: onlyifdoesntexist uninsneveruninstall

[Dirs]
Name: "{commonappdata}\FSCM Edge\Service"; Permissions: admins-full system-full users-readexec
Name: "{commonappdata}\FSCM Edge\EdgeRuntime"; Permissions: users-modify; Flags: uninsneveruninstall
Name: "{commonappdata}\FSCM Edge\EdgeRuntime\data"; Permissions: users-modify; Flags: uninsneveruninstall
Name: "{commonappdata}\FSCM Edge\EdgeRuntime\logs"; Permissions: users-modify; Flags: uninsneveruninstall

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{commonappdata}\FSCM Edge\Service\fscm-edge.exe"; Parameters: "--service-control=install --config=""{commonappdata}\FSCM Edge\EdgeRuntime\edge.config.yaml"""; Description: "Install FSCM Edge background service"; Flags: runhidden waituntilterminated
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{commonappdata}\FSCM Edge\Service\fscm-edge.exe"; Parameters: "--service-control=uninstall --config=""{commonappdata}\FSCM Edge\EdgeRuntime\edge.config.yaml"""; Flags: runhidden waituntilterminated; RunOnceId: "RemoveFscmEdgeService"

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
end;
