; Inno Setup script for AI Chat (Windows, x64)
; 1) Publish first (from the repository root):
;      dotnet publish AiChat.csproj -f net10.0-windows10.0.19041.0 -c Release -o publish\win-x64
; 2) Open this file in Inno Setup and press Compile (or: ISCC AiChat.iss)
; Output: Output\AiChat-Setup-<version>.exe

#define MyAppName      "AI Chat"
#ifndef MyAppVersion
  #define MyAppVersion "1.1.0"
#endif
#define MyAppPublisher "AI Chat"
#define MyAppExeName   "AiChat.exe"
#define MyAppURL       "https://github.com/CodeCraftDevHQ/AiChat"
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

[Code]
{ AI Chat shows its UI in Microsoft Edge WebView2. Windows 11 and updated Windows 10 already have it;
  if it is missing, the installer downloads Microsoft's small bootstrapper and runs it before finishing. }
const
  WebView2Guid = '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  WebView2Url  = 'https://go.microsoft.com/fwlink/p/?LinkId=2124703';

var
  DownloadPage: TDownloadWizardPage;

function RuntimeVersionOk(Root: Integer; const SubKey: string): Boolean;
var
  V: string;
begin
  Result := RegQueryStringValue(Root, SubKey, 'pv', V) and (V <> '') and (V <> '0.0.0.0');
end;

function WebView2Installed: Boolean;
var
  Key: string;
begin
  Key := 'SOFTWARE\Microsoft\EdgeUpdate\Clients\' + WebView2Guid;
  Result := RuntimeVersionOk(HKLM32, Key) or RuntimeVersionOk(HKLM64, Key) or RuntimeVersionOk(HKCU, Key);
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), SetupMessage(msgPreparingDesc), nil);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;
  if (CurPageID <> wpReady) or WebView2Installed then Exit;

  DownloadPage.Clear;
  DownloadPage.Add(WebView2Url, 'MicrosoftEdgeWebview2Setup.exe', '');
  DownloadPage.Show;
  try
    try
      DownloadPage.Download;
      if not Exec(ExpandConstant('{tmp}\MicrosoftEdgeWebview2Setup.exe'), '/silent /install', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
        Log('WebView2 setup could not be started.');
    except
      // Offline or blocked: continue, the app will tell the user what is missing.
      SuppressibleMsgBox(
        'Microsoft Edge WebView2 Runtime could not be downloaded.' + #13#10 +
        'AI Chat needs it to run. Install it from:' + #13#10 +
        'https://developer.microsoft.com/microsoft-edge/webview2/',
        mbInformation, MB_OK, IDOK);
    end;
  finally
    DownloadPage.Hide;
  end;
end;
