#ifndef MyAppVersion
  #error MyAppVersion must be supplied by package-release.ps1
#endif
#ifndef ReleaseDirectory
  #error ReleaseDirectory must be supplied by package-release.ps1
#endif
#ifndef ProjectRoot
  #error ProjectRoot must be supplied by package-release.ps1
#endif

#define MyAppName "TimecodeSyncPlayer"
#define MyAppPublisher "Studio Sandix"
#define MyAppExeName "TimecodeSyncPlayer.exe"

[Setup]
AppId=StudioSandix.TimecodeSyncPlayer
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} installer
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputBaseFilename=TimecodeSyncPlayer-v{#MyAppVersion}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
LicenseFile={#ProjectRoot}\LICENSE
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupLogging=yes
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"

[Files]
Source: "{#ReleaseDirectory}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion
Source: "{#ProjectRoot}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#ProjectRoot}\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#ProjectRoot}\CHANGELOG.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"

[UninstallDelete]
Type: filesandordirs; Name: "{app}"
