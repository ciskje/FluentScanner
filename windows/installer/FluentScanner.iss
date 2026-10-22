; ============================================================
; FluentScanner — Inno Setup 6 script
;
; App version: single source is <Version> in
; source/FluentScanner.csproj — #define MyAppVersion below
; derives from it at build time (never bump for docs/refactor/build).
;
; Build: see windows/AGENTS.md (Release + installer).
; Output: installer\dist\FluentScanner-Setup-<version>-win-x64.exe
; ============================================================

#define MyAppName "FluentScanner"
#define MyAppVersion "1.2.6"
#define MyAppExeName "FluentScanner.exe"
#define PublishDir "..\publish"
#define IconFile "..\source\Assets\AppIcon.ico"

[Setup]
AppId={{8C3E7777-1D4E-450C-AC75-7EAF3CA5509F}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
DefaultGroupName={#MyAppName}
OutputDir=.\dist
OutputBaseFilename={#MyAppName}-Setup-{#MyAppVersion}-win-x64
SetupIconFile={#IconFile}
; Icon in Settings > Apps > Installed apps / Control Panel (skill windows-setup):
; without this the ARP entry shows the generic placeholder.
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
ArchitecturesAllowed=x64compatible
; 64-bit mode: without this the (32-bit) Setup resolves {autopf} to
; "C:\Program Files (x86)" — the installer would propose the x86 folder.
ArchitecturesInstallIn64BitMode=x64compatible
DefaultDirName={autopf}\{#MyAppName}
; Per-machine install under C:\Program Files: requires the UAC prompt.
PrivilegesRequired=admin

[Languages]
Name: "italian"; MessagesFile: "compiler:Languages\Italian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; Publish self-contained (includes the .NET runtime and Windows App SDK)
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; runasoriginaluser: without it, after an admin install the app would start elevated
Filename: "{app}\{#MyAppExeName}"; Description: "Launch FluentScanner"; Flags: nowait postinstall skipifsilent runasoriginaluser
