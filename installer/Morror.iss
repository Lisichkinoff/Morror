#define ProjectRoot AddBackslash(SourcePath) + ".."
#ifndef InstallerOutput
  #define InstallerOutput ProjectRoot + "\dist"
#endif

[Setup]
#ifdef TestBuild
AppId=Morror.Installer.Test
OutputBaseFilename=Morror-{#MyAppVersion}-Setup-Test
CreateUninstallRegKey=no
AppMutex=Local\Morror.Installer.Test.App
SetupMutex=Local\Morror.Installer.Test.Setup
#else
AppId={{DF58DC88-4346-493F-BCCE-9BCA671D2387}
OutputBaseFilename=Morror-{#MyAppVersion}-Setup-win64
AppMutex=Local\Morror.Desktop,Local\Morror.iPhone
SetupMutex=Local\Morror.Setup
#endif
AppName=Morror
AppVersion={#MyAppVersion}
AppVerName=Morror {#MyAppVersion}
AppPublisher=Morror
VersionInfoVersion={#MyAppVersion}.0
VersionInfoDescription=Установка Morror
DefaultDirName={localappdata}\Programs\Morror
DefaultGroupName=Morror
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#InstallerOutput}
SetupIconFile={#ProjectRoot}\assets\morror.ico
UninstallDisplayIcon={app}\Morror.exe
WizardImageFile={#ProjectRoot}\assets\wizard-large.bmp
WizardSmallImageFile={#ProjectRoot}\assets\wizard-small.bmp
WizardStyle=modern
DisableWelcomePage=no
DisableDirPage=no
DisableProgramGroupPage=yes
UsePreviousAppDir=yes
Compression=lzma2/normal
SolidCompression=yes
CloseApplications=no
RestartApplications=no
Uninstallable=not IsPortableUpdate
UninstallDisplayName=Morror
SetupLogging=yes

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; GroupDescription: "Ярлыки:";

[Files]
#include FileManifest

[Icons]
#ifdef TestBuild
Name: "{app}\TestStartMenu\Morror"; Filename: "{app}\Morror.exe"; WorkingDir: "{app}"; IconFilename: "{app}\morror.ico"; Check: not IsPortableUpdate
Name: "{app}\TestDesktop\Morror"; Filename: "{app}\Morror.exe"; WorkingDir: "{app}"; IconFilename: "{app}\morror.ico"; Tasks: desktopicon; Check: not IsPortableUpdate
#else
Name: "{autoprograms}\Morror\Morror"; Filename: "{app}\Morror.exe"; WorkingDir: "{app}"; IconFilename: "{app}\morror.ico"; Comment: "Экран и звук Android на компьютере"; Check: not IsPortableUpdate
Name: "{autodesktop}\Morror"; Filename: "{app}\Morror.exe"; WorkingDir: "{app}"; IconFilename: "{app}\morror.ico"; Comment: "Экран и звук Android на компьютере"; Tasks: desktopicon; Check: not IsPortableUpdate
#endif

[Run]
Filename: "{app}\Morror.exe"; Description: "Запустить Morror"; Flags: nowait postinstall skipifsilent

[Messages]
AppRunningError=Программа Morror уже запущена.%n%nОткройте значок Morror рядом с часами Windows и выберите «Выйти из Morror», затем повторите установку или удаление.

[Code]
function IsPortableUpdate(): Boolean;
var
  Index: Integer;
begin
  Result := False;
  for Index := 1 to ParamCount do
    if CompareText(ParamStr(Index), '/MORRORPORTABLEUPDATE') = 0 then
      Result := True;
end;

function NeedsFile(RelativePath, ExpectedHash: String): Boolean;
var
  Destination: String;
begin
  Destination := ExpandConstant('{app}\') + RelativePath;
  Result := True;
  if FileExists(Destination) then
    try
      Result := CompareText(GetSHA256OfFile(Destination), ExpectedHash) <> 0;
    except
      Result := True;
    end;
end;

function InitializeSetup(): Boolean;
begin
  Result := IsDotNetInstalled(net48, 0);
  if not Result then
    SuppressibleMsgBox('Для Morror требуется .NET Framework 4.8 или новее. Установите его и повторите запуск установщика.', mbCriticalError, MB_OK, IDOK);
end;
