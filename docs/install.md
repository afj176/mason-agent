# Install details

The [README](../README.md#install) has the short steps. This page has the rest: model options, where settings live and what each package needs.

## Windows

Download from the [latest release](https://github.com/afj176/mason-agent/releases/latest):

- **Installer** (`MasonAgent-Setup-x64.exe`, about 106 MB): installs for your Windows account into `%LOCALAPPDATA%\Programs\MasonAgent`; no administrator rights needed. Its pages are illustrated and follow Windows light or dark mode. From version 2.2.1 it can also add the `mason` command to your PATH. Requires Windows 11.
- **Portable ZIP** (`MasonAgent-win-x64.zip`, about 125 MB): extract the whole folder anywhere and run `MasonAgent.exe`. Runs on Windows 11 and on Windows 10 version 2004 or later.

Both are x64 and self-contained (.NET and the Windows App SDK are included). Releases are not code-signed yet, so Windows SmartScreen may warn on first run: choose **More info → Run anyway**. SHA-256 files are attached to each release.

**You also need a model.** Model weights and runtimes are not bundled. The quickest start is **Models → Quick setup**, which installs llama.cpp for your GPU and downloads a coding model that fits after you confirm. Or point Mason Agent at:

- GGUF weights. Mason Agent runs them inside the app with a llama.cpp build that includes `llama-server-impl.dll` (current releases do). Turn off **Run GGUF models inside Mason** under Models → Runtimes to give each model its own `llama-server` process instead, or
- Transformers (safetensors) weights plus a Python installation with the needed packages (an optional isolated runtime for GPTQ/AWQ models can be set up from the app; that setup downloads Python packages), or
- an OpenAI-compatible server already running on `localhost` / `127.0.0.1`.

Git for Windows is optional and adds Git Bash as a shell for commands. Using PowerShell in Auto mode needs a one-time setup that asks for administrator approval.

**Where settings live.** Settings, projects, sessions and chats are stored in `%LOCALAPPDATA%\MasonAgent`. To keep everything beside the app instead, put an empty file named `portable.flag` next to `MasonAgent.exe`; data then goes to a `data` folder in the same directory. The `MASON_DATA_HOME` environment variable overrides both. Your project folders are never moved; only the edits you allow are written to them.

## macOS

For macOS 14 or later on Apple silicon and Intel. Download from the [release page](https://github.com/afj176/mason-agent/releases/tag/preview):

- **Installer** ([`MasonAgent-macos.pkg`](https://github.com/afj176/mason-agent/releases/download/preview/MasonAgent-macos.pkg)): puts Mason Agent in Applications and links `mason` into `/usr/local/bin`, so it works in every terminal. It opens with an illustrated welcome page and ends with an illustrated finish page.
- **App only** ([`MasonAgent-macos.zip`](https://github.com/afj176/mason-agent/releases/download/preview/MasonAgent-macos.zip)): drag the app into Applications, then choose **Agent → Install Command Line Tool…** in the menu bar if you want `mason`.

The app is signed ad hoc and not notarized by Apple, so macOS blocks it the first time. Open **System Settings → Privacy & Security** and choose **Open Anyway**; on macOS 14 you can also right-click the file and choose **Open**.

Every change is built and checked on a macOS 15 runner: the tests (including real commands under the sandbox), the `.pkg` install, a sandboxed `mason` request in Auto, and a request typed into the app and approved there. That check uses a stand-in model, and the app has had little use on people's own Macs yet.

Settings, sessions and downloaded models live in `~/Library/Application Support/MasonAgent`. `MASON_DATA_HOME` overrides the location.

## Linux

For x64. Download from the [release page](https://github.com/afj176/mason-agent/releases/tag/preview):

- **Debian and Ubuntu** ([`mason-agent-linux-amd64.deb`](https://github.com/afj176/mason-agent/releases/download/preview/mason-agent-linux-amd64.deb)): installs `mason` and `mason-agent` in `/usr/bin` with a menu entry. Built on Ubuntu 24.04; needs that or a newer Debian or Ubuntu.

  ```sh
  sudo apt install ./mason-agent-linux-amd64.deb
  ```

- **Other distributions** ([`mason-agent-linux-amd64.tar.gz`](https://github.com/afj176/mason-agent/releases/download/preview/mason-agent-linux-amd64.tar.gz)): installs for your user under `~/.local` without administrator rights, tells you if `~/.local/bin` is not on your PATH yet, and `install.sh --uninstall` removes it. Needs GTK 4, gtkmm 4.10 or later, libadwaita 1.4 or later and libcurl from your distribution; the installer lists any that are missing.

  ```sh
  tar -xzf mason-agent-linux-amd64.tar.gz
  cd mason-agent-*-linux-amd64 && ./install.sh
  ```

Install bubblewrap to use Auto. Without it Mason still works, and Auto refuses to run commands instead of running them unprotected. The app is built and checked on Ubuntu 24.04 for every change and has not been tried on other distributions. There is no Flatpak, RPM or arm64 build yet.

Settings, sessions and downloaded models live in `~/.local/share/MasonAgent` (or `$XDG_DATA_HOME/MasonAgent`). `MASON_DATA_HOME` overrides the location.
