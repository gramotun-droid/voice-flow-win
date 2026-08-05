; Inno Setup script для VoiceFlowWin.
;
; Схема повторяет установщик MdReader: один лёгкий EXE, ярлык в меню «Пуск»,
; деинсталлятор, установка без прав администратора.
;
; Локальная сборка:  iscc installer\voiceflowwin.iss
; С версией:         iscc /DMyAppVersion=1.0.0 installer\voiceflowwin.iss

#ifndef MyAppVersion
#define MyAppVersion "0.0.0-dev"
#endif

; VersionInfoVersion попадает в ресурс версии PE-файла и допускает только
; числа: SemVer-суффикс вроде «-rc.1» компилятор отвергает. Поэтому для
; ресурса берётся числовая часть версии, а в AppVersion и имя файла уходит
; полная версия с суффиксом.
#if Pos("-", MyAppVersion) > 0
  #define MyAppVersionNumeric Copy(MyAppVersion, 1, Pos("-", MyAppVersion) - 1)
#else
  #define MyAppVersionNumeric MyAppVersion
#endif

#define MyAppName "VoiceFlowWin"
#define MyAppPublisher "gramotun"
#define MyAppURL "https://github.com/gramotun-droid/voice-flow-win"
#define MyAppExeName "VoiceFlowWin.exe"

[Setup]
AppId={{7C1E2B84-4F1D-4A2E-9C3B-2D7A5F0E9B41}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
VersionInfoVersion={#MyAppVersionNumeric}

; Установка для текущего пользователя: права администратора не нужны, а
; окно с запросом UAC при каждом обновлении только мешало бы.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\VoiceFlowWin
DefaultGroupName=VoiceFlowWin
DisableProgramGroupPage=yes

; Все относительные пути отсчитываются от корня репозитория.
SourceDir=..
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=installer_output
OutputBaseFilename=VoiceFlowWin-Setup-x64-{#MyAppVersion}
SetupIconFile=src\VoiceFlowWin.App\Assets\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

; Закрыть работающий экземпляр перед обновлением, иначе файлы заняты.
CloseApplications=yes
CloseApplicationsFilter=*.exe,*.dll
RestartApplications=no

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "autostart"; Description: "Запускать VoiceFlowWin при входе в Windows"; GroupDescription: "Автозапуск"; Flags: unchecked

[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\VoiceFlowWin"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,VoiceFlowWin}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\VoiceFlowWin"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
    ValueName: "VoiceFlowWin"; ValueData: """{app}\{#MyAppExeName}"" --minimized"; \
    Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,VoiceFlowWin}"; Flags: nowait postinstall skipifsilent

; Тихая установка — это обновление из самого приложения: там нет мастера с
; галочкой «запустить», поэтому приложение поднимается установщиком, который
; точно знает, что установка закончена.
Filename: "{app}\{#MyAppExeName}"; Parameters: "--minimized"; Flags: nowait; Check: WizardSilent

; Настройки, словарь и скачанные модели лежат в %LocalAppData%\VoiceFlowWin и
; при удалении программы сознательно не трогаются: пользователь не должен
; терять гигабайты моделей и свой словарь из-за переустановки.
[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\VoiceFlowWin\Updates"
