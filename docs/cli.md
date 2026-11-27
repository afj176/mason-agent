# The mason command line

`mason` is the same agent in your terminal, on all three systems. The macOS installer and both Linux packages put it on your PATH. On Windows it arrives with version 2.2.1: the installer can add it to your PATH, and with the portable ZIP you add its `bin` folder yourself.

The session and model commands below are the same everywhere, and so are `/compact`, `/agent`, `/commands`, `/mcp` and `/queue`. The script operations `exec`, `review`, `serve`, `map`, `symbol`, `git-status`, `worktree` and the `task-` commands are on all three, `compare-decision` and the `terminal-` commands are on Windows, and on macOS and Linux the browser task is `browse`. On macOS and Linux, while Mason works, type a message and press Enter to queue it, or start it with `!` to steer. The examples below use Windows paths.

```powershell
cd D:\src\shop
mason
```

No options are needed. On start, `mason` uses a model that is already running, otherwise restarts the one you used last, otherwise lists the coding models on your computer to pick from, and with none installed offers to download one that fits your GPU.

Type a request and each tool call prints as it finishes. Approvals take one key: `y` once, `a` always for this project, `n` decline. Esc stops a run.

| In a session | |
| --- | --- |
| `/diff` · `/undo` · `/redo` | See what changed; put back or reapply everything the last request did |
| `/permission read\|review\|edits\|auto` | What Mason may do without asking. `auto` runs commands in your system's sandbox |
| `/ask` · `/build` | Answer questions only, or make changes |
| `/new` · `/resume` · `/compact` · `/status` | Start, continue, shrink or inspect a session |
| `/model` | Switch between running models and saved connections |
| `/theme` · `/theme NAME` | Show the theme families, or choose one. The app uses the same choice |
| `/models` | The models on this computer and what each can do for the agent |
| `/models start` · `/models stop` | Pick a context size with the memory each needs, then run a GGUF coding model with llama.cpp for this session. `/models start 1 16k` skips the picker |
| `/models search TEXT` · `/models download REPO` | Find a model on Hugging Face, see its builds and which fit, download one |
| `/models setup` · `/models runtime` | Suggest and download a model that fits; install llama.cpp for your GPU |
| `/rules` · `/agent` · `/commands` | Saved approvals, and your agents and commands from `.mason/` |
| `/languages` · `/languages install NAME` | The language server that checks each language in the project; install a missing one |
| `/notes` · `/notes add TEXT` | Notes kept for the project and given to the model with each request (macOS and Linux; in the app on Windows) |
| `/generation` · `/generation NAME VALUE` | Temperature, top P and the like, kept for the model in use (macOS and Linux; in the app on Windows) |
| `/comment` · `/review` · `/milestone` · `/accept` · `/window` · `/embeddings` · `/fork` · `/export` · `/import` | On macOS and Linux: pin a note to a line, have the model review the changes, keep a plan, run acceptance scenarios, attach a window, choose an embeddings server, fork a session from an earlier request, save or add a session as a file |

In a wide colour terminal the start banner shows the app icon in half blocks, and the accent follows your theme. `mason theme NAME` sets the theme without starting a session.

`/help` has the full list. Options when starting: `--workspace PATH`, `--resume [SESSION]`, a permission (`--read-only`, `--review`, `--edits`, `--auto`), and `--endpoint URL --model ID` for any OpenAI-compatible server on localhost.

Good to know, as it works on Windows: a model started from the terminal stops when you leave `mason`. Downloads share one queue with the desktop app, so close the app or download there while it is open. Approval rules, model files, agents and commands are shared with the desktop app; terminal sessions are kept separately and do not appear in its session list.

**For scripts.** The same executable runs single operations that print JSON Lines and exit 0 on success. `exec` is read-only unless you pass `--auto`.

```powershell
mason exec --workspace D:\src\shop --endpoint http://127.0.0.1:8080 --model YOUR_MODEL --auto --prompt-file brief.txt
mason review --workspace D:\src\shop --endpoint http://127.0.0.1:8080 --model YOUR_MODEL --prompt-file brief.txt
mason worktree --workspace D:\src\shop --destination D:\src\shop-pricing --branch mason/pricing
mason symbol --workspace D:\src\shop --path src/cart.ts --line 12 --column 8 --mode references
```

Each run gets its own journal, so builds can run in parallel against separate worktrees (on Windows the project menu also offers **New isolated worktree**). `mason --help` lists browser tasks, background tasks and session resume. For a real development server on Windows, `serve --workflow preview --port 8123` runs the named workflow under your Windows account, verifies the loopback listener belongs to its process tree, proxies the disposable browser to it, then stops the tree.
