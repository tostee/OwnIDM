; OwnIDM installer (Inno Setup 6.3+). Build it with installer\build-installer.ps1.
;  * Per-user install, no administrator rights (like the rest of OwnIDM).
;  * Small: yt-dlp, ffmpeg and Deno are not included; OwnIDM downloads them when first needed
;    and keeps yt-dlp up to date (Opciones -> Video -> Componentes).
;  * Installs the .NET 8 Desktop Runtime from Microsoft if it is missing.
;  * It is also the update package: OwnIDM downloads it and runs it with /SILENT /RELAUNCH=1; it
;    replaces the files in the same folder and opens OwnIDM again.
;  * Signed when build-installer.ps1 is given a certificate (it then defines SignTool).

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define BuildDir "..\build\Release"

[Setup]
AppId={{6C1C3C7E-4F0B-4C7A-9C55-0D1D3B9E2A71}
AppName=OwnIDM
AppVersion={#AppVersion}
AppVerName=OwnIDM {#AppVersion}
AppPublisher=OwnIDM
AppCopyright=© 2026 OwnIDM
VersionInfoVersion={#AppVersion}
VersionInfoCompany=OwnIDM
VersionInfoDescription=Instalador de OwnIDM
VersionInfoProductName=OwnIDM
VersionInfoProductVersion={#AppVersion}
AppPublisherURL=https://github.com/tostee/OwnIDM
; What OwnIDM connects to and changes in the system, shown before installing (not in silent updates).
InfoBeforeFile=PRIVACIDAD.txt
DefaultDirName={autopf}\OwnIDM
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=OwnIDM-Setup-{#AppVersion}
SetupIconFile=..\src\OwnIDM.App\Assets\ownidm.ico
UninstallDisplayIcon={app}\OwnIDM.exe
UninstallDisplayName=OwnIDM
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ShowLanguageDialog=no
; Closes a running OwnIDM (and its browser host) before replacing or removing its files.
CloseApplications=force
; Always writes %TEMP%\Setup Log <date> #NNN.txt: a silent update that fails is otherwise undiagnosable.
SetupLogging=yes
#ifdef SignTool
SignTool={#SignTool}
SignedUninstaller=yes
#endif

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#BuildDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\OwnIDM"; Filename: "{app}\OwnIDM.exe"
Name: "{autodesktop}\OwnIDM"; Filename: "{app}\OwnIDM.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\OwnIDM.exe"; Description: "{cm:LaunchProgram,OwnIDM}"; Flags: nowait postinstall skipifsilent
; Update started from OwnIDM (silent): open it again once its files are replaced.
Filename: "{app}\OwnIDM.exe"; Flags: nowait; Check: RelaunchRequested

; OwnIDM writes these itself at runtime (browser host, start with Windows); remove them on uninstall.
[Registry]
Root: HKCU; Subkey: "Software\Google\Chrome\NativeMessagingHosts\com.ownidm.host"; Flags: uninsdeletekey dontcreatekey
Root: HKCU; Subkey: "Software\Microsoft\Edge\NativeMessagingHosts\com.ownidm.host"; Flags: uninsdeletekey dontcreatekey
Root: HKCU; Subkey: "Software\Chromium\NativeMessagingHosts\com.ownidm.host"; Flags: uninsdeletekey dontcreatekey
Root: HKCU; Subkey: "Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\com.ownidm.host"; Flags: uninsdeletekey dontcreatekey
Root: HKCU; Subkey: "Software\Vivaldi\NativeMessagingHosts\com.ownidm.host"; Flags: uninsdeletekey dontcreatekey
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "OwnIDM"; Flags: uninsdeletevalue dontcreatekey

; Magnet / .torrent associations (Opciones -> Torrent): OwnIDM removes its own entries and leaves those
; of other torrent clients alone.
[UninstallRun]
Filename: "{app}\OwnIDM.exe"; Parameters: "--unregister-associations"; Flags: runhidden waituntilterminated; RunOnceId: "UnregisterAssociations"

; The download list and settings (%APPDATA%\OwnIDM) are kept; downloaded components and temporary files go.
[UninstallDelete]
Type: files; Name: "{localappdata}\OwnIDM\com.ownidm.host.json"
Type: filesandordirs; Name: "{localappdata}\OwnIDM\tools"
Type: filesandordirs; Name: "{localappdata}\OwnIDM\Temp"
Type: filesandordirs; Name: "{localappdata}\OwnIDM\Torrent"
Type: filesandordirs; Name: "{localappdata}\OwnIDM\Update"

[Code]
const
  DotNetUrl = 'https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe';
  DotNetFile = 'windowsdesktop-runtime-8-win-x64.exe';

  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{6C1C3C7E-4F0B-4C7A-9C55-0D1D3B9E2A71}_is1';

var
  DownloadPage: TDownloadWizardPage;
  InstallSucceeded: Boolean;
  { This instance handed the work to an elevated copy of itself and must not report a failure. }
  HandedToElevated: Boolean;

function RelaunchRequested: Boolean;
begin
  Result := ExpandConstant('{param:RELAUNCH|0}') = '1';
end;

function CanWriteTo(Dir: String): Boolean;
var
  Probe: String;
begin
  Probe := AddBackslash(Dir) + 'ownidm-write-test.tmp';
  Result := SaveStringToFile(Probe, '', False);
  if Result then
    DeleteFile(Probe);
end;

function QuoteParam(S: String): String;
begin
  if Pos(' ', S) > 0 then
    Result := '"' + S + '"'
  else
    Result := S;
end;

{ Setup runs without administrator rights, but an earlier copy may have been installed elevated into a folder
  such as C:\Program Files\OwnIDM. Replacing those files then fails with "Acceso denegado" and the update is
  rolled back. In that case start this same installer again elevated (Windows asks for permission), on the same
  folder and with the same parameters. }
function InitializeSetup: Boolean;
var
  PrevDir, Params, P: String;
  I, ResultCode: Integer;
begin
  Result := True;
  if IsAdmin or (ExpandConstant('{param:ELEVATED|0}') = '1') then
    exit;
  if not RegQueryStringValue(HKCU, UninstallKey, 'Inno Setup: App Path', PrevDir) then
    exit;
  if not DirExists(PrevDir) or CanWriteTo(PrevDir) then
    exit;

  Log('No se puede escribir en ' + PrevDir + '; se vuelve a iniciar el instalador con permisos de administrador.');
  Params := '';
  for I := 1 to ParamCount do
  begin
    P := ParamStr(I);
    if (CompareText(Copy(P, 1, 5), '/SL5=') <> 0) and (CompareText(Copy(P, 1, 5), '/DIR=') <> 0) then
      Params := Params + QuoteParam(P) + ' ';
  end;
  Params := Params + '/DIR="' + PrevDir + '" /ELEVATED=1';

  if ShellExec('runas', ExpandConstant('{srcexe}'), Params, '', SW_SHOW, ewNoWait, ResultCode) then
    HandedToElevated := True
  else
  begin
    Log('No se concedieron permisos de administrador (código ' + IntToStr(ResultCode) + ').');
    SuppressibleMsgBox('OwnIDM está instalado en ' + PrevDir + ', una carpeta que necesita permisos de administrador.' + #13#10 +
      'Vuelve a intentar la actualización y acepta el aviso de Windows.', mbError, MB_OK, IDOK);
  end;
  Result := False;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    InstallSucceeded := True;
end;

{ An update started from OwnIDM that failed (and was rolled back) would otherwise leave the user with
  OwnIDM closed and no explanation. Leave the log path where OwnIDM looks at startup (AppUpdater) and
  open the old version again. }
procedure DeinitializeSetup;
var
  ResultCode: Integer;
  AppExe: String;
begin
  if InstallSucceeded or HandedToElevated or not RelaunchRequested then
    exit;
  try
    ForceDirectories(ExpandConstant('{localappdata}\OwnIDM\Update'));
    SaveStringToFile(ExpandConstant('{localappdata}\OwnIDM\Update\failed.txt'), ExpandConstant('{log}'), False);
  except
    Log('No se pudo guardar el aviso de actualización fallida: ' + GetExceptionMessage);
  end;
  try
    AppExe := ExpandConstant('{app}\OwnIDM.exe');
    if FileExists(AppExe) then
      Exec(AppExe, '', '', SW_SHOWNORMAL, ewNoWait, ResultCode);
  except
    Log('No se pudo volver a abrir OwnIDM: ' + GetExceptionMessage);
  end;
end;

function DotNetDesktop8Installed: Boolean;
var
  FindRec: TFindRec;
begin
  Result := FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\8.*'), FindRec);
  if Result then
    FindClose(FindRec);
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage('Descargando .NET 8', 'OwnIDM necesita el entorno .NET 8 Desktop Runtime de Microsoft.', nil);
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo,
  MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := MemoDirInfo + NewLine + NewLine + MemoTasksInfo;
  if not DotNetDesktop8Installed then
    Result := Result + NewLine + NewLine + 'Se descargará e instalará .NET 8 Desktop Runtime de Microsoft (unos 58 MB; Windows pedirá permiso).';
end;

{ Downloads and installs the .NET 8 Desktop Runtime before OwnIDM's files are copied. }
function NextButtonClick(CurPageID: Integer): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;
  if (CurPageID <> wpReady) or DotNetDesktop8Installed then
    exit;

  DownloadPage.Clear;
  DownloadPage.Add(DotNetUrl, DotNetFile, '');
  DownloadPage.Show;
  try
    try
      DownloadPage.Download;
    except
      if DownloadPage.AbortedByUser then
        Log('Descarga de .NET cancelada.')
      else
        SuppressibleMsgBox(AddPeriod(GetExceptionMessage), mbCriticalError, MB_OK, IDOK);
      Result := False;
      exit;
    end;
  finally
    DownloadPage.Hide;
  end;

  { The runtime is installed for all users, so its own installer asks for elevation. }
  if not ShellExec('runas', ExpandConstant('{tmp}\' + DotNetFile), '/install /quiet /norestart', '',
                   SW_SHOW, ewWaitUntilTerminated, ResultCode) then
    ResultCode := -1;
  { 3010: installed, a restart is pending; 1638: a newer 8.0 is already there. }
  if (ResultCode <> 0) and (ResultCode <> 3010) and (ResultCode <> 1638) then
  begin
    SuppressibleMsgBox('No se pudo instalar .NET 8 Desktop Runtime (código ' + IntToStr(ResultCode) + ').' + #13#10 +
      'Puedes instalarlo a mano desde https://dotnet.microsoft.com/download/dotnet/8.0 y volver a ejecutar este instalador.',
      mbError, MB_OK, IDOK);
    Result := False;
  end;
end;
