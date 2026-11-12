; OpenATLAS Windows installer (Inno Setup 6)
; Build it with installer\build-installer.ps1, which publishes the app first and passes in
; AppVersion and PublishDir. Compiling this file on its own uses the defaults below.
;
; What gets installed:
;   {app}\                 the self-contained app (no .NET install needed)
;   {app}\references\      the F1 reference laps and track.json files (My Laps is never packaged)
; The user's own laps are saved to Documents\OpenATLAS\My Laps by the app, so they survive
; updates and uninstalling.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\publish\OpenATLAS"
#endif

#define AppName "OpenATLAS"
#define AppExe "OpenATLAS.exe"

[Setup]
; Keep AppId the same for every release: it is how Windows recognises an update of the same app
AppId={{35CD2566-A792-4CD9-B1FE-492F54B9F876}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=OpenATLAS
AppComments=Trackside telemetry and delta analysis workbench
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} Setup

DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; Installs for all users (needs admin); the dialog lets users without admin rights install just for themselves
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog

; 64-bit Windows 10 or later (the app is built for win-x64)
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0

LicenseFile=..\LICENSE
SetupIconFile=..\Workbench\Assets\OpenATLAS.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern

OutputDir=Output
OutputBaseFilename=OpenATLAS-Setup-{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
LZMANumBlockThreads=4

; Close a running OpenATLAS before updating its files
CloseApplications=yes

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[InstallDelete]
; An update replaces the bundled F1 data completely, so laps removed from a release do not linger
Type: filesandordirs; Name: "{app}\references"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\references\*"; DestDir: "{app}\references"; Excludes: "\My Laps"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
