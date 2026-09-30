; RDock 安裝腳本（Inno Setup 6）
; 1. 先執行專案根目錄的 scripts\publish.ps1
; 2. 用 Inno Setup Compiler 開啟本檔並 Compile
; 輸出：publish\installer\RDock-Setup-1.0.4.exe

#define MyAppName "RDock"
#define MyAppVersion "1.0.4"
#define MyAppPublisher "Rank"
#define MyAppURL "https://github.com/shelan46k/RDock"
#define MyAppExeName "RDock.exe"

[Setup]
AppId={{A7C3E9F1-8B2D-4E6A-9C1F-0D5B8A4E2F73}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
OutputDir=..\publish\installer
OutputBaseFilename=RDock-Setup-{#MyAppVersion}
SetupIconFile=..\Assets\RDock.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
; 若安裝 Inno 時有勾選語言包，可再加：
; Name: "chinesetraditional"; MessagesFile: "compiler:Languages\ChineseTraditional.isl"

[Tasks]
Name: "desktopicon"; Description: "建立桌面捷徑"; GroupDescription: "額外捷徑:"; Flags: unchecked
Name: "autostart"; Description: "開機時啟動 RDock"; GroupDescription: "啟動選項:"; Flags: unchecked

[Files]
Source: "..\publish\single\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\解除安裝 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
  ValueType: string; ValueName: "RDock"; ValueData: """{app}\{#MyAppExeName}"""; \
  Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "立即啟動 RDock"; Flags: nowait postinstall skipifsilent
