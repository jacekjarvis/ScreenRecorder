; Inno Setup script for Jarvo Screen Recorder.
; Build with tools/build-installer.ps1, which publishes the app (with .NET included) and FFmpeg first.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif
#define AppName "Jarvo Screen Recorder"
#define AppExe "JarvoScreenRecorder.exe"

[Setup]
AppId={{6F3C2B7E-9A41-4D2B-B8E5-3C7A1D5E9F20}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Jacek Jarvis
AppPublisherURL=https://github.com/jacekjarvis/ScreenRecorder
AppSupportURL=https://github.com/jacekjarvis/ScreenRecorder/issues
; Per-user install: no administrator prompt; goes to %LocalAppData%\Programs.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
OutputDir=..\artifacts
OutputBaseFilename=JarvoScreenRecorder-Setup-{#AppVersion}
SetupIconFile=..\src\ScreenRecorder.App\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
LZMAUseSeparateProcess=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 10 version 2004 or later (needed for screen capture without the cursor).
MinVersion=10.0.19041
ChangesAssociations=yes
; Updating while the app is open: close it first, reopen it afterwards.
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop icon"; GroupDescription: "Shortcuts:"
Name: "fileassoc"; Description: "Open .jsrec recordings with {#AppName}"; GroupDescription: "Files:"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
Root: HKA; Subkey: "Software\Classes\.jsrec"; ValueType: string; ValueName: ""; ValueData: "JarvoScreenRecorder.Recording"; Flags: uninsdeletevalue; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\JarvoScreenRecorder.Recording"; ValueType: string; ValueName: ""; ValueData: "Jarvo Screen Recording"; Flags: uninsdeletekey; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\JarvoScreenRecorder.Recording\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"",0"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\JarvoScreenRecorder.Recording\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: fileassoc

[Run]
Filename: "{app}\{#AppExe}"; Description: "Start {#AppName} now"; Flags: nowait postinstall skipifsilent

; Recordings (Videos\ScreenRecorder) and settings are deliberately kept when uninstalling.
