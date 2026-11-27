# Building and releasing

## Folders

Each platform lives in its own folder and builds on its own, with nothing shared but the docs and this README:

| Folder | What it holds |
| --- | --- |
| [`windows/`](../windows) | The WinUI 3 app, the C# engine and `mason`, their checks, build scripts and installer |
| [`macos/`](../macos) | The Swift package: engine, `mason`, the SwiftUI app, tests and packaging |
| [`linux/`](../linux) | The Meson project: C++ engine, `mason`, the GTK 4 app, tests and packaging |
| [`site/`](../site), [`docs/`](.) | The website and shared documentation |

## Versions and releases

Each platform keeps its version in its own folder: `windows/src/MasonAgent/MasonAgent.csproj`, `macos/Sources/MasonCore/Version.swift` and `linux/meson.build`. They are released together, so `scripts/version.sh` reads and changes all three: run it alone to print the version, with `set 2.3.0`, or with `bump patch`.

Every pull request merged into `main` is a release. Its label sets the version:

| Label | Version | |
| --- | --- | --- |
| `release: patch` | 2.2.1 → 2.2.2 | the default with no label |
| `release: minor` | 2.2.1 → 2.3.0 | new features |
| `release: major` | 2.2.1 → 3.0.0 | breaking changes |
| `release: skip` | unchanged | documentation, CI, no release |

On merge, `release-on-merge.yml` commits the new version to `main`, tags it `vX.Y.Z` and builds and publishes Windows, macOS and Linux to that release. The website and the download links read the latest release.

## Build from source

### Windows

Prerequisites:

- Windows 10/11 x64 and the [.NET 10 SDK](https://dotnet.microsoft.com/download). The Windows App SDK and Windows SDK build tools come from NuGet.
- For release builds: Visual Studio 2022 or later (or Build Tools) with the **C++ x64 build tools** for the native coding helpers, and Git for Windows.
- For the installer: [Inno Setup 6](https://jrsoftware.org/isinfo.php).

```powershell
cd windows

# Run a Debug build (use MASON_DATA_HOME to keep test data away from your real profile)
dotnet build src\MasonAgent\MasonAgent.csproj -c Debug -p:Platform=x64

# Release payload and ZIP in windows\artifacts\MasonAgent-<version>-win-x64.
# Omit -Installer and -InnoCompiler for the portable package alone.
.\scripts\Publish.ps1 -Version X.Y.Z -Installer -InnoCompiler "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"

# Optional: try the release against your own local models first, and ship what they did with it.
.\scripts\Invoke-ModelBenchmark.ps1 -Endpoint http://127.0.0.1:8080 -Model YOUR_LOADED_MODEL -OutputDirectory .cache\qualification
.\scripts\Invoke-WorkflowQualification.ps1 -Endpoint http://127.0.0.1:8080 -Model YOUR_LOADED_MODEL -SecondaryEndpoint http://127.0.0.1:8081 -SecondaryModel SECOND_MODEL -OutputDirectory .cache\workflow
.\scripts\Publish.ps1 -Version X.Y.Z -ModelEvidence .cache\qualification\report.json -WorkflowEvidence .cache\workflow\workflow-evidence.json
```

`Publish.ps1` takes the current version (`bash scripts/version.sh` prints it), requires a clean checkout and runs the scripted checks, which need no model. The release on GitHub does the same; no model runs there. The two model scripts are optional and run only on your own computer: when you pass their evidence, `Publish.ps1` checks it is for the exact source commit and ships it with the release. In them, each model builds a store from an empty directory four times, reviews a working store and six single-defect variants, and repairs a separate JavaScript application during a concurrent edit. Edge exercises discount boundaries and two checkouts at phone and desktop widths. Both packages include the two reports and their SHA-256 hashes. Configure two running local llama.cpp servers on the Windows runner labeled `mason-model-evals` using `MASON_EVAL_ENDPOINT`, `MASON_EVAL_MODEL`, `MASON_SECONDARY_EVAL_ENDPOINT` and `MASON_SECONDARY_EVAL_MODEL`.

#### Checks

`windows/tests/Mason.CodeChecks` covers the coding engine, projects and sessions, shells, the Windows sandbox and its network denial. The sandbox checks need a small native probe, built with the MSVC tools:

```powershell
cd windows
.\scripts\Build-CodeSandboxProbe.ps1
$env:MASON_SANDBOX_PROBE = (Resolve-Path .cache\code-sandbox-probe\SandboxNativeProbe.exe)
dotnet run --project tests\Mason.CodeChecks -c Release
```

The default run uses scripted model responses and needs no model. The `--evaluate-*` modes in `windows/tests/Mason.CodeChecks/Program.cs` run real coding tasks and need a local model server.

### macOS

On a Mac with Xcode's command line tools:

```sh
cd macos
swift test                  # engine tests, including real commands under the sandbox
Scripts/build-app.sh        # universal app, .zip and .pkg in macos/dist
```

`MasonCore` and the `mason` tool use Foundation only, so they also build and pass their tests on Linux with the Swift toolchain. The SwiftUI app builds on macOS only. The [macOS workflow](../.github/workflows/macos.yml) runs the tests, builds the packages, installs the `.pkg` and runs [`Scripts/check-installed.sh`](../macos/Scripts/check-installed.sh) on a Mac runner.

### Linux

On Ubuntu 24.04:

```sh
sudo apt install build-essential meson ninja-build pkg-config libcurl4-openssl-dev nlohmann-json3-dev libgtkmm-4.0-dev libadwaita-1-dev libatspi2.0-dev bubblewrap dpkg-dev
cd linux
meson setup builddir && meson compile -C builddir && meson test -C builddir
./builddir/mason                # the command line
./builddir/mason-agent          # the app
```

`linux/packaging/build-packages.sh` builds the `.deb` and the portable archive into `linux/dist`. `-Dapp=false` builds only the engine and `mason`, without GTK. [`linux/packaging/Dockerfile.build`](../linux/packaging/Dockerfile.build) is the same environment as a container, and the [Linux workflow](../.github/workflows/linux.yml) builds, tests, packages and starts the app on each change. The sandbox tests run real commands under bubblewrap where it can create a sandbox, and otherwise check that Auto refuses.
