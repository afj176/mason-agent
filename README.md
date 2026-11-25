<div align="center">

<img src="docs/images/mortar.png" width="160" alt="Mortar, the Mason Agent mascot">

# Mason Agent

**Make the next move clear.** A coding workspace for Windows that runs on your own local models.

[Website](https://masonagent.site) · [Download](https://github.com/afj176/mason-agent/releases/latest) · [Installer (x64)](https://github.com/afj176/mason-agent/releases/latest/download/MasonAgent-Setup-x64.exe) · [Portable ZIP](https://github.com/afj176/mason-agent/releases/latest/download/MasonAgent-win-x64.zip)

</div>

<img src="docs/images/code-dark.png" alt="A Mason Agent coding session in dark mode: projects sidebar, a request to fix CSV parsing, and the agent's summary of two edited files and a passing test run">

## What it does

- **Projects that keep their history.** Add a folder to keep its sessions, files and changes together. Right-click a project in the sidebar or open its project menu and choose **Remove project from Mason**. Its files stay on disk. Adding the same folder again restores its sessions and project notes. Switch between Code, Chat and Models in the title bar. Ctrl+K searches sessions, projects, workspace files and commands. Each session remembers its workspace view. Workbench pairs the conversation with its artifacts, Review opens line comments beside the recorded checks, and Notebook gives the conversation a wider reading layout and follows your Mason appearance setting.
- **Runs on your own models.** GGUF models run inside Mason Agent through llama.cpp, so there is no separate server process to start or keep alive. Transformers models run with Python, and you can connect to an OpenAI-compatible server already running on localhost. **Quick setup** under Models checks your GPU, installs the matching llama.cpp build, suggests a coding model that fits in its memory and starts it once you confirm the download.
- **You set the permissions.** Sessions start read-only. Switch to *Review edits* (approve each edit and command), *Workspace edits*, or *Auto*, which runs commands in a Windows sandbox limited to the project folder with network access off unless you allow a destination. Commands run in Git Bash, PowerShell or Command Prompt. On an approval, **Always allow…** saves an editable rule for that command prefix, folder or MCP tool in this project. A deny rule always wins, and you can revoke rules in Harness settings (project menu → **Project policy & extensions**).
- **Every change on record.** Edits appear as diffs against the original file and can be restored; select a diff line to attach feedback and send several comments together. **Undo** in the session menu puts back everything a request changed, including files its commands created, edited or deleted, and **Redo** reapplies it; files you changed since are left alone and listed. Completed turns show the observations behind each feature check, with the full evidence and verified revision available to inspect.
- **Chat beside your code.** Conversations with any connected model, with code blocks, tables and attached documents. Favourite and recent models come first in the pickers. Long chats compact automatically and keep their originals.
- **A model library.** Scans your folders for GGUF and safetensors weights, shows what each model can do for the coding agent, estimates whether it fits in your GPU memory, and downloads models from Hugging Face on request. Choosing a model sizes its context to the memory you have. Each connection can carry its own generation settings (temperature, top P/K, min P, repeat penalty, seed) for the coding agent.
- **Your agents and commands.** Agents and slash commands are Markdown files with front matter in `.mason/agents` and `.mason/commands`; Mason Agent also reads `.claude/` and `.opencode/` folders. Pick an agent in session settings; commands appear in Ctrl+K and take `$ARGUMENTS` or `$1`–`$9`.
- **Scheduled and background work.** Schedule a request hourly, daily, on weekdays or weekly; it runs as a background task while Mason Agent is open, and a run missed while it was closed happens once at the next start. The taskbar button shows progress and flashes when an approval is waiting, and Windows notifies you when Mason Agent is in the background.
- **Fork, export and import sessions.** Fork from any earlier request, optionally putting files back as they were then. Export a session as JSON, with paths and keys removed if you like, and import it into another project.

## Screenshots

Build retains feature requirements, command failures and verification receipts across follow-ups and restarts. Start a new session or enter `/new` followed by a request to begin a separate task. Each verification records the revision of its inputs. In Auto, Mason checks fully specified literals when a relevant file already exists and after a file edit.

For multi-step browser checks, put a version 1 `mason.acceptance.json` in the project root and ask Mason to “verify the features in mason.acceptance.json”. Use “implement the features in mason.acceptance.json” to authorize edits. The [checkout fixture](tests/fixtures/checkout/mason.acceptance.json) checks distinct products, discount thresholds and order persistence in Edge at phone and desktop widths. A static project can omit `Application`. A Python WSGI project specifies a local `module:callable`. Mason starts it in an isolated temporary copy with a fresh database, observes same-origin requests and discards the copy at the end of each viewport check.

For an empty project, you can end the Build request with a fenced code block labeled `mason.acceptance.json` containing the same contract. Its scenarios define the automated acceptance criteria for that turn. Mason retains them in the session so edits to the project cannot change the assertions. Use `click_if_enabled` followed by an assertion to check controls that may legitimately be disabled, such as checkout on an empty cart. After `capture`, use `new_id` to require a distinct identifier and `same` to verify that identifier after a reload.

For a fresh, single-page offline store with unit prices, quantity discounts, a cart that clears on checkout and a saved receipt, Auto can use a built-in catalog component. The local model extracts the offers and writes the storefront copy and palette. The component computes line totals in cents and generates receipts. Edge exercises the resulting page, and a separate review derives checks from the brief and the rendered controls. Extra requirements stay on the general implementation and verification path.

The `navigate_code` tool finds C# definitions, bound references, related tests and compiler diagnostics using framework references. For Python, JavaScript/TypeScript, Go, Ruby and Dart it returns labeled text candidates. Project-specific compiler output is available in command receipts.

### Agent tools in 2.0

Auto builds of static browser features get a separate read-only review after the original checks pass. The reviewer receives the user's brief and the rendered page, with its own context. Mason pins its executable checks and runs them in fresh Edge profiles at both viewport widths. For explicit catalog prices it calculates independent basket totals. It also checks displayed prices and durable receipts when requested. Review results and failed steps appear in Activity.

In **Session settings → Attach or detach a window**, select an open Windows application to let Mason observe accessible controls. Build sessions ask you before invoking a control or setting a value. Attachments end when Mason closes. For project websites, the browser tool can operate a disposable Edge page, observe after each action and save a screenshot under the session profile. Desktop controls depend on the target application's Windows accessibility support.

Ask for TypeScript, JavaScript or Python symbol inspection at a file, line and column. TypeScript provides compiler diagnostics and bound definitions/references. Jedi provides Python definitions/references and syntax diagnostics. Both operate on a bounded source snapshot. Dependency-specific checks still use your project's build. Python inspection requires a local Python installation.

Store reusable instructions in `.mason/skills/NAME/SKILL.md`. Named workflows live in `mason.workflows.json`. Auto executes them inside the project sandbox. Setting `BeforeVerify` runs a workflow before final verification and a failed hook blocks completion. The agent can also start, poll and stop offline background jobs during a run. Jobs share the run's 30-minute deadline.

```json
{"Version":1,"Workflows":{"test":{"Executable":"python","Arguments":["-B","-m","unittest"],"Directory":".","BeforeVerify":true},"preview":{"Executable":"python","Arguments":["-m","http.server","8123","--bind","127.0.0.1"],"Directory":"."}}}
```

Add local MCP servers under **MCP servers** in Harness settings (project menu → **Project policy & extensions**): enter the endpoint, choose **Discover tools** to list what the server offers, and tick the tools the agent may call. Servers can be edited, switched off or removed there. They are saved in the Mason profile's `mcp-servers.json`, for example `[{"Name":"docs","Endpoint":"http://127.0.0.1:8088/mcp","Tools":["search_docs"]}]`. Mason negotiates MCP 2025-06-18 over HTTP, lists allowlisted tools, and shows the tool arguments for approval before a call unless an **Always allow** rule covers it. An unattended run declines approvals.

### Command line

`mason` is the same agent in your terminal. The installer puts it on your PATH; with the portable ZIP, add its `bin` folder to PATH yourself.

```powershell
cd D:\src\shop
mason
```

No options are needed. On start, `mason` uses a model that is already running, otherwise restarts the one you used last, otherwise lists the coding models on your computer to pick from, and with none installed offers to download one that fits your GPU.

Type a request and each tool call prints as it finishes. Approvals take one key: `y` once, `a` always for this project, `n` decline. Esc stops a run.

| In a session | |
| --- | --- |
| `/diff` · `/undo` · `/redo` | See what changed; put back or reapply everything the last request did |
| `/permission read\|review\|edits\|auto` | What Mason may do without asking. `auto` runs commands in the Windows sandbox |
| `/ask` · `/build` | Answer questions only, or make changes |
| `/new` · `/resume` · `/compact` · `/status` | Start, continue, shrink or inspect a session |
| `/model` | Switch between running models and saved connections |
| `/models` | The models on this computer and what each can do for the agent |
| `/models start` · `/models stop` | Run a GGUF coding model with llama.cpp for this session |
| `/models search TEXT` · `/models download REPO` | Find a model on Hugging Face, see its builds and which fit, download one |
| `/models setup` · `/models runtime` | Suggest and download a model that fits; install llama.cpp for your GPU |
| `/rules` · `/agent` · `/commands` | Saved approvals, and your agents and commands from `.mason/` |

`/help` has the full list. Options when starting: `--workspace PATH`, `--resume [SESSION]`, a permission (`--read-only`, `--review`, `--edits`, `--auto`), and `--endpoint URL --model ID` for any OpenAI-compatible server on localhost.

Good to know: a model started from the terminal stops when you leave `mason`. Downloads share one queue with the desktop app, so close the app or download there while it is open. Approval rules, model files, agents and commands are shared with the desktop app; terminal sessions are kept separately and do not appear in its session list.

**For scripts.** The same executable runs single operations that print JSON Lines and exit 0 on success. `exec` is read-only unless you pass `--auto`.

```powershell
mason exec --workspace D:\src\shop --endpoint http://127.0.0.1:8080 --model YOUR_MODEL --auto --prompt-file brief.txt
mason review --workspace D:\src\shop --endpoint http://127.0.0.1:8080 --model YOUR_MODEL --prompt-file brief.txt
mason worktree --workspace D:\src\shop --destination D:\src\shop-pricing --branch mason/pricing
mason symbol --workspace D:\src\shop --path src/cart.ts --line 12 --column 8 --mode references
```

Each run gets its own journal, so builds can run in parallel against separate worktrees (the project menu also offers **New isolated worktree**). `mason --help` lists browser tasks, background tasks and session resume. For a real development server, `serve --workflow preview --port 8123` runs the named workflow under your Windows account, verifies the loopback listener belongs to its process tree, proxies the disposable browser to it, then stops the tree.

| Review changes | Chat |
| --- | --- |
| <img src="docs/images/changes-dark.png" alt="Changes viewer showing a unified diff beside the session"> | <img src="docs/images/chat-dark.png" alt="Chat with a local model's answer, a code block and the Reference panel"> |
| **Models** | **Harness settings** |
| <img src="docs/images/models-dark.png" alt="Model library with GPU memory, Quick setup and local GGUF models with their agent use"> | <img src="docs/images/harness-dark.png" alt="Harness settings with saved approvals, agents and commands, MCP servers and schedules"> |

## Install

Download from the [latest release](https://github.com/afj176/mason-agent/releases/latest):

- **Installer** (`MasonAgent-Setup-x64.exe`, about 106 MB): installs for your Windows account into `%LOCALAPPDATA%\Programs\MasonAgent`; no administrator rights needed. It can add the `mason` command to your PATH. Requires Windows 11.
- **Portable ZIP** (`MasonAgent-win-x64.zip`, about 125 MB): extract the whole folder anywhere and run `MasonAgent.exe`. Runs on Windows 11 and on Windows 10 version 2004 or later.

Both are x64 and self-contained (.NET and the Windows App SDK are included). Releases are not code-signed yet, so Windows SmartScreen may warn on first run: choose **More info → Run anyway**. SHA-256 files are attached to each release.

**You also need a model.** Model weights and runtimes are not bundled. The quickest start is **Models → Quick setup**, which installs llama.cpp for your GPU and downloads a coding model that fits after you confirm. Or point Mason Agent at:

- GGUF weights. Mason Agent runs them inside the app with a llama.cpp build that includes `llama-server-impl.dll` (current releases do). Turn off **Run GGUF models inside Mason** under Models → Runtimes to give each model its own `llama-server` process instead, or
- Transformers (safetensors) weights plus a Python installation with the needed packages (an optional isolated runtime for GPTQ/AWQ models can be set up from the app; that setup downloads Python packages), or
- an OpenAI-compatible server already running on `localhost` / `127.0.0.1`.

Git for Windows is optional and adds Git Bash as a shell for commands. Using PowerShell in Auto mode needs a one-time setup that asks for administrator approval.

**Where settings live.** Settings, projects, sessions and chats are stored in `%LOCALAPPDATA%\MasonAgent`. To keep everything beside the app instead, put an empty file named `portable.flag` next to `MasonAgent.exe`; data then goes to a `data` folder in the same directory. The `MASON_DATA_HOME` environment variable overrides both. Your project folders are never moved; only the edits you allow are written to them.

## Working with the coding harness

Click **Website** or a completed turn’s **Open preview** to open the selected HTML file or the project’s `index.html` in Mason’s embedded browser. Both actions can also reopen a standalone HTML page changed in the session or a local server tab in a server-only project. Use the address field for another project page or the loopback URL of a running development server. **Reload website** reads the latest files. Browser storage for the preview is kept under the Mason profile for that project.

During a coding run, **Steer** interrupts the current model action and applies your new instruction. **Queue** saves a follow-up for after completion. Use `/queue` to resume saved input after a stop. In the session menu, **Undo** previews and restores everything the latest request changed, including command output files, and **Redo** reapplies it. Requests sent with earlier versions have no snapshots, so for those **Rewind last edited turn** restores files changed through Mason's edit tools; a later external edit blocks the rewind before any files change. Commands and their side effects remain in the activity history.

Repository search can page through results and read numbered line ranges in UTF-8 files up to 8 MiB. The Repository menu and `mason git-status --workspace PATH` show staged, unstaged and untracked paths. A delegated investigation reads up to eight assigned files and returns findings with source citations. Mason checks the citations against the current files.

With a window attached and a local vision model loaded, Mason can inspect a screenshot and propose a click or drag. The approval shows the captured window. Mason checks the frame again before sending input and observes the result afterward. Text-only models can use supported accessibility controls.

For a local WSGI application, an acceptance scenario can combine Edge interactions, observed HTTP responses and an independent count of a SQLite table in the disposable application copy. The release trials exercise repeated checkout of one basket and separate checkout of two baskets. The two-model workflow qualification requires an interactive Windows desktop and at least one model serving images with a compatible projector.

### Plan and inspect a project

Open **Plan & repository map** in the project menu. Search for a qualified symbol to see definitions and callers. C# uses Roslyn. TypeScript, JavaScript and Python use their language services for supported declarations. Other matches carry a candidate label. Add milestones with files, dependencies and a verification workflow. A source change invalidates the affected receipt and blocks dependent milestones. A manual review has its own status. Project notes can be edited or forgotten and carry a source revision.

### Connect the preview and editor

Use the element picker in **Website** to attach an accessible control, its screenshot and recent console diagnostics. **Verify click** repeats the interaction in disposable Edge at mobile and desktop widths. For VS Code, install `integrations/mason-workspace.vsix` from the Mason installation directory. Run **Mason: Share Selection with Local Project**, then **Attach editor selection** in Mason. The attachment records the editor buffer version, selected text, diagnostics and disk hash. **Mason: Toggle Project Task Notifications** follows this folder’s background tasks in VS Code.

### Run tasks and review changes

**Background tasks** starts a detached local worker and reopens its progress and pending approvals after the desktop closes. The CLI supports `task-list`, `task-show`, `task-allow`, `task-deny` and `task-cancel`. An approval identifies both the task and the pending request. The workspace terminal supports line input, EOF, scrollback and cancellation. **Model roles & decision inspector** assigns the implementer’s review and vision partners and limits model calls. The optional trace recorder supports prediction-only comparisons of saved requests. GPU memory management applies to role models launched by the desktop.

**Fork session in an isolated worktree** starts an experiment from the current checkout, including uncommitted files. Compare experiments from the same turn, inspect their checks and apply a selected hunk. **Git delivery & CI feedback** prepares a local draft from the reviewed hunks. Publishing to the configured GitHub repository requires an explicit action and a configured GitHub CLI.

### Use a project's test suite

In `.mason/tests.json`, register the named test, the workflow that runs it, the files that define the test and the implementation inputs. For example, this entry uses a Node test named `discount` reported by a workflow named `test` in `mason.workflows.json`.

```json
[{
  "requirement": "Apply the loyalty discount",
  "workflow": "test",
  "format": "tap",
  "report": "",
  "test": "discount",
  "testFiles": ["price.test.cjs"],
  "inputs": ["price.cjs"]
}]
```

Auto records a baseline and reruns the pinned check before completion. Results distinguish new regressions, missing tests and failures present at baseline. For a direct CommonJS assertion with two JSON arguments, the reviewer can identify an order-independent operation before editing starts. Mason checks the existing source for simple symmetric expressions, pins a second assertion with the arguments exchanged when appropriate, and reports its result. A test owner can specify `argumentOrder` as `ordered` or `interchangeable` to declare the contract explicitly. CommonJS codec projects can also declare a `roundTrip` property on a named test with `module`, `encode`, `decode` and `alphabet`. The module must be a declared input. Mason exercises every string of length zero through three over that alphabet in the same workspace sandbox and returns the first counterexample. Supported reporters include TAP, TRX, JUnit XML, Go test JSON and Flutter test JSON. Use **Project policy & extensions** for tool restrictions, workflow allowlists, event hooks and versioned skill, documentation and MCP bundles. Installed bundles start disabled. Documentation excerpts include the source, version and location.

## Build from source

Prerequisites:

- Windows 10/11 x64 and the [.NET 10 SDK](https://dotnet.microsoft.com/download). The Windows App SDK and Windows SDK build tools come from NuGet.
- For release builds: Visual Studio 2022 or later (or Build Tools) with the **C++ x64 build tools** for the native coding helpers, and Git for Windows.
- For the installer: [Inno Setup 6](https://jrsoftware.org/isinfo.php).

```powershell
# Run a Debug build (use MASON_DATA_HOME to keep test data away from your real profile)
dotnet build src\MasonAgent\MasonAgent.csproj -c Debug -p:Platform=x64

# Release payload and ZIP in artifacts\MasonAgent-<version>-win-x64
.\scripts\Invoke-ModelBenchmark.ps1 -Endpoint http://127.0.0.1:8080 -Model YOUR_LOADED_MODEL -OutputDirectory .cache\qualification
.\scripts\Invoke-WorkflowQualification.ps1 -Endpoint http://127.0.0.1:8080 -Model YOUR_LOADED_MODEL -SecondaryEndpoint http://127.0.0.1:8081 -SecondaryModel SECOND_MODEL -OutputDirectory .cache\workflow

# Omit -Installer and -InnoCompiler for the portable package alone.
.\scripts\Publish.ps1 -Version 2.2.1 -ModelEvidence .cache\qualification\report.json -WorkflowEvidence .cache\workflow\workflow-evidence.json -Installer -InnoCompiler "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
```

`Publish.ps1` requires a clean checkout and passing evidence for the exact source commit. Each model builds a store from an empty directory four times, reviews a working store and six single-defect variants, and repairs a separate JavaScript application during a concurrent edit. Edge exercises discount boundaries and two checkouts at phone and desktop widths. Both packages include the two reports and their SHA-256 hashes. Configure two running local llama.cpp servers on the Windows runner labeled `mason-model-evals` using `MASON_EVAL_ENDPOINT`, `MASON_EVAL_MODEL`, `MASON_SECONDARY_EVAL_ENDPOINT` and `MASON_SECONDARY_EVAL_MODEL`.

### Checks

`tests/Mason.CodeChecks` covers the coding engine, projects and sessions, shells, the Windows sandbox and its network denial. The sandbox checks need a small native probe, built with the MSVC tools:

```powershell
.\scripts\Build-CodeSandboxProbe.ps1
$env:MASON_SANDBOX_PROBE = (Resolve-Path .cache\code-sandbox-probe\SandboxNativeProbe.exe)
dotnet run --project tests\Mason.CodeChecks -c Release
```

The default run uses scripted model responses and needs no model. The `--evaluate-*` modes in `tests/Mason.CodeChecks/Program.cs` run real coding tasks and need a local model server.

## Privacy

Everything stays on your computer: projects, sessions, chats and settings live in your local profile, and models are reached only on localhost. Mason Agent connects to the internet only when you ask it to: searching or downloading models from Hugging Face, setting up the optional quantized runtime, or allowing a specific destination for a sandboxed command. There is no telemetry.

## License

[MIT](LICENSE). Manrope is used under the SIL Open Font License 1.1; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
