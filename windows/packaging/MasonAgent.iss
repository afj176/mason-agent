; Dark mode (WizardStyle "dynamic") and .png wizard images need Inno Setup 6.6 or later.
#if Ver < EncodeVer(6,6,0)
  #error Inno Setup 6.6 or later is required to build this installer.
#endif
[Setup]
AppId=MasonAgent.Desktop
AppName=Mason Agent
AppMutex=Local\MasonAgent.Desktop.App
AppVersion={#AppVersion}
AppPublisher=Mason Agent
DefaultDirName={localappdata}\Programs\MasonAgent
DefaultGroupName=Mason Agent
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible and not arm64
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
DisableProgramGroupPage=yes
; Follows the Windows light or dark setting. The first and last pages show the welcome and installed poses;
; the inner pages draw a pose band at their left ([Code] below). Band colours are Blueprint's panel colours.
WizardStyle=modern dynamic
DisableWelcomePage=no
WizardImageFile=art\welcome-100.png,art\welcome-150.png,art\welcome-200.png
; Without this, dark mode swaps in Inno's own dark wizard image.
WizardImageFileDynamicDark=art\welcome-100.png,art\welcome-150.png,art\welcome-200.png
WizardSmallImageFile=
WizardSmallImageFileDynamicDark=
WizardImageBackColor=#E6EAF7
WizardImageBackColorDynamicDark=#212A44
SetupIconFile=..\src\MasonAgent\Assets\Brand\app.ico
OutputDir={#OutputPath}
OutputBaseFilename=MasonAgent-{#AppVersion}-Setup-x64
Compression=lzma2/fast
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
UninstallDisplayIcon={app}\MasonAgent.exe
; Setup tells running programs that PATH changed, so new terminals see the mason command at once.
ChangesEnvironment=yes
[Messages]
WelcomeLabel2=This installs [name/ver] for your Windows account only. No administrator rights are needed.%n%nMason Agent is a coding agent that works with models running on this PC.
FinishedLabel=Setup has finished installing [name]. If you added the mason command to PATH, open a new terminal and type mason to use it there.
[Tasks]
Name: "clipath"; Description: "Add the mason command to PATH (use Mason Agent from any terminal)"
[Files]
; The pre-publish payload check rejects root-level profile directories. Nested data
; directories belong to the shipped language tools and must match the release manifest.
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "\portable.flag"
; Pose art for the wizard pages, extracted to a temporary folder only while Setup runs.
Source: "art\*.png"; Flags: dontcopy
[Icons]
Name: "{group}\Mason Agent"; Filename: "{app}\MasonAgent.exe"; AppUserModelID: "MasonAgent.Desktop"
[Run]
; Opened through Explorer, not by Setup itself. Inno Setup 6 passes Setup's RedirectionGuard protection on to programs
; it starts, and with it Windows refuses the junctions and symlinks model folders use (error 448, "untrusted mount
; point"): Models would report "Windows blocked access through local model links" until Mason was reopened from Start.
; Started by Explorer, Mason runs exactly as it does from the Start menu.
Filename: "{win}\explorer.exe"; Parameters: """{app}\MasonAgent.exe"""; Description: "Open Mason Agent"; Flags: nowait postinstall skipifsilent
[InstallDelete]
; Files earlier releases installed that this one no longer ships: the WebView2 libraries, Windows AI /
; ONNX Runtime / DirectML files the full Windows App SDK package brought in, and retired assets. Setup
; only adds and replaces files, so without these entries an upgrade would leave them in place. Only
; paths inside {app}; the user's profile lives elsewhere and is never touched.
Type: filesandordirs; Name: "{app}\NpuDetect"
Type: files; Name: "{app}\DirectML.dll"
Type: files; Name: "{app}\Mason.Code.Core.pdb"
Type: files; Name: "{app}\Mason.NetworkHook.pdb"
Type: files; Name: "{app}\MasonAgent.pdb"
Type: files; Name: "{app}\Microsoft.Graphics.Imaging.dll"
Type: files; Name: "{app}\Microsoft.Graphics.Imaging.Projection.dll"
Type: files; Name: "{app}\Microsoft.Graphics.Imaging.winmd"
Type: files; Name: "{app}\Microsoft.Graphics.ImagingInternal.ImageObjectRemover.winmd"
Type: files; Name: "{app}\Microsoft.Graphics.ImagingInternal.winmd"
Type: files; Name: "{app}\Microsoft.Graphics.Internal.Imaging.winmd"
Type: files; Name: "{app}\Microsoft.ML.OnnxRuntime.dll"
Type: files; Name: "{app}\Microsoft.Web.WebView2.Core.dll"
Type: files; Name: "{app}\Microsoft.Web.WebView2.Core.Projection.dll"
Type: files; Name: "{app}\Microsoft.Windows.AI.AICapabilities.dll"
Type: files; Name: "{app}\Microsoft.Windows.AI.ContentModerationInternal.winmd"
Type: files; Name: "{app}\Microsoft.Windows.AI.ContentSafety.dll"
Type: files; Name: "{app}\Microsoft.Windows.AI.ContentSafety.Projection.dll"
Type: files; Name: "{app}\Microsoft.Windows.AI.ContentSafety.winmd"
Type: files; Name: "{app}\Microsoft.Windows.AI.Foundation.Projection.dll"
Type: files; Name: "{app}\Microsoft.Windows.AI.Foundation.winmd"
Type: files; Name: "{app}\Microsoft.Windows.AI.FoundationInternal.winmd"
Type: files; Name: "{app}\Microsoft.Windows.AI.GenerativeInternal.winmd"
Type: files; Name: "{app}\Microsoft.Windows.AI.Imaging.dll"
Type: files; Name: "{app}\Microsoft.Windows.AI.Imaging.Projection.dll"
Type: files; Name: "{app}\Microsoft.Windows.AI.Imaging.winmd"
Type: files; Name: "{app}\Microsoft.Windows.AI.MachineLearning.dll"
Type: files; Name: "{app}\Microsoft.Windows.AI.MachineLearning.Projection.dll"
Type: files; Name: "{app}\Microsoft.Windows.AI.MachineLearning.winmd"
Type: files; Name: "{app}\Microsoft.Windows.AI.Projection.dll"
Type: files; Name: "{app}\Microsoft.Windows.AI.Text.dll"
Type: files; Name: "{app}\Microsoft.Windows.AI.Text.Projection.dll"
Type: files; Name: "{app}\Microsoft.Windows.AI.Text.winmd"
Type: files; Name: "{app}\Microsoft.Windows.AI.winmd"
Type: files; Name: "{app}\Microsoft.Windows.Internal.Vision.winmd"
Type: files; Name: "{app}\Microsoft.Windows.Private.Workloads.SessionManager.winmd"
Type: files; Name: "{app}\Microsoft.Windows.PrivateCommon.winmd"
Type: files; Name: "{app}\Microsoft.Windows.SemanticSearch.winmd"
Type: files; Name: "{app}\Microsoft.Windows.Vision.winmd"
Type: files; Name: "{app}\Microsoft.Windows.VisionInternal.winmd"
Type: files; Name: "{app}\Microsoft.Windows.Widgets.dll"
Type: files; Name: "{app}\Microsoft.Windows.Widgets.Projection.dll"
Type: files; Name: "{app}\Microsoft.Windows.Widgets.winmd"
Type: files; Name: "{app}\Microsoft.Windows.Workloads.dll"
Type: files; Name: "{app}\Microsoft.Windows.Workloads.Resources.dll"
Type: files; Name: "{app}\Microsoft.Windows.Workloads.Resources_ec.dll"
Type: files; Name: "{app}\Microsoft.Windows.Workloads.winmd"
Type: files; Name: "{app}\Microsoft.WindowsAppRuntime.Insights.Resource.dll"
Type: files; Name: "{app}\onnxruntime.dll"
Type: files; Name: "{app}\onnxruntime_providers_shared.dll"
Type: files; Name: "{app}\sandbox-setup\Mason.SandboxSetup.pdb"
Type: files; Name: "{app}\SessionHandleIPCProxyStub.dll"
Type: files; Name: "{app}\System.Numerics.Tensors.dll"
Type: files; Name: "{app}\WebView2Loader.dll"
Type: files; Name: "{app}\WindowsAppRuntime.DeploymentExtensions.OneCore.dll"
Type: files; Name: "{app}\WindowsAppRuntime.png"
Type: files; Name: "{app}\WindowsAppSdk.AppxDeploymentExtensions.Desktop-EventLog-Instrumentation.dll"
Type: files; Name: "{app}\WindowsAppSdk.AppxDeploymentExtensions.Desktop.dll"
Type: files; Name: "{app}\workloads.365.json"
Type: files; Name: "{app}\workloads.j32.json"
Type: files; Name: "{app}\workloads.json"
Type: files; Name: "{app}\workloads.lnl.json"
Type: files; Name: "{app}\workloads.qnn.json"
Type: files; Name: "{app}\workloads.stx.json"
; Files that earlier releases installed under their former names, and a profile import script that is no longer shipped.
Type: files; Name: "{app}\DbCowboy.Code.Core.*"
Type: files; Name: "{app}\DbCowboy.NetworkHook.*"
Type: files; Name: "{app}\sandbox-setup\DbCowboy.SandboxSetup.*"
Type: files; Name: "{app}\native-handoff\DbCowboy.BashBootstrap.exe"
Type: files; Name: "{app}\Import-DbCowboyProfile.ps1"
; Logo and mascot images earlier versions installed; the app now shows the app icon and the poses in Assets\Poses.
Type: files; Name: "{app}\Assets\Brand\logo-dark.png"
Type: files; Name: "{app}\Assets\Brand\logo-light.png"
Type: files; Name: "{app}\Assets\Brand\symbol.png"
Type: files; Name: "{app}\Assets\Brand\mascot.png"
Type: files; Name: "{app}\Assets\Brand\wordmark-dark.png"
Type: files; Name: "{app}\Assets\Brand\wordmark-light.png"
Type: filesandordirs; Name: "{app}\Assets\Brand\mascot"
Type: filesandordirs; Name: "{app}\Assets\Brand\poses"
[Code]
// The mason command: {app}\bin holds only the CLI launcher. It is added to this user's PATH when the
// task is selected and removed on uninstall. Entries are compared case-insensitively and other entries are kept as they are.
const EnvironmentKey = 'Environment';

function WithoutEntry(Paths, Entry: string): string;
var
  Rest, Item, Kept: string;
  P: Integer;
begin
  Rest := Paths; Kept := '';
  while Rest <> '' do
  begin
    P := Pos(';', Rest);
    if P = 0 then begin Item := Rest; Rest := ''; end
    else begin Item := Copy(Rest, 1, P - 1); Rest := Copy(Rest, P + 1, Length(Rest)); end;
    if (Item <> '') and (CompareText(RemoveBackslashUnlessRoot(Item), RemoveBackslashUnlessRoot(Entry)) <> 0) then
    begin
      if Kept <> '' then Kept := Kept + ';';
      Kept := Kept + Item;
    end;
  end;
  Result := Kept;
end;

procedure SetCliPath(Add: Boolean);
var
  Paths, Entry, Updated: string;
begin
  Entry := ExpandConstant('{app}\bin');
  if not RegQueryStringValue(HKEY_CURRENT_USER, EnvironmentKey, 'Path', Paths) then Paths := '';
  Updated := WithoutEntry(Paths, Entry);
  if Add then
  begin
    if Updated <> '' then Updated := Updated + ';';
    Updated := Updated + Entry;
  end;
  if Updated <> Paths then RegWriteExpandStringValue(HKEY_CURRENT_USER, EnvironmentKey, 'Path', Updated);
end;

// Pose art: the inner pages get a band at their left with the pose for that step. Images come at 100, 150
// and 200% scale (art\make_installer_art.py); the closest one to the display scale is drawn unscaled.
var
  PoseBand: TBitmapImage;

function ArtFile(Pose: string): string;
var
  Name: string;
begin
  if ScaleX(100) >= 175 then Name := Pose + '-200.png'
  else if ScaleX(100) >= 125 then Name := Pose + '-150.png'
  else Name := Pose + '-100.png';
  Result := ExpandConstant('{tmp}\' + Name);
  if not FileExists(Result) then ExtractTemporaryFile(Name);
end;

procedure InitializeWizard;
var
  Right, Gap: Integer;
begin
  PoseBand := TBitmapImage.Create(WizardForm);
  PoseBand.Parent := WizardForm.InnerPage;
  PoseBand.Left := 0;
  PoseBand.Top := WizardForm.Bevel1.Top + WizardForm.Bevel1.Height;
  PoseBand.Width := ScaleX(150);
  PoseBand.Height := WizardForm.InnerPage.ClientHeight - PoseBand.Top;
  PoseBand.Anchors := [akLeft, akTop, akBottom];
  PoseBand.Center := True;
  if IsDarkInstallMode then PoseBand.BackColor := StrToColor('#212A44') else PoseBand.BackColor := StrToColor('#E6EAF7');
  PoseBand.Visible := False;
  // The page content moves right of the band and keeps its right edge.
  Gap := ScaleX(20);
  Right := WizardForm.InnerNotebook.Left + WizardForm.InnerNotebook.Width;
  WizardForm.InnerNotebook.Left := PoseBand.Width + Gap;
  WizardForm.InnerNotebook.Width := Right - WizardForm.InnerNotebook.Left;
  // The finished page shows the installed pose; the welcome page uses WizardImageFile.
  WizardForm.WizardBitmapImage2.PngImage.LoadFromFile(ArtFile('installed'));
end;

procedure CurPageChanged(CurPageID: Integer);
var
  Pose: string;
begin
  Pose := '';
  if CurPageID = wpSelectDir then Pose := 'choose-location'
  else if (CurPageID = wpSelectTasks) or (CurPageID = wpReady) then Pose := 'ready-to-install'
  // Preparing appears only when something needs the user, such as Mason Agent still running.
  else if CurPageID = wpPreparing then Pose := 'needs-attention'
  else if CurPageID = wpInstalling then Pose := 'installing';
  PoseBand.Visible := Pose <> '';
  if Pose <> '' then PoseBand.PngImage.LoadFromFile(ArtFile(Pose));
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then SetCliPath(WizardIsTaskSelected('clipath'));
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then SetCliPath(False);
end;
