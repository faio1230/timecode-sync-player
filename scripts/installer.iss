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
; VC++ runtime minimum (v0.6.0: 14.50.35710). The value is kept in package-release.ps1.
#ifndef VcMinMajor
  #error VcMinMajor must be supplied by package-release.ps1
#endif
#ifndef VcMinMinor
  #error VcMinMinor must be supplied by package-release.ps1
#endif
#ifndef VcMinBld
  #error VcMinBld must be supplied by package-release.ps1
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
Filename: "{tmp}\vc_redist.x64.exe"; Parameters: "/install /quiet /norestart"; StatusMsg: "Installing Microsoft Visual C++ v14 Redistributable (x64) {#VcMinMajor}.{#VcMinMinor}.{#VcMinBld} or later..."; Flags: waituntilterminated shellexec; Check: VcRuntimeMissing

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
const
  VcRuntimeKey = 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64';

{ True when the x64 VC++ runtime is not installed or is older than the minimum
  (Major.Minor.Bld compared in order). Missing values count as missing runtime. }
function VcRuntimeMissing: Boolean;
var
  Installed, Major, Minor, Bld: Cardinal;
begin
  Result := True;
  if not RegQueryDWordValue(HKLM64, VcRuntimeKey, 'Installed', Installed) then
    Exit;
  if Installed <> 1 then
    Exit;
  if not RegQueryDWordValue(HKLM64, VcRuntimeKey, 'Major', Major) then
    Exit;
  if not RegQueryDWordValue(HKLM64, VcRuntimeKey, 'Minor', Minor) then
    Exit;
  if not RegQueryDWordValue(HKLM64, VcRuntimeKey, 'Bld', Bld) then
    Exit;
  if Major <> {#VcMinMajor} then
    Result := Major < {#VcMinMajor}
  else if Minor <> {#VcMinMinor} then
    Result := Minor < {#VcMinMinor}
  else
    Result := Bld < {#VcMinBld};
  Log(Format('VC++ runtime x64 installed %d.%d.%d, minimum {#VcMinMajor}.{#VcMinMinor}.{#VcMinBld}, install=%d', [Major, Minor, Bld, Ord(Result)]));
end;
