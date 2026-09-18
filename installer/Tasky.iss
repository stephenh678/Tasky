; Tasky installer (Inno Setup 6).
;
; Build with build-installer.ps1 in the repo root, which publishes the app and then invokes this
; with the right /D defines. Compiling this file directly needs AppVersion and PublishDir passed
; on the ISCC command line.
;
; Design notes:
;   * Per-user by default (PrivilegesRequired=lowest), so installing needs no administrator rights
;     and - just as importantly - the app can replace its own files when it self-updates. A
;     Program Files install would make every future update require elevation.
;   * AppId is a fixed GUID. It is what Windows keys upgrades on: keep it stable forever, or an
;     upgrade turns into a second parallel installation.
;   * The uninstaller shells out to Tasky.exe --uninstall-cleanup for the per-user state that
;     lives outside this folder (settings, Drive sign-in cache, update staging, the Run key, toast
;     registration). That knowledge belongs to the app, not to this script - see
;     Services/UninstallCleanupService.cs.

#ifndef AppVersion
  #error AppVersion must be passed in, e.g. ISCC /DAppVersion=1.2.0
#endif
#ifndef PublishDir
  #error PublishDir must be passed in, e.g. ISCC /DPublishDir=..\publish
#endif

#define AppName "Tasky"
#define AppPublisher "stephenh678"
#define AppUrl "https://github.com/stephenh678/Tasky"
#define AppExe "Tasky.exe"

[Setup]
; Never change this GUID - it is the identity Windows uses to recognise an existing install.
AppId={{8F1C5A2E-4B3D-4C7A-9E6F-2A8D0B4E7C15}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
VersionInfoVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
DefaultDirName={autopf}\{#AppName}
; One app, one shortcut - a Start Menu folder page just adds a step with nothing to decide.
DisableProgramGroupPage=yes
DefaultGroupName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
OutputDir={#PublishDir}\..\installer-output
OutputBaseFilename=Tasky-Setup-{#AppVersion}
SetupIconFile={#SourcePath}\..\Assets\icon.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; lowest = per-user install, no UAC prompt. See the design note above on why this matters beyond
; convenience: the in-app updater replaces these files in place.
PrivilegesRequired=lowest
; Self-contained win-x64 build - refuse to install on anything that can't run it, rather than
; failing confusingly at launch.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Use Restart Manager to detect and close a running Tasky instead of failing on a locked
; Tasky.exe. Tasky has no single-instance mutex, so file locks are what identifies it.
CloseApplications=yes
RestartApplications=yes
MinVersion=10.0.17763

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "startupicon"; Description: "Start Tasky when I sign in to Windows"; GroupDescription: "Startup:"; Flags: unchecked

[InstallDelete]
; Inno overwrites what a release ships but leaves behind what it no longer does, so a file dropped
; between versions would linger forever - the loose managed DLLs from before Tasky moved to a
; single-file build being the case that actually happened. The zip-based updater this replaced
; mirrored the folder for exactly that reason; these patterns keep that property without the risk
; of a blanket "{app}\*" wipe, which would take unins000.dat/exe with it mid-upgrade and leave the
; install unremovable. Tasky.exe and README.md are excluded because the [Files] below always
; rewrites them anyway.
Type: files; Name: "{app}\*.dll"
Type: files; Name: "{app}\*.json"
Type: files; Name: "{app}\*.pdb"

[Files]
; Whole publish folder, recursively - no per-file list to drift out of step with the build.
; PublishSingleFile still leaves WPF's native DLLs alongside Tasky.exe, so this must not be
; narrowed to just the exe.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SourcePath}\..\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon
; Writes the same HKCU Run entry StartupService toggles from Settings, so the two agree. The app
; reads that key fresh on every check (it keeps no cached copy), so ticking this box simply shows
; up as "Start with Windows" already enabled.
Name: "{userstartup}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: startupicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
; The in-app updater runs this installer with /SILENT, where the postinstall entry above is
; skipped - so Tasky would update and then simply not come back. /LAUNCHAFTER=1 is our own flag
; (UpdateService.ApplyUpdateAndRestart passes it) asking for an explicit relaunch instead of
; relying on Restart Manager, which only restarts what it chose to close.
Filename: "{app}\{#AppExe}"; Flags: nowait; Check: ShouldLaunchAfterSilentInstall

[UninstallRun]
; Runs while Tasky.exe still exists, before [Files] are removed. RemoveTaskData is set by the
; prompt in InitializeUninstall below. runascurrentuser matters: this must touch the profile of
; the person uninstalling, never an administrator's.
Filename: "{app}\{#AppExe}"; Parameters: "--uninstall-cleanup {code:GetRemoveDataFlag}"; RunOnceId: "TaskyUninstallCleanup"; Flags: waituntilterminated runascurrentuser skipifdoesntexist

[Code]
var
  RemoveTaskData: Boolean;

function ShouldLaunchAfterSilentInstall(): Boolean;
begin
  Result := ExpandConstant('{param:LAUNCHAFTER|0}') = '1';
end;

function GetRemoveDataFlag(Param: string): string;
begin
  if RemoveTaskData then
    Result := '--remove-data'
  else
    Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  // Default to KEEPING the data: the .tasky files, backups and attachments are the only
  // irreplaceable thing here, and an uninstall is not obviously a request to delete them.
  RemoveTaskData :=
    SuppressibleMsgBox(
      'Also delete your Tasky tasks, backups and attachments?' + #13#10#13#10 +
      'Your settings and Google Drive sign-in will be removed either way.' + #13#10 +
      'Choose No to keep your task data in Documents\Tasky.',
      mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES;
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    // Removing the local sign-in cache signs Tasky out on this machine but does not revoke the
    // app's access on Google's side - that can only be done from the account itself, so say so
    // rather than leaving the user assuming an uninstall covered it.
    SuppressibleMsgBox(
      'Tasky has been removed.' + #13#10#13#10 +
      'Signing out locally does not revoke Tasky''s access to your Google Drive. To revoke it, ' +
      'visit https://myaccount.google.com/permissions',
      mbInformation, MB_OK, IDOK);
  end;
end;
