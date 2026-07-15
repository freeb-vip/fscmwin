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
DefaultDirName={localappdata}\Programs\FSCM Edge
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
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

; User-managed state is installed once and never replaced or removed by an upgrade.
Source: "{#SourceDir}\EdgeRuntime\edge.config.yaml"; DestDir: "{app}\EdgeRuntime"; Flags: onlyifdoesntexist uninsneveruninstall
Source: "{#SourceDir}\EdgeRuntime\print-templates.json"; DestDir: "{app}\EdgeRuntime"; Flags: onlyifdoesntexist uninsneveruninstall

[Dirs]
Name: "{app}\EdgeRuntime\data"; Flags: uninsneveruninstall
Name: "{app}\EdgeRuntime\logs"; Flags: uninsneveruninstall

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent
