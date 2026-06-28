[Setup]
AppName=FlipSwitcher
AppVersion={#Version}
AppVerName=FlipSwitcher
AppPublisher=DjangoZane
AppPublisherURL=https://github.com/dianbanjiu/FlipSwitcher
AppSupportURL=https://github.com/dianbanjiu/FlipSwitcher
AppUpdatesURL=https://github.com/dianbanjiu/FlipSwitcher
DefaultDirName={autopf}\FlipSwitcher
DefaultGroupName=FlipSwitcher
AllowNoIcons=yes
LicenseFile=
OutputDir=..\publish
OutputBaseFilename=FlipSwitcher-windows-x64-Setup
Compression=lzma
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64
UninstallDisplayIcon={app}\FlipSwitcher.exe
CloseApplications=yes
CloseApplicationsFilter=FlipSwitcher.exe
RestartApplications=no
SetupIconFile=Assets\flipswitcher.ico

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "chinesetrad"; MessagesFile: "compiler:Languages\ChineseTraditional.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "quicklaunchicon"; Description: "{cm:CreateQuickLaunchIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked; OnlyBelowVersion: 6.1; Check: not IsAdminInstallMode

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\FlipSwitcher"; Filename: "{app}\FlipSwitcher.exe"
Name: "{group}\{cm:UninstallProgram,FlipSwitcher}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\FlipSwitcher"; Filename: "{app}\FlipSwitcher.exe"; Tasks: desktopicon
Name: "{userappdata}\Microsoft\Internet Explorer\Quick Launch\FlipSwitcher"; Filename: "{app}\FlipSwitcher.exe"; Tasks: quicklaunchicon

[Run]
Filename: "{app}\FlipSwitcher.exe"; Description: "{cm:LaunchProgram,FlipSwitcher}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

