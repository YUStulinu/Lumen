; Inno Setup script for Lumen.
; Build it with scripts\publish.ps1, which publishes the app first and passes the version and paths:
;   ISCC.exe /DMyAppVersion=1.0.0 /DSourceDir=..\artifacts\publish /DOutputDir=..\artifacts installer\Lumen.iss
; Inno Setup: https://jrsoftware.org/isinfo.php  (winget install JRSoftware.InnoSetup)

#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts"
#endif

#define MyAppName "Lumen"
#define MyAppPublisher "Iustin Prodan"
#define MyAppExeName "Lumen.exe"
#define MyAppUrl "https://github.com/YOUR-USERNAME/Lumen"

[Setup]
; A fixed AppId lets future versions upgrade this installation in place. Never change it.
AppId={{6C1E9F4B-2D7A-4B8E-9E43-7F3A5D0C1B22}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppUrl}
AppSupportURL={#MyAppUrl}/issues
; Per-user install into %LOCALAPPDATA%\Programs\Lumen: no administrator rights, no UAC prompt.
PrivilegesRequired=lowest
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=Lumen-{#MyAppVersion}-win-x64-setup
SetupIconFile=..\src\Lumen.App\Assets\lumen.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
; The running app holds this mutex (see SingleInstanceGuard.cs); setup asks the user to close it.
AppMutex=Local\Lumen.SingleInstance.7F3A
CloseApplications=yes
LicenseFile=..\LICENSE

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startup"; Description: "Start Lumen when I sign in to Windows"; GroupDescription: "Startup:"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; Same value the app writes from Settings → "Start Lumen when I sign in" (AutostartService.cs).
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Lumen"; \
  ValueData: """{app}\{#MyAppExeName}"" --background"; Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[Code]
// The startup entry may also have been created from inside the app; remove it on uninstall either way.
// Settings (%APPDATA%\Lumen) and downloaded models (%LOCALAPPDATA%\Lumen) are kept on purpose.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Lumen');
end;
