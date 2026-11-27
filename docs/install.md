# Install details

The [README](../README.md#install) has the steps. This page answers the questions that come after.

## Getting a model

Mason Agent doesn't include a model. The quickest way is **Models → Quick setup**, which installs llama.cpp for your hardware and downloads a coding model that fits after you confirm. You can also use:

- **GGUF files you already have.** Mason finds them and runs them with llama.cpp.
- **A server you already run**, such as llama.cpp, LM Studio or Ollama, as long as it is on this computer and speaks the OpenAI API.
- **Transformers (safetensors) weights**, through your own Python with PyTorch and Transformers installed.

## Where your data is kept

Settings, projects, sessions, chats and downloaded models are kept on your computer:

| | |
| --- | --- |
| Windows | `%LOCALAPPDATA%\MasonAgent` |
| macOS | `~/Library/Application Support/MasonAgent` |
| Linux | `~/.local/share/MasonAgent` |

Set `MASON_DATA_HOME` to use another folder. With the Windows portable ZIP, an empty `portable.flag` file next to `MasonAgent.exe` keeps everything in a `data` folder beside the app. Your project folders are never moved; Mason only writes the edits you allow.

## Windows

- Requires Windows 11 for the installer. The portable ZIP also runs on Windows 10 version 2004 or later.
- Git for Windows is optional and lets commands run in Git Bash. Using PowerShell in Auto needs a one-time setup that asks for administrator approval.
- To uninstall, use **Settings → Apps** in Windows. Your data folder is kept.

## macOS

- Requires macOS 14 or later, on Apple silicon or Intel.
- The first time you attach another app's window, macOS asks for the Accessibility permission, and for Screen Recording before Mason takes a picture of it.

## Linux

- The `.deb` needs Ubuntu 24.04 or newer, or a matching Debian. The archive needs GTK 4, gtkmm 4.10, libadwaita 1.4 and libcurl, and lists any that are missing.
- Install bubblewrap to use Auto. Without it, Auto refuses to run commands rather than running them unprotected.
- Mason installs the Vulkan build of llama.cpp when it finds a GPU and the Vulkan loader, and the CPU build otherwise.
- To remove the archive install, run `./install.sh --uninstall`.
- There is no Flatpak, RPM or arm64 build yet.
