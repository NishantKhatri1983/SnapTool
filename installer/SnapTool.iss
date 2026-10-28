; Inno Setup script for SnapTool. Build with installer\build.ps1 (it publishes the exe first).
; Installs per user by default (no admin rights needed); an administrator can choose "all users" instead.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\bin\publish"
#endif

#define AppName "SnapTool"
#define AppExe "SnapTool.exe"
#define AppUrl "https://github.com/NishantKhatri1983/SnapTool"
#define DotNetUrl "https://dotnet.microsoft.com/download/dotnet/8.0"

[Setup]
; Keep this AppId for every future version so upgrades replace the installed copy.
AppId={{2E22694F-9E4F-40B0-A149-FA06DEB70AF6}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=NishantKhatri1983
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
LicenseFile=..\LICENSE
SetupIconFile=..\snaptool.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
; Ask to close a running SnapTool (its single-instance lock) before installing or uninstalling.
AppMutex=Local\SnapTool.SingleInstance
CloseApplications=yes
OutputDir=..\bin\installer
OutputBaseFilename=SnapTool-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}

[Tasks]
Name: "autostart"; Description: "Start SnapTool when I sign in to Windows"
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "{#PublishDir}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Same value SnapTool itself writes when "Start with Windows" is ticked in its menu.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "SnapTool"; \
  ValueData: """{app}\{#AppExe}"""; Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExe}"; Description: "Start SnapTool now"; Flags: nowait postinstall skipifsilent

[Code]
// SnapTool needs the .NET 8 Desktop Runtime (x64), or a newer one: it rolls forward to .NET 9, 10, ...
function HasDesktopRuntime(Root: String): Boolean;
var
  FindRec: TFindRec;
  Major: Integer;
begin
  Result := False;
  if FindFirst(Root + '\dotnet\shared\Microsoft.WindowsDesktop.App\*', FindRec) then
  try
    repeat
      if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0) and (FindRec.Name <> '.') and (FindRec.Name <> '..') then
      begin
        Major := StrToIntDef(Copy(FindRec.Name, 1, Pos('.', FindRec.Name + '.') - 1), 0);
        if Major >= 8 then Result := True;
      end;
    until Result or not FindNext(FindRec);
  finally
    FindClose(FindRec);
  end;
end;

function InitializeSetup(): Boolean;
var
  ErrorCode: Integer;
begin
  Result := True;
  if HasDesktopRuntime(ExpandConstant('{commonpf64}')) then Exit;
  if MsgBox('SnapTool needs the Microsoft .NET 8 Desktop Runtime (x64), which isn''t installed on this PC.' + #13#10#13#10 +
            'Open the download page now? On that page, choose ".NET Desktop Runtime" > Windows "x64" and install it. ' +
            'SnapTool will be installed either way, and starts once the runtime is there.',
            mbConfirmation, MB_YESNO) = IDYES then
    ShellExec('open', '{#DotNetUrl}', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Value: String;
begin
  // SnapTool can switch "Start with Windows" on by itself too; remove that entry if it points at this copy.
  if CurUninstallStep = usPostUninstall then
    if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'SnapTool', Value) then
      if Pos(Lowercase(ExpandConstant('{app}')), Lowercase(Value)) > 0 then
        RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'SnapTool');
end;
