; Kashtrix Playout suite installer (Inno Setup 6).
; Built by installer\Build-Installer.ps1, which publishes all 17 apps into one self-contained
; folder (no .NET install needed on the target PC) and passes the paths below with /D.
;
;   StageDir   published suite folder           (required)
;   VcRedist   vc_redist.x64.exe                (required; installed only when missing)
;   DemoDir    cgdemo-templates folder          (optional component, ~700 MB)
;   AppVersion e.g. 1.0.42                      Commit  short git hash
;   OutputDir  where the setup .exe is written

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef Commit
  #define Commit "local"
#endif
#ifndef OutputDir
  #define OutputDir "out"
#endif
#ifndef StageDir
  #error StageDir is required (run Build-Installer.ps1)
#endif
#ifndef VcRedist
  #error VcRedist is required (run Build-Installer.ps1)
#endif

#define AppName "Kashtrix Playout"
#define AppExe "Kashtrix.Playout.exe"

[Setup]
; Never change AppId: upgrades and uninstall are matched by it.
AppId={{6E0B7C2A-4F1D-4B8E-9C3A-5A1D0C4E7B1F}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion} ({#Commit})
AppPublisher=Kashtrix
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\Kashtrix Playout
DefaultGroupName=Kashtrix Playout
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
PrivilegesRequired=admin
OutputDir={#OutputDir}
OutputBaseFilename=Kashtrix-Playout-Setup-{#AppVersion}
SetupIconFile=..\src\BroadcastPlayout.App\Branding\kashtrix-playout.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/normal
SolidCompression=yes
LZMANumBlockThreads=4
WizardStyle=modern
; Close running Kashtrix apps on upgrade/uninstall (Windows Restart Manager).
CloseApplications=yes
RestartApplications=no

[Types]
Name: "full"; Description: "Full (suite + broadcast demo graphics)"
Name: "compact"; Description: "Suite only"
Name: "custom"; Description: "Custom"; Flags: iscustom

[Components]
Name: "suite"; Description: "Kashtrix Playout suite (Playout, CG Editor, Multiview, Scheduler and all tools)"; Types: full compact custom; Flags: fixed
#ifdef DemoDir
Name: "demos"; Description: "Broadcast demo CG packages (Prime HD, AP1 HD, Space 4K, about 700 MB)"; Types: full
#endif

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut for Kashtrix Playout"; GroupDescription: "Shortcuts:"

[Files]
Source: "{#StageDir}\*"; DestDir: "{app}"; Components: suite; Flags: ignoreversion recursesubdirs createallsubdirs
#ifdef DemoDir
Source: "{#DemoDir}\*"; DestDir: "{app}\cgdemo-templates"; Components: demos; Flags: ignoreversion recursesubdirs createallsubdirs
#endif
Source: "{#VcRedist}"; DestDir: "{tmp}"; DestName: "vc_redist.x64.exe"; Flags: deleteafterinstall; Check: VcRedistNeeded

[Icons]
Name: "{group}\Kashtrix Playout"; Filename: "{app}\{#AppExe}"
Name: "{group}\CG Editor"; Filename: "{app}\Kashtrix.CGEditor.exe"
Name: "{group}\CG Controller"; Filename: "{app}\Kashtrix.CGController.exe"
Name: "{group}\Multiview"; Filename: "{app}\Kashtrix.Multiview.exe"
Name: "{group}\Scheduler"; Filename: "{app}\Kashtrix.Scheduler.exe"
Name: "{group}\Playlist Editor"; Filename: "{app}\Kashtrix.PlaylistEditor.exe"
Name: "{group}\Channel Controller"; Filename: "{app}\Kashtrix.ChannelController.exe"
Name: "{group}\Settings"; Filename: "{app}\Kashtrix.Settings.exe"
Name: "{group}\Tools\File Manager"; Filename: "{app}\Kashtrix.FileManager.exe"
Name: "{group}\Tools\QC Controller"; Filename: "{app}\Kashtrix.QCController.exe"
Name: "{group}\Tools\Ingest Server"; Filename: "{app}\Kashtrix.IngestServer.exe"
Name: "{group}\Tools\MAM"; Filename: "{app}\Kashtrix.MAM.exe"
Name: "{group}\Tools\NRCS"; Filename: "{app}\Kashtrix.NRCS.exe"
Name: "{group}\Tools\Prompter"; Filename: "{app}\Kashtrix.Prompter.exe"
Name: "{group}\Tools\HA Controller"; Filename: "{app}\Kashtrix.HAController.exe"
Name: "{group}\Tools\API Gateway"; Filename: "{app}\Kashtrix.ApiGateway.exe"
Name: "{group}\Tools\Output Engine"; Filename: "{app}\Kashtrix.OutputEngine.exe"
Name: "{group}\Uninstall Kashtrix Playout"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Kashtrix Playout"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{tmp}\vc_redist.x64.exe"; Parameters: "/install /quiet /norestart"; StatusMsg: "Installing Microsoft Visual C++ runtime..."; Flags: waituntilterminated; Check: VcRedistNeeded
Filename: "{app}\{#AppExe}"; Description: "Start Kashtrix Playout now"; Flags: nowait postinstall skipifsilent unchecked

[Code]
// The browser-based HTML graphics (CefSharp) need the Visual C++ 2015-2022 runtime (14.30 or newer).
// Installed only when Windows does not already have a recent enough copy.
function VcRedistNeeded(): Boolean;
var
  installed, major, minor: Cardinal;
begin
  Result := True;
  if RegQueryDWordValue(HKLM64, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64', 'Installed', installed) and (installed = 1) and
     RegQueryDWordValue(HKLM64, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64', 'Major', major) and
     RegQueryDWordValue(HKLM64, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64', 'Minor', minor) then
    Result := (major < 14) or ((major = 14) and (minor < 30));
end;
