; Inno Setup 6 script for EversoloForPC.
; Build:  dotnet publish -c Release -r win-x64 --self-contained true -o publish
;         ISCC installer\EversoloForPC.iss
#define AppName "EversoloForPC"
#define AppVersion "0.2.0"
#define AppPublisher "MBB"
#define AppContact "mbb.build@gmail.com"
#define AppExe "EversoloForPC.exe"

[Setup]
AppId={{6B0B7B5C-7F0E-4C62-9C1B-0E5F2A6E1D42}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppContact={#AppContact}
AppSupportURL=mailto:{#AppContact}
AppCopyright=Copyright (c) 2026 {#AppPublisher}
VersionInfoVersion={#AppVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} setup
; Per-user install: no administrator rights, no UAC prompt.
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename={#AppName}-Setup-{#AppVersion}
SetupIconFile=..\app.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "tr"; MessagesFile: "compiler:Languages\Turkish.isl"

[CustomMessages]
en.TaskDesktop=Create a desktop shortcut
tr.TaskDesktop=Masaüstü kısayolu oluştur
en.TaskAutostart=Start with Windows
tr.TaskAutostart=Windows ile başlat
en.TaskFfmpeg=Install ffmpeg with winget (needed for "Listen on this PC")
tr.TaskFfmpeg=ffmpeg'i winget ile kur ("PC'de dinle" için gerekli)
en.RunApp=Start {#AppName}
tr.RunApp={#AppName} uygulamasını başlat

[Tasks]
Name: "desktopicon"; Description: "{cm:TaskDesktop}"
Name: "autostart"; Description: "{cm:TaskAutostart}"; Flags: unchecked
Name: "ffmpeg"; Description: "{cm:TaskFfmpeg}"; Flags: unchecked

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{#AppName} - Settings"; Filename: "{app}\{#AppExe}"; Parameters: "--settings"
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; ValueData: """{app}\{#AppExe}"""; Tasks: autostart; Flags: uninsdeletevalue

[Run]
; ffmpeg is not bundled (GPL build); winget installs the Gyan.FFmpeg package, which the app finds on its own.
Filename: "{cmd}"; Parameters: "/c winget install --id Gyan.FFmpeg -e --accept-source-agreements --accept-package-agreements"; Tasks: ffmpeg; Flags: runhidden waituntilterminated; StatusMsg: "ffmpeg..."
Filename: "{app}\{#AppExe}"; Description: "{cm:RunApp}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{cmd}"; Parameters: "/c taskkill /im {#AppExe} /f"; Flags: runhidden; RunOnceId: "KillApp"
