#ifndef MyAppVersion
  #define MyAppVersion "0.1.0"
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

[CustomMessages]
english.DownloadMpvNow=Download mpv now (run get-mpv.ps1)
japanese.DownloadMpvNow=mpv を今ダウンロードする (get-mpv.ps1 を実行)

[Files]
Source: "{#ReleaseDirectory}\*"; DestDir: "{app}"; Excludes: "mpv-2.dll,libmpv-2.dll,*.pdb"; Flags: ignoreversion
Source: "{#ProjectRoot}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#ProjectRoot}\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#ProjectRoot}\CHANGELOG.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#ProjectRoot}\scripts\get-mpv.ps1"; DestDir: "{app}\scripts"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"

[Run]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\scripts\get-mpv.ps1"" -DestinationDirectory ""{app}"" -Force"; WorkingDir: "{app}"; Description: "{cm:DownloadMpvNow}"; Flags: postinstall skipifsilent waituntilterminated; Check: not WizardSilent
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\scripts\get-mpv.ps1"" -DestinationDirectory ""{app}"" -Force"; WorkingDir: "{app}"; Flags: waituntilterminated; Check: ShouldDownloadMpvSilently

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
function ShouldDownloadMpvSilently: Boolean;
var
  Index: Integer;
begin
  Result := False;
  if not WizardSilent then
    Exit;

  for Index := 1 to ParamCount do
  begin
    if CompareText(ParamStr(Index), '/DOWNLOADMPV') = 0 then
    begin
      Result := True;
      Exit;
    end;
  end;
end;
