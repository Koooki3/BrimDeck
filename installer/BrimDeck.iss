; BrimDeck installer. Build it with scripts/Publish-Release.ps1, which passes AppVersion and SourceDir.
; The other defines exist so scripts/Test-AppUpdate.ps1 can install an isolated copy under its own identity.
;
; Install:   install mode (current user / all users) -> folder -> Install -> finished page with
;            "create desktop shortcut", "create Start menu shortcut" and "run BrimDeck".
; Update:    the installed version is uninstalled first (keeping data), then the new one is installed.
; Uninstall: one dialog with an unchecked "delete user data" box -> uninstall -> "uninstalled" message.

#ifndef AppVersion
  #error Define AppVersion, for example /DAppVersion=0.2.0
#endif
#ifndef SourceDir
  #error Define SourceDir as the folder produced by dotnet publish
#endif
; The Windows file version takes numbers only, so a prerelease label ("0.3.0-beta.1") is left out of it.
#if Pos("-", AppVersion) > 0
  #define FileVersion Copy(AppVersion, 1, Pos("-", AppVersion) - 1)
#else
  #define FileVersion AppVersion
#endif
; Must match GitHubUpdates.InstallerAppId. Never change it, or updates install a second copy.
#ifndef AppId
  #define AppId "9D9AD6ED-CF62-4527-A587-6DBE94A74B9E"
#endif
#ifndef AppName
  #define AppName "BrimDeck"
#endif
#ifndef MainExe
  #define MainExe "BrimDeck.exe"
#endif
; Settings, secrets and caches. Installing and updating never touch this folder; uninstalling deletes it on request.
#ifndef DataDir
  #define DataDir "{localappdata}\BrimDeck"
#endif
; The app holds Local\BrimDeck.Instance.<user name> while it runs (App.OnStartup).
#ifndef MutexPrefix
  #define MutexPrefix "BrimDeck.Instance."
#endif
; Value the app writes under HKCU\...\Run for launch at startup (StartupRegistration).
#ifndef RunValue
  #define RunValue "BrimDeck"
#endif
#ifndef AppUserModelId
  #define AppUserModelId "BrimDeck.Desktop"
#endif
#ifndef Compression
  #define Compression "lzma2/max"
#endif
#define UninstallKey "Software\Microsoft\Windows\CurrentVersion\Uninstall\{" + AppId + "}_is1"

