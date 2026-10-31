; Inno Setup script for AI Chat (Windows, x64)
; 1) Publish first (from the repository root):
;      dotnet publish AiChat.csproj -f net10.0-windows10.0.19041.0 -c Release -o publish\win-x64
; 2) Open this file in Inno Setup and press Compile (or: ISCC installer\AiChat.iss)
; Output: installer\Output\AiChat-Setup-<version>.exe

#define MyAppName      "AI Chat"
#define MyAppVersion   "1.0.0"
#define MyAppPublisher "AI Chat"
#define MyAppExeName   "AiChat.exe"
#define MyAppURL       "https://github.com/YOUR-NAME/AiChat"
; Works whether this file sits in the repo root or in the installer\ folder.
#if FileExists(AddBackslash(SourcePath) + "AiChat.csproj")
  #define Root "."
#else
  #define Root ".."
#endif
#define PublishDir     Root + "\publish\win-x64"

[Setup]
; Keep this GUID forever: it identifies the app for upgrades/uninstall. (Double the first brace.)
AppId={{6F1D2B0A-3C54-4E8B-9A71-5B2C8E4D7A19}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=Output
OutputBaseFilename=AiChat-Setup-{#MyAppVersion}
; Uncomment after you create an .ico file:
;SetupIconFile=..\Resources\AppIcon\appicon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
#if FileExists(AddBackslash(SourcePath) + Root + "\LICENSE")
LicenseFile={#Root}\LICENSE
#endif
CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
; Persian (unofficial, optional): download Farsi.isl from the Inno Setup translations page and uncomment
;Name: "farsi"; MessagesFile: "Farsi.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
