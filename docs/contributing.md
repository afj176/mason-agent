# Building from source

Each platform is its own native project and builds on its own:

| Folder | What it holds |
| --- | --- |
| [`windows/`](../windows) | The WinUI 3 app, the C# engine and `mason`, tests and installer |
| [`macos/`](../macos) | The Swift package: engine, `mason`, the SwiftUI app, tests and packaging |
| [`linux/`](../linux) | The Meson project: C++ engine, `mason`, the GTK 4 app, tests and packaging |
| [`site/`](../site) | The website |

## Windows

You need Windows 10 or 11 x64 and the [.NET 10 SDK](https://dotnet.microsoft.com/download). Release builds also need Visual Studio 2022 or its Build Tools with the C++ x64 tools, and the installer needs [Inno Setup](https://jrsoftware.org/isinfo.php) 6.6 or later.

```powershell
cd windows
dotnet build src\MasonAgent\MasonAgent.csproj -c Debug -p:Platform=x64
dotnet run --project tests\Mason.CodeChecks -c Release
.\scripts\Publish.ps1 -Version X.Y.Z -Installer -InnoCompiler "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
```

Set `MASON_DATA_HOME` to a scratch folder while testing, so your real profile is left alone.

## macOS

On a Mac with Xcode's command line tools:

```sh
cd macos
swift test
Scripts/build-app.sh        # app, .zip and .pkg in macos/dist
```

## Linux

On Ubuntu 24.04:

```sh
sudo apt install build-essential meson ninja-build pkg-config libcurl4-openssl-dev nlohmann-json3-dev libgtkmm-4.0-dev libadwaita-1-dev libatspi2.0-dev bubblewrap dpkg-dev
cd linux
meson setup builddir && meson compile -C builddir && meson test -C builddir
```

`linux/packaging/build-packages.sh` builds the `.deb` and the archive into `linux/dist`.

## Releases

Every pull request merged into `main` is released. Its label sets the version: `release: patch` (the default), `release: minor`, `release: major`, or `release: skip` for changes that ship nothing.
