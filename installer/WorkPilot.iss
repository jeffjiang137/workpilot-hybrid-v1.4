#define MyAppName "WorkPilot"
#define MyAppVersion "1.4.0"
#define MyAppPublisher "WorkPilot"
#define MyAppExeName "WorkPilot.App.exe"

[Setup]
AppId={{E99D04D6-6F40-4C26-BB79-3D94C66D846C}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\WorkPilot
DefaultGroupName=WorkPilot
DisableProgramGroupPage=yes
OutputDir=..\artifacts\installer
OutputBaseFilename=WorkPilot-Hybrid-V1.4-win-x64-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
MinVersion=10.0.17763
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "..\artifacts\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\WorkPilot"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\WorkPilot"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加快捷方式："; Flags: unchecked

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "启动 WorkPilot"; Flags: nowait postinstall skipifsilent
