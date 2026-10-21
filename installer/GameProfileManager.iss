#ifndef SourceDir
  #error SourceDir must point to a self-contained win-x64 publish directory.
#endif

#define AppName "Game Profile Manager"
#define AppExe "GameProfileManager.exe"
#define AppVersion GetVersionNumbersString(AddBackslash(SourceDir) + AppExe)

[Setup]
AppId=GameProfileManager.Vladislav2505
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Vladislav2505
AppPublisherURL=https://github.com/Vladislav2505/GameProfileManager
AppSupportURL=https://github.com/Vladislav2505/GameProfileManager/issues
DefaultDirName={autopf}\GameProfileManager
UsePreviousAppDir=yes
DisableDirPage=no
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.22000
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
SetupIconFile=..\src\GameProfileManager\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
VersionInfoVersion={#AppVersion}
OutputBaseFilename=GameProfileManager-Setup-{#AppVersion}-win-x64

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "Создать значок на рабочем столе"; GroupDescription: "Дополнительные значки:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Game Profile Manager"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"
Name: "{autodesktop}\Game Profile Manager"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
Root: HKLM64; Subkey: "Software\GameProfileManager"; Flags: uninsdeletekeyifempty
Root: HKLM64; Subkey: "Software\GameProfileManager"; ValueType: string; ValueName: "InstallDir"; ValueData: "{app}"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExe}"; Description: "Запустить Game Profile Manager"; WorkingDir: "{app}"; Flags: postinstall nowait skipifsilent

[Code]
function InitializeSetup: Boolean;
var
  InstalledDir: String;
  InstalledVersion: Int64;
  SetupVersion: Int64;
  Comparison: Integer;
begin
  Result := True;
  if not RegQueryStringValue(HKLM64, 'Software\GameProfileManager', 'InstallDir', InstalledDir) then
    Exit;
  if not GetPackedVersion(AddBackslash(InstalledDir) + '{#AppExe}', InstalledVersion) then
    Exit;
  if not GetPackedVersion(ExpandConstant('{srcexe}'), SetupVersion) then
    Exit;

  Comparison := ComparePackedVersion(InstalledVersion, SetupVersion);
  if Comparison < 0 then
    Result := MsgBox('Установлена версия ' + VersionToStr(InstalledVersion) + '. Обновить до ' +
      VersionToStr(SetupVersion) + '? Папка установки и пользовательские данные сохранятся.',
      mbConfirmation, MB_YESNO) = IDYES
  else if Comparison = 0 then
    Result := MsgBox('Версия ' + VersionToStr(SetupVersion) +
      ' уже установлена. Переустановить файлы программы?', mbConfirmation, MB_YESNO) = IDYES
  else
  begin
    MsgBox('Установлена более новая версия ' + VersionToStr(InstalledVersion) +
      '. Этот установщик содержит версию ' + VersionToStr(SetupVersion) +
      '. Установка старой версии отменена.', mbInformation, MB_OK);
    Result := False;
  end;
end;
