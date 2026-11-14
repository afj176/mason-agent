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
WizardStyle=modern
SetupIconFile=..\src\MasonAgent\Assets\Brand\app.ico
OutputDir={#OutputPath}
OutputBaseFilename=MasonAgent-{#AppVersion}-Setup-x64
Compression=lzma2/fast
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
UninstallDisplayIcon={app}\MasonAgent.exe
[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "portable.flag,data\*,ui-qa-data\*"
[Icons]
Name: "{group}\Mason Agent"; Filename: "{app}\MasonAgent.exe"; AppUserModelID: "MasonAgent.Desktop"
[Run]
Filename: "{app}\MasonAgent.exe"; Description: "Open Mason Agent"; Flags: nowait postinstall skipifsilent
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
; The letter-mark logo images were replaced by the mascot lockup in 1.1.
Type: files; Name: "{app}\Assets\Brand\logo-dark.png"
Type: files; Name: "{app}\Assets\Brand\logo-light.png"
Type: files; Name: "{app}\Assets\Brand\symbol.png"
