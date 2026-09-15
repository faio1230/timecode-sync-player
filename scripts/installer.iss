#ifndef MyAppVersion
  #error MyAppVersion must be supplied by package-release.ps1
#endif
#ifndef ReleaseDirectory
  #error ReleaseDirectory must be supplied by package-release.ps1
#endif
#ifndef ProjectRoot
  #error ProjectRoot must be supplied by package-release.ps1
#endif
#ifndef VcRedistFile
  #error VcRedistFile must be supplied by package-release.ps1
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
Source: "{#ReleaseDirectory}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#ProjectRoot}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#ProjectRoot}\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#ProjectRoot}\CHANGELOG.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#VcRedistFile}"; DestDir: "{tmp}"; DestName: "vc_redist.x64.exe"; Flags: deleteafterinstall

[Icons]
Name: "{userprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"

[Run]
Filename: "{tmp}\vc_redist.x64.exe"; Parameters: "/install /quiet /norestart"; StatusMsg: "Installing Microsoft Visual C++ 2015-2022 Redistributable (x64)..."; Flags: waituntilterminated shellexec; Check: VcRuntimeMissing

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
function VcRuntimeMissing: Boolean;
var
  Installed: Cardinal;
begin
  Result := True;
  if RegQueryDWordValue(HKLM64, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64', 'Installed', Installed) then
    Result := Installed <> 1;
end;
