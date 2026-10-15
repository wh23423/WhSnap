; WhSnap 安装脚本（Inno Setup 6）
; 设计要点：
;   - per-user 安装到 %LOCALAPPDATA%\Programs\WhSnap：不需要管理员权限，自动更新也无需提权
;   - 默认勾选开机自启（写入 HKCU\...\Run），卸载时自动清理
;   - 卸载前结束正在运行的实例，卸载后清理安装目录
;
; 用法（由 build/tools/build-installer.ps1 调用）：
;   ISCC.exe build\installer\WhSnap.iss

#define MyAppName "WhSnap"
#ifndef MyAppVersion
  #define MyAppVersion "0.1.0"
#endif
#define MyAppPublisher "wh23423"
#define MyAppExeName "WhSnap.exe"
#define MyAppUrl "https://github.com/wh23423"
#define PayloadDir "..\..\artifacts\publish"

[Setup]
AppId={{B7A3F1C2-6D4E-4A19-9C3B-8E2F5A7D1B44}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion} (preview)
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppUrl}
AppSupportURL={#MyAppUrl}
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
DisableDirPage=no
PrivilegesRequired=lowest
OutputDir=..\..\artifacts\installer
OutputBaseFilename=WhSnap-{#MyAppVersion}-setup-x64
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile=..\..\src\WhSnap.App\Assets\whsnap.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName} {#MyAppVersion}
AppMutex=WhSnap_SingleInstance
MinVersion=10.0.22000
AllowNoIcons=yes

[Languages]
; Inno Setup 默认不附带简体中文语言文件。向导的标准文案使用英文；
; [Tasks]/[Icons] 里的自定义说明仍是中文（.iss 为 UTF-8 BOM，显示正常）。
; 如需全中文向导：把官方 ChineseSimplified.isl 放到 build\installer\languages\，
; 下面的条件会自动启用，无需改动本脚本。
Name: "english"; MessagesFile: "compiler:Default.isl"
#if FileExists(AddBackslash(SourcePath) + "languages\ChineseSimplified.isl")
Name: "chinesesimplified"; MessagesFile: "languages\ChineseSimplified.isl"
#endif

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："; Flags: checkedonce
Name: "autostart"; Description: "开机自动启动 {#MyAppName}（推荐）"; GroupDescription: "启动选项："; Flags: checkedonce

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#MyAppName}"; ValueData: """{app}\{#MyAppExeName}"" --tray"; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "立即运行 {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{cmd}"; Parameters: "/c taskkill /IM {#MyAppExeName} /F"; Flags: runhidden; RunOnceId: "StopWhSnap"

[UninstallDelete]
; 清理安装目录内的残留（配置与日志在 %APPDATA%\WhSnap，按惯例保留）
Type: filesandordirs; Name: "{app}"