[Setup]
AppId={{{#AppId}}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=MoFeng2223
AppPublisherURL=https://github.com/MoFeng2223/BrimDeck
AppSupportURL=https://github.com/MoFeng2223/BrimDeck/issues
AppUpdatesURL=https://github.com/MoFeng2223/BrimDeck/releases
AppCopyright=Copyright (C) MoFeng2223
VersionInfoVersion={#FileVersion}
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#MainExe}
; Installs for the current user by default, so in-app updates run without an administrator prompt.
; The first dialog lets the user install for all users instead; later updates keep the chosen mode and folder.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
DefaultDirName={autopf}\{#AppName}
; Missing folders are created without asking. A reinstall starts at the previous folder and may choose another one,
; because the installed version is removed first (PrepareToInstall).
DisableDirPage=no
DisableReadyPage=yes
DisableProgramGroupPage=yes
MinVersion=10.0.22000
SetupArchitecture=x64
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern dynamic
SetupIconFile=..\src\BrimDeck\Assets\BrimDeck.ico
RestartApplications=no
; Writes "Setup Log <date> #nnn.txt" to %TEMP% for diagnosing failed installs and updates.
SetupLogging=yes
Compression={#Compression}
SolidCompression=yes
OutputBaseFilename={#AppName}-{#AppVersion}-Setup
; The language follows the Windows display language without asking: Chinese, Simplified or Traditional, shows
; Simplified Chinese and anything else English. A reinstall does not reuse the language of the earlier installer.
ShowLanguageDialog=no
UsePreviousLanguage=no

; English comes first, so a display language without a match falls back to it. Setup never matches a Traditional
; Chinese display language to a Simplified Chinese entry (it compares the code pages, 950 and 936), so "cht" carries
; Traditional Chinese's language ID and shows the Simplified Chinese texts.
[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "chs"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "cht"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[LangOptions]
cht.LanguageID=$0404

; Entries without a prefix are the Chinese texts for chs and cht; the en. entries after them replace them in English.
[CustomMessages]
ExitFirstTitle=请先退出 {#AppName}
ExitFirstText={#AppName} 正在运行。请右键点击任务栏通知区域中的 {#AppName} 图标，选择“退出”，然后点击“重试”。
DesktopShortcut=创建桌面快捷方式
StartMenuShortcut=创建开始菜单快捷方式
OldVersionNotRemoved=无法卸载已安装的旧版本 {#AppName}（代码 %1）。请在 Windows 设置的“应用”中卸载后，重新运行安装程序。
UninstallTitle=卸载 {#AppName}
UninstallQuestion=确定要卸载 {#AppName} 吗？
UninstallButton=卸载
DeleteUserData=删除用户数据
Uninstalled={#AppName} 已卸载。
en.ExitFirstTitle=Quit {#AppName} first
en.ExitFirstText={#AppName} is running. Right-click the {#AppName} icon in the notification area of the taskbar, choose "Exit", then click "Retry".
en.DesktopShortcut=Create a desktop shortcut
en.StartMenuShortcut=Create a Start menu shortcut
en.OldVersionNotRemoved=The installed version of {#AppName} could not be uninstalled (code %1). Uninstall it from Apps in Windows Settings, then run Setup again.
en.UninstallTitle=Uninstall {#AppName}
en.UninstallQuestion=Do you want to uninstall {#AppName}?
en.UninstallButton=Uninstall
en.DeleteUserData=Delete user data
en.Uninstalled={#AppName} was uninstalled.

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

; Both shortcuts are created during installation so the uninstaller tracks them. The finished page removes the
; ones the user unchecks (FinishShortcuts).
[Icons]
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#MainExe}"; AppUserModelID: "{#AppUserModelId}"
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#MainExe}"; AppUserModelID: "{#AppUserModelId}"

[Run]
; An in-app update runs Setup silently with /RELAUNCH after BrimDeck has exited.
Filename: "{app}\{#MainExe}"; Flags: nowait runasoriginaluser; Check: RelaunchRequested

[Code]
var
  DesktopShortcut, StartMenuShortcut: Boolean;
  DesktopCheck, StartMenuCheck, LaunchCheck: TNewCheckBox;
  DeleteUserData: Boolean;

function HasParam(const Name: String): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), Name) = 0 then
    begin
      Result := True;
      Exit;
    end;
end;

function RelaunchRequested: Boolean;
begin
  Result := HasParam('/RELAUNCH');
end;

function AppRunning: Boolean;
begin
  Result := CheckForMutexes('Local\{#MutexPrefix}' + GetUserNameString);
end;

// Silent runs come from an in-app update, which starts Setup and then exits, so they wait for that exit.
// Interactive runs ask the user to quit BrimDeck from the tray menu.
function EnsureAppClosed(Silent: Boolean): Boolean;
var
  Waited: Integer;
begin
  Result := True;
  Waited := 0;
  while AppRunning do
  begin
    if Silent then
    begin
      if Waited >= 30000 then
      begin
        Result := False;
        Exit;
      end;
      Sleep(250);
      Waited := Waited + 250;
    end
    else if TaskDialogMsgBox(CustomMessage('ExitFirstTitle'), CustomMessage('ExitFirstText'),
      mbError, MB_RETRYCANCEL, [], 0) = IDCANCEL then
    begin
      Result := False;
      Exit;
    end;
  end;
end;

// ----- Setup -----

// A first install defaults to a desktop shortcut and no Start menu shortcut. When a version is already installed,
// PrepareToInstall replaces these with the shortcuts that exist at that moment.
function InitializeSetup: Boolean;
begin
  Result := EnsureAppClosed(WizardSilent);
  DesktopShortcut := True;
  StartMenuShortcut := False;
end;

function AddCheck(const Caption: String; Checked: Boolean): TNewCheckBox;
begin
  Result := TNewCheckBox.Create(WizardForm);
  Result.Parent := WizardForm.FinishedPage;
  Result.Caption := Caption;
  Result.Checked := Checked;
end;

procedure InitializeWizard;
begin
  DesktopCheck := AddCheck(CustomMessage('DesktopShortcut'), DesktopShortcut);
  StartMenuCheck := AddCheck(CustomMessage('StartMenuShortcut'), StartMenuShortcut);
  LaunchCheck := AddCheck(ExpandConstant('{cm:LaunchProgram,{#AppName}}'), True);
end;

procedure CurPageChanged(CurPageID: Integer);
var
  Top: Integer;
begin
  // The ready page is skipped, so the folder page starts the installation.
  if CurPageID = wpSelectDir then
    WizardForm.NextButton.Caption := SetupMessage(msgButtonInstall)
  else if CurPageID = wpFinished then
  begin
    Top := WizardForm.FinishedLabel.Top + WizardForm.FinishedLabel.Height + ScaleY(16);
    DesktopCheck.SetBounds(WizardForm.FinishedLabel.Left, Top, WizardForm.FinishedLabel.Width, ScaleY(22));
    StartMenuCheck.SetBounds(WizardForm.FinishedLabel.Left, Top + ScaleY(28), WizardForm.FinishedLabel.Width, ScaleY(22));
    LaunchCheck.SetBounds(WizardForm.FinishedLabel.Left, Top + ScaleY(56), WizardForm.FinishedLabel.Width, ScaleY(22));
  end;
end;

// A typed or browsed folder always ends in \BrimDeck, whether or not it was written with a trailing / or \.
function NormalizeAppDir(Dir: String): String;
begin
  Result := Trim(Dir);
  StringChangeEx(Result, '/', '\', True);
  while (Length(Result) > 3) and (Result[Length(Result)] = '\') do
    SetLength(Result, Length(Result) - 1);
  if CompareText(ExtractFileName(Result), '{#AppName}') <> 0 then
    Result := AddBackslash(Result) + '{#AppName}';
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = wpSelectDir then
    WizardForm.DirEdit.Text := NormalizeAppDir(WizardForm.DirEdit.Text);
end;

// Returns <path> from a command line such as "<path>\unins000.exe" /SILENT /UI.
// Pos disagrees with Copy on paths with Chinese characters, so the closing quote is found character by character.
function QuotedPath(const Command: String): String;
var
  I: Integer;
begin
  Result := Command;
  if (Length(Command) = 0) or (Command[1] <> '"') then
    Exit;
  Result := '';
  for I := 2 to Length(Command) do
  begin
    if Command[I] = '"' then
      Exit;
    Result := Result + Command[I];
  end;
end;

// Runs the uninstaller of the installed version before any file is copied, so no file of the old version remains.
// /UPGRADE keeps settings, data and the startup entry; the installer waits until that uninstall has finished.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Uninstaller: String;
  ResultCode, Waited: Integer;
begin
  Result := '';
  if not RegQueryStringValue(HKA, '{#UninstallKey}', 'UninstallString', Uninstaller) then
  begin
    Log('No installed version found.');
    Exit;
  end;
  // The uninstaller below deletes both shortcuts, including ones the user has kept, so record which exist now and
  // restore exactly those in FinishShortcuts. A shortcut the user deleted stays deleted.
  DesktopShortcut := FileExists(ExpandConstant('{autodesktop}\{#AppName}.lnk'));
  StartMenuShortcut := FileExists(ExpandConstant('{autoprograms}\{#AppName}.lnk'));
  if not WizardSilent then
  begin
    DesktopCheck.Checked := DesktopShortcut;
    StartMenuCheck.Checked := StartMenuShortcut;
  end;
  Uninstaller := QuotedPath(Uninstaller);
  if not FileExists(Uninstaller) then
  begin
    Log('Uninstaller of the installed version is missing: ' + Uninstaller);
    Exit;
  end;
  Log('Removing the installed version: ' + Uninstaller);
  if not Exec(Uninstaller, '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /UPGRADE', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
  begin
    Log('Uninstall failed with code ' + IntToStr(ResultCode));
    Result := FmtMessage(CustomMessage('OldVersionNotRemoved'), [IntToStr(ResultCode)]);
    Exit;
  end;
  // The old uninstaller deletes itself shortly after it exits. Waiting lets the new one reuse the name unins000.exe.
  Waited := 0;
  while FileExists(Uninstaller) and (Waited < 10000) do
  begin
    Sleep(200);
    Waited := Waited + 200;
  end;
end;

// Keeps the checked shortcuts and starts BrimDeck when requested.
// Silent installs (in-app updates) keep the shortcuts that existed before the update.
procedure FinishShortcuts;
var
  ResultCode: Integer;
begin
  if not WizardSilent then
  begin
    DesktopShortcut := DesktopCheck.Checked;
    StartMenuShortcut := StartMenuCheck.Checked;
  end;
  if not DesktopShortcut then
    DeleteFile(ExpandConstant('{autodesktop}\{#AppName}.lnk'));
  if not StartMenuShortcut then
    DeleteFile(ExpandConstant('{autoprograms}\{#AppName}.lnk'));
  if not WizardSilent and LaunchCheck.Checked then
    ExecAsOriginalUser(ExpandConstant('{app}\{#MainExe}'), '', '', SW_SHOWNORMAL, ewNoWait, ResultCode);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  // Settings > Apps and Control Panel run UninstallString. /SILENT replaces the built-in confirmation and completion
  // messages with the dialog below; /UI marks that run as interactive.
  if CurStep = ssPostInstall then
    RegWriteStringValue(HKA, '{#UninstallKey}', 'UninstallString', '"' + ExpandConstant('{uninstallexe}') + '" /SILENT /UI')
  else if CurStep = ssDone then
    FinishShortcuts;
end;

// ----- Uninstall -----

// Laid out like the Windows confirmation box: a question icon and the question in the white area, and a grey
// footer holding the "delete user data" box on the left (as a task dialog's verification check box) and the
// buttons on the right.
// Sizes are in units of 100% display scaling. CreateCustomForm converts its client size to the current scaling
// itself (measured on 7.1.0: 250 became 695 px at a ScaleX factor of 1.667 when passed through ScaleX first), so
// the form size is passed unscaled while child controls use ScaleX/ScaleY. The form keeps this size instead of
// growing with WizardSizePercent.
function ConfirmUninstall: Boolean;
var
  Form: TSetupForm;
  Icon: TBitmapImage;
  Prompt: TNewStaticText;
  Footer: TPanel;
  DeleteCheck: TNewCheckBox;
  UninstallButton, CancelButton: TNewButton;
  ButtonWidth, ContentHeight: Integer;
begin
  Form := CreateCustomForm(310, 128, True, True);
  try
    Form.Caption := CustomMessage('UninstallTitle');
    // Task dialog colors. The style engine would otherwise paint both areas the same.
    Form.StyleElements := [seFont, seBorder];
    Footer := TPanel.Create(Form);
    Footer.Parent := Form;
    Footer.BevelOuter := bvNone;
    Footer.ParentBackground := False;
    Footer.StyleElements := [seFont, seBorder];
    if IsDarkInstallMode then
    begin
      Form.Color := $2B2B2B;
      Footer.Color := $202020;
    end
    else
    begin
      Form.Color := $FFFFFF;
      Footer.Color := $F0F0F0;
    end;
    Footer.SetBounds(0, Form.ClientHeight - ScaleY(50), Form.ClientWidth, ScaleY(50));
    ContentHeight := Footer.Top;

    Icon := TBitmapImage.Create(Form);
    Icon.Parent := Form;
    Icon.SetBounds(ScaleX(20), (ContentHeight - ScaleY(24)) div 2, ScaleX(24), ScaleY(24));
    InitializeBitmapImageFromStockIcon(Icon, 23 { SIID_HELP }, Form.Color, [16, 20, 24, 32, 40, 48, 64]);
    Prompt := TNewStaticText.Create(Form);
    Prompt.Parent := Form;
    Prompt.Caption := CustomMessage('UninstallQuestion');
    Prompt.Left := Icon.Left + Icon.Width + ScaleX(12);
    Prompt.Top := (ContentHeight - Prompt.Height) div 2;

    CancelButton := TNewButton.Create(Form);
    CancelButton.Parent := Footer;
    CancelButton.Caption := SetupMessage(msgButtonCancel);
    CancelButton.ModalResult := mrCancel;
    CancelButton.Cancel := True;
    UninstallButton := TNewButton.Create(Form);
    UninstallButton.Parent := Footer;
    UninstallButton.Caption := CustomMessage('UninstallButton');
    UninstallButton.ModalResult := mrOk;
    UninstallButton.Default := True;
    ButtonWidth := Form.CalculateButtonWidth([UninstallButton.Caption, CancelButton.Caption]);
    CancelButton.SetBounds(Footer.Width - ScaleX(12) - ButtonWidth, (Footer.Height - ScaleY(23)) div 2, ButtonWidth, ScaleY(23));
    UninstallButton.SetBounds(CancelButton.Left - ScaleX(8) - ButtonWidth, CancelButton.Top, ButtonWidth, ScaleY(23));
    DeleteCheck := TNewCheckBox.Create(Form);
    DeleteCheck.Parent := Footer;
    DeleteCheck.Caption := CustomMessage('DeleteUserData');
    DeleteCheck.Checked := False;
    DeleteCheck.SetBounds(ScaleX(20), (Footer.Height - ScaleY(19)) div 2, UninstallButton.Left - ScaleX(28), ScaleY(19));
    Form.ActiveControl := UninstallButton;
    Result := Form.ShowModal = mrOk;
    DeleteUserData := DeleteCheck.Checked;
  finally
    Form.Free;
  end;
end;

function InitializeUninstall: Boolean;
begin
  Result := EnsureAppClosed(UninstallSilent and not HasParam('/UI'));
  DeleteUserData := HasParam('/DELETEUSERDATA');
  if Result and HasParam('/UI') then
    Result := ConfirmUninstall;
end;

procedure RemoveStartupEntry;
var
  Command: String;
begin
  if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', '{#RunValue}', Command) and
    (Pos(Lowercase(AddBackslash(ExpandConstant('{app}'))), Lowercase(Command)) > 0) then
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', '{#RunValue}');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usUninstall) and not HasParam('/UPGRADE') then
    RemoveStartupEntry
  else if (CurUninstallStep = usPostUninstall) and DeleteUserData and not HasParam('/UPGRADE') then
    DelTree(ExpandConstant('{#DataDir}'), True, True, True)
  else if (CurUninstallStep = usDone) and HasParam('/UI') then
    MsgBox(CustomMessage('Uninstalled'), mbInformation, MB_OK);
end;
