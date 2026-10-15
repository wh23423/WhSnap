; WhSnap 安装脚本（Inno Setup 6）
; 设计要点：
;   - per-user 安装到 %LOCALAPPDATA%\Programs\WhSnap：不需要管理员权限，自动更新也无需提权
;   - 默认勾选开机自启（写入 HKCU\...\Run），卸载时自动清理
;   - 升级就地：沿用上次安装目录（UsePreviousAppDir + DisableDirPage=auto）
;   - 安装日志写到 %TEMP%\Setup Log *.txt；安装/卸载前自动关闭正在运行的实例
;   - 卸载时询问"是否保留配置与日志"（静默卸载默认保留，绝不静默删用户数据）
;   - 用户手册存在就装进应用目录并加开始菜单项（见 MyAppManual）
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
; 用户手册：存在就装进应用目录并加开始菜单项（T7 产出后随下次构建自动生效）
#define MyAppManual "whsnap-用户手册.md"
#define ManualPath AddBackslash(SourcePath) + "..\..\docs\" + MyAppManual

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
; auto = 覆盖安装时不再问安装目录（升级就地），首次安装仍然让用户选
DisableDirPage=auto
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
; 升级就地：沿用上次的安装目录（Inno 默认就是 yes，这里显式写出来，避免以后被误改）
UsePreviousAppDir=yes
; 安装日志写到 %TEMP%\Setup Log *.txt：装完出问题能直接看
SetupLogging=yes
; 安装/升级前自动关掉正在运行的 WhSnap（靠 Restart Manager），卸载器同样处理
CloseApplications=yes
RestartApplications=no

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
#if FileExists(ManualPath)
Source: "..\..\docs\{#MyAppManual}"; DestDir: "{app}"; Flags: ignoreversion
#endif

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
#if FileExists(ManualPath)
Name: "{group}\{#MyAppName} 用户手册"; Filename: "{app}\{#MyAppManual}"
#endif
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#MyAppName}"; ValueData: """{app}\{#MyAppExeName}"" --tray"; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "立即运行 {#MyAppName}"; Flags: nowait postinstall skipifsilent
#if FileExists(ManualPath)
Filename: "{app}\{#MyAppManual}"; Description: "查看用户手册"; Flags: postinstall shellexec skipifsilent unchecked
#endif

[UninstallRun]
Filename: "{cmd}"; Parameters: "/c taskkill /IM {#MyAppExeName} /F"; Flags: runhidden; RunOnceId: "StopWhSnap"

[UninstallDelete]
; 清理安装目录内的残留（配置与日志在 %APPDATA%\WhSnap，默认保留，见 [Code]）
Type: filesandordirs; Name: "{app}"

[Code]
var
  KeepUserData: Boolean;

function WhSnapUserDataDir: String;
begin
  Result := ExpandConstant('{userappdata}\WhSnap');
end;

{ 卸载开始时决定"是否保留配置与日志"：
  - `/DELUSERDATA`：无人值守时明确要求连用户数据一起删（脚本化测试 / 彻底清理用）；
  - 静默卸载（/SILENT、/VERYSILENT，且没给上面的参数）：默认**保留**——绝不在没人看着的时候删用户数据；
  - 交互式卸载：弹一次确认框，默认按钮是"是"（保留）。
  删的是 %APPDATA%\WhSnap（配置、日志、崩溃证据、诊断包、性能样本都在里面）。 }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Answer: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    KeepUserData := True;

    if Pos('/DELUSERDATA', Uppercase(GetCmdTail)) > 0 then
    begin
      KeepUserData := False;
    end
    else if not UninstallSilent then
    begin
      Answer := MsgBox(
        '是否保留 WhSnap 的配置、日志与崩溃证据？' + #13#10 + #13#10 +
        WhSnapUserDataDir + #13#10 + #13#10 +
        '是：保留（推荐；重新安装后设置、颜色历史与日志都还在）' + #13#10 +
        '否：连同该目录一起删除（不可恢复）',
        mbConfirmation, MB_YESNO or MB_DEFBUTTON1);

      KeepUserData := (Answer = IDYES);
    end;
  end
  else if CurUninstallStep = usPostUninstall then
  begin
    { 双保险：只删"目录名正好是 WhSnap"的那个路径，避免误删 }
    if (not KeepUserData) and (ExtractFileName(WhSnapUserDataDir) = 'WhSnap') then
      DelTree(WhSnapUserDataDir, True, True, True);
  end;
end;
