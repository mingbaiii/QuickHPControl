; ================================================================
;  QuickHPControl — Inno Setup 7 安装脚本
;  用法: ISCC.exe installer.iss
; ================================================================

#define AppName       "QuickHPControl"
#define AppVersion    "1.0.0.0"
#define AppPublisher  "mingbai"
#define AppExeName    "QuickHPControl.exe"
#define Net481Rel     "533320"   ; .NET 4.8.1 最低 Release 号

[Setup]
AppId={{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={pf}\{#AppName}
DefaultGroupName={#AppName}
PrivilegesRequired=admin
OutputDir=Installer
OutputBaseFilename=QuickHPControl_Setup_{#AppVersion}
Compression=lzma
SolidCompression=yes
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}
WizardStyle=modern
DisableProgramGroupPage=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "bin\Release\net481\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent shellexec unchecked

[Code]
// ----------------------------------------------------------------
//  检查 .NET Framework 4.8.1 是否已安装
// ----------------------------------------------------------------
function IsDotNet481Installed: Boolean;
var
  ReleaseKey: string;
  Release: Cardinal;
begin
  ReleaseKey := 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full';
  Result := RegQueryDWordValue(HKLM, ReleaseKey, 'Release', Release) and
            (Release >= {#Net481Rel});
end;

function InitializeSetup: Boolean;
var
  ErrorCode: Integer;
begin
  Result := True;
  if not IsDotNet481Installed then
  begin
    if MsgBox(
      '.NET Framework 4.8.1 is required but was not found.' + #13#10 +
      'Please install it from Microsoft''s website and then re-run this installer.' + #13#10#13#10 +
      'Open the download page in your browser now?',
      mbConfirmation, MB_YESNO) = IDYES then
    begin
      ShellExec('open', 'https://dotnet.microsoft.com/en-us/download/dotnet-framework/net481', '', '', SW_SHOW, ewNoWait, ErrorCode);
    end;
    Result := False;
  end;
end;

// ----------------------------------------------------------------
//  卸载前结束 QuickHPControl 进程
// ----------------------------------------------------------------
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if CurUninstallStep = usAppMutexCheck then
  begin
    // 强制结束进程（/f），静默隐藏窗口（SW_HIDE）
    ShellExec('open', 'taskkill.exe', '/f /im {#AppExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    ShellExec('open', 'taskkill.exe', '/f /im HP.SystemControl.Background.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;
