# Mason Agent harness TODO

Reviewed against the installed Windows build 2.1.1 and source commit `6f27413` on November 24, 2026. The list below preserves the original acceptance criteria and the 2.1.1 audit. The original six keep their numbers so “3 and 4” continues to mean the repository map and resumable milestones.

## 2.2 implementation

| Workflow | Implementation and acceptance evidence |
| --- | --- |
| Repository context and milestones | Qualified C# and TypeScript references, file-revision invalidation, independent and dependent milestones across restart. The desktop exposes the map, editable decisions and source-linked project notes. |
| Edit recovery and tests | Per-file recovery preserves a concurrent edit. A named project test captures a baseline, reruns against the current revision and distinguishes unrelated failures. The Node fixture runs under the real sandbox. TRX, JUnit, Go and Flutter also have reporter parser checks. |
| Preview | The embedded preview captures selected controls. The same Edge check passes on the working fixture and fails after removing its handler. |
| Reconnectable work | The desktop and CLI share one task journal. A real Qwen worker survived closing its desktop at review. The pending decision was accepted once. Tests cover Windows model lifetimes and sandboxed terminal input. |
| Model roles and inspection | A paired run recorded a hash-bound LFM image observation after a Qwen implementation passed its browser checks. The inspector compares recorded text requests without executing the proposed tools. |
| Experiments and delivery | A dirty Git checkout can be forked and compared by hunk. The regression preserves a concurrent edit and a second unapplied hunk. Local Git draft creation and CI feedback parsing have separate checks. |
| Integrations | Project policy expiry and revocation, opted-in workflow events, bundle install and disable, named documentation excerpts and allowlisted stdio MCP resource reads have regressions. |
| Maintenance evaluation | The runner compares two models twice on a regression, normalization refactor, interrupted edit and historical release-site source. Reports retain unsuccessful trials, independent outcomes, revisions and timings. |

The release workflow binds both model identities and these evaluation reports to the source commit. Debugger integration, cloud workers, cross-device clients and team administration remain future product decisions.

## Goose Desktop and OpenCode Desktop comparison (November 25, 2026)

Compared against Goose v1.61.0 (`aaif-goose/goose`) and OpenCode v2.4.1 (`anomalyco/opencode`) from their source and docs. Mason already leads on the Windows sandbox, verification receipts, compiler-backed navigation (OpenCode v2 removed LSP) and browser checks. Items where they are ahead:

| # | Gap | Who does it | Mason today | Status |
| --- | --- | --- | --- | --- |
| G1 | **Models run inside the app, no server process.** | Goose embeds llama.cpp in-process (`crates/oi-kit-local-inference`). | `llama-server.exe` or Python runs as an owned child process on a loopback port. | **Implemented**: GGUF models run inside Mason through `llama-server-impl.dll` (see below). |
| G2 | One-click local onboarding: detect the GPU, install the matching runtime, download the recommended quantization that fits memory. | Goose `recommend_variant`, onboarding `LocalModelPicker`. | Runtime installer, Hugging Face downloads and fit estimate exist as separate steps. | **Implemented**: Models → Quick setup detects the GPU, installs the matching llama.cpp build if needed, ranks trusted coding GGUFs by the best quantization that fits 85% of free VRAM, downloads after confirmation and starts the model inside Mason (`QuickSetup.cs`). |
| G3 | Undo/redo of a whole step, including files changed by commands. | OpenCode git snapshots per model step. | Rewind covers files changed through edit tools only. | **Implemented**: each request records content-addressed workspace snapshots in the profile; Undo/Redo in session settings restores everything the request changed and leaves later edits alone (`CodeSnapshots.cs`). |
| G4 | Permission rules with "always allow" saved per project, matched on path, shell prefix and URL. | OpenCode `permissions`. | Per-action approvals and project policy profiles. | **Implemented**: approvals offer Always allow with an editable prefix; deny wins; rules are listed and revoked in Harness settings (`CodeApprovalRules.cs`). |
| G5 | Custom agents and slash commands as Markdown files. | OpenCode agents/commands, Goose recipes and `.agents/agents`. | Skills and named workflows; fixed Ask/Build and roles. | **Implemented**: `.mason/agents` and `.mason/commands` (also `.claude/…`, `.agents/agents`, `.opencode/command`); agent picker in session settings, commands in Ctrl+K, `$ARGUMENTS`/`$1–$9` (`CodeCustomization.cs`). |
| G6 | MCP management UI with stdio and HTTP servers, toggles and approvals. | Both. | `mcp-servers.json` and bundles; no add/toggle UI. | **Implemented**: Harness settings lists, adds, edits, enables and removes stdio and HTTP servers, with tool discovery for the allowlist (`CodeMcpRegistry`). |
| G7 | Scheduled or triggered runs. | Goose scheduler, OpenCode automations. | Background tasks run on demand. | **Implemented**: hourly, daily, weekday or weekly runs started as background tasks while Mason is open; a missed run starts once (`CodeSchedules.cs`). |
| G8 | Edit a past message or fork from it; session import/export. | Goose sessions, OpenCode `session export --sanitize`. | Fork into a worktree from the current turn. | **Implemented**: fork before any request (optionally restoring files), export with redaction, import (`CodeSessionTransfer.cs`). |
| G9 | Windows taskbar progress and a flash when an approval waits. | OpenCode v2.4.0. | In-app notices only. | **Implemented**: taskbar progress and flash, plus a Windows notification when Mason is in the background (`TaskbarStatus.cs`, `Notifications.cs`). |
| G10 | Tool-calling fallback for models whose template has no native tools. | Goose emulated tools. | Native tool calls, with a Flint-specific path. | **Implemented**: a call written as text (wrong tags, fenced JSON) for an offered tool is recovered; llama.cpp already emulates tools for templates without them (`ToolCallRecovery.cs`). |

### Extra recommendations

| # | Item | Source | Status |
| --- | --- | --- | --- |
| X1 | Generation settings per coding connection (temperature, top-p/k, min-p, repeat penalty, seed) | Goose `ModelSettingsPanel` | Implemented: Connections → Coding generation |
| X2 | Apply the memory-fit context automatically when a model is selected | Goose `estimate_max_context_for_memory` | Implemented: selecting a model applies its memory-fit context |
| X3 | Model switcher in the composer with favourites and recents | OpenCode v2 model picker | Implemented: ★ favourites and recents first in Code and Chat pickers |
| X4 | Windows notification when a run finishes or waits for approval while Mason is in the background | OpenCode taskbar/inbox | Implemented with G9 |
| D1 | Fluent 2 foundations: type ramp, 4/8 radii, system layering brushes over Mica, one accent ramp instead of per-control overrides | Windows design guidance | Implemented: `Styles/Mason.xaml`, App.xaml accent ramp |
| D2 | One component set: settings cards, segmented choices, tables, detail lists, icon buttons, pills, empty states | WinUI Gallery, PowerToys, Files | Implemented: `MainWindow.Design.cs` |

### G1: models inside Mason

Recent llama.cpp Windows builds split every tool into a thin `.exe` and an `-impl.dll`. `llama-server-impl.dll` exports `int llama_server(int, char**)` and `void llama_server_terminate()`, the entry points llama-cli uses to embed the server. Mason loads that DLL from the selected runtime folder and runs the server on a thread in its own process. Templates, tool-call parsing, JSON-schema grammars, vision projectors and speculative decoding are therefore identical to `llama-server.exe`, and every client keeps the same loopback API.

Constraints, all enforced in `EmbeddedLlamaServer`:

- One embedded model at a time. The server keeps its shutdown handler in a static, so additional models run as owned processes.
- One llama.cpp build per Mason process. Loading a second build's DLLs would bind to the first build's `llama.dll`. Selecting a different build takes effect for embedding after Mason restarts; until then that build runs as a process.
- `llama_server_terminate` is only called after the server reports that it is listening; before that the static handler can refer to the previous run.
- A native abort (`GGML_ASSERT`, a CUDA failure) ends Mason itself. Clear **Run GGUF models inside Mason** in Runtime setup to isolate models in their own processes again.
- An embedded model stops when Mason closes, so a background task that must outlive the desktop needs the process mode.
- Transformers and GPTQ/AWQ models still run in Python. Embedding CPython with PyTorch and CUDA is not practical in a self-contained app.

## Original six

- [ ] **1. Recover at the failed edit.** A failed snippet match currently pauses snippet tools for the rest of the run. Refresh the affected file and offer a bounded, hash-checked patch. Prove it with a concurrent edit that survives while the intended change lands and a second file remains patchable. [Edit recovery](windows/src/Mason.Code.Core/CodeEngine.cs)
- [ ] **2. Use the project's own tests as feature evidence.** Add named test results and baseline comparison to the existing workflows and acceptance checks. Start with .NET and TypeScript, then Go and Flutter. Verify that Review identifies the failing test before the patch, its passing rerun, and a pre-existing unrelated failure. [Verification](windows/src/Mason.Code.Core/CodeAcceptance.cs)
- [ ] **3. Build an incremental repository map.** Index symbols, imports, callers and tests across packages. Rank the slice sent to a local model by relevance and available context. Label textual matches separately from compiler-resolved references. Use a rename to check that Mason finds the implementation and callers among same-named symbols and refreshes the affected entries. [Current semantic queries](windows/src/Mason.Code.Core/CodeSemantic.cs) · [Aider repository maps](https://aider.chat/docs/repomap.html)
- [ ] **4. Add editable, resumable milestones.** Give a task a plan with dependencies, decisions, evidence and the revision last verified. Close Mason midway, edit one dependency externally, and reopen. Mason should preserve finished independent work, recheck the affected milestone and show the next action. [Session state](windows/src/Mason.Code.Core/CodeSession.cs)
- [ ] **5. Fork, compare and selectively apply an experiment.** Connect worktrees, session history and Review. Try two solutions from the same turn, compare their diffs and checks, then take a selected change without disturbing unrelated local edits. Account for files changed by commands as well as edit tools. [Worktrees](windows/src/Mason.Code.Core/CodeWorktree.cs) · [Rewind](windows/src/Mason.Code.Core/CodeRewind.cs)
- [ ] **6. Evaluate maintenance work on existing repositories.** Use held-out bugs, repeated runs and independent checks. Record false completions, missed valid fixes, time and human interventions per model. Publish comparable Qwen and LFM results for a refactor, regression and interrupted change. The current multi-file benchmark asks for two specified text files, and the storefront has a dedicated implementation path. [Benchmark](windows/tests/Mason.CodeChecks/ModelBenchmark.cs) · [Storefront planner](windows/src/Mason.Code.Core/CodeStorefrontPlan.cs)

## Wider feature audit

“Present” means an implementation is exposed in the inspected source. “Partial” identifies an existing mechanism and the specific extension it lacks. “Open” identifies a first-class workflow I could not find in this release. A shell command or a separately launched CLI can cover some of these workflows manually.

| Capability | Mason 2.1.1 | Material next step |
| --- | --- | --- |
| Workspace authority and approvals | Present. Project-scoped Auto sandbox, network controls and per-action approvals. [Tool permissions](windows/src/Mason.Code.Core/CodeSchema.cs) | Add inspectable project policy profiles for routine commands and tools, with scope, expiry and revocation. |
| Navigation, context and edits | Partial. Paged search, scoped instructions, checkpointed history, semantic queries and hash-checked edits. [Context](windows/src/Mason.Code.Core/CodeContext.cs) | Items 1 and 3. Add editable, project-scoped memory for durable decisions and verified commands, with source, freshness and a way to forget a fact. |
| Planning and background execution | Partial. Ask/Build, steer, queue, resume and job polling exist. Jobs belong to the run and are cancelled when its services are disposed. [Run services](windows/src/Mason.Code.Core/CodeRunServices.cs) | Item 4 plus a local task service. Closing the UI should allow a designated task to continue. Reattach to progress, cancellation and pending approvals. |
| Parallel work and local model choice | Partial. Worktrees, independent CLI runs, model capability checks and bounded read-only delegation exist. [Investigator](windows/src/Mason.Code.Core/CodeInvestigation.cs) · [Model adapter](windows/src/MasonAgent/CodeLocalModel.cs) | Define implementer, reviewer and visual-verifier roles with model, tools, budget and worktree scope. Schedule by available GPU memory. Permit sequential handoffs on a single GPU. |
| Interactive terminal and debugging | Partial. Shells, command output and background job tails exist. Command input currently opens a null device. [Command runner](windows/src/Mason.Code.Core/CodeCommand.cs) | Give the user and agent an attachable terminal with input, cancellation and scrollback. A later debugger adapter could attach a stack frame and locals to a task. |
| Browser and native computer use | Present for embedded preview, disposable Edge checks and approved actions in an attached Windows app. [Preview](windows/src/MasonAgent/MainWindow.Website.cs) · [Browser tools](windows/src/Mason.Code.Core/CodeComputerUse.cs) | Let the user point at a live element and attach its accessible name, screenshot, relevant console error and source location when available. Re-run the interaction after the patch. |
| IDE and developer context | Partial. Workspace file references, diff-line comments and opening files in an external editor exist. [Code viewer](windows/src/MasonAgent/MainWindow.CodeViewer.cs) | Add a bidirectional editor bridge for selected lines, unsaved buffers and diagnostics. Distinguish editor-buffer evidence from the on-disk revision before applying an edit. |
| Verification, review and evaluation | Partial. Browser receipts, Python test checks, before-verify workflows, independent review and model benchmarks exist. [Acceptance](windows/src/Mason.Code.Core/CodeAcceptance.cs) · [Release qualification](windows/scripts/Assert-WorkflowQualification.ps1) | Items 2 and 6. Add a local run inspector that reconstructs one failed decision from the visible context, tool schema, model identity and receipt. Compare another model on the saved input without replaying live writes. |
| Integrations and extensibility | Partial. Project skills, named workflows, a before-verify hook and allowlisted loopback HTTP MCP tools exist. [MCP client](windows/src/Mason.Code.Core/CodeMcp.cs) · [Workflow host](windows/src/Mason.Code.Core/CodeRunServices.cs) | Add event hooks with outcomes, local stdio MCP and resource discovery. Make it possible to install, inspect, disable and version a bundle of tools and skills. |
| Git delivery and CI feedback | Partial. Repository status, worktrees, diff review, CLI and CI for Mason itself exist. [Git](windows/src/Mason.Code.Core/CodeGit.cs) · [CLI](windows/src/Mason.Cli/Program.cs) | Add a user-project workflow that drafts a PR from reviewed hunks, attaches check evidence and brings a failing CI check or review comment back to its originating task. |
| Clients, notifications and automation | Partial. The CLI emits JSON Lines. I found no reconnectable client API for an ongoing desktop turn. [CLI entry point](windows/src/Mason.Cli/Program.cs) | Share a local task/event API between desktop, CLI and editor. Notify on approvals or completion. Cross-device clients, cloud execution and team administration are separate product choices. |
| External knowledge | Open as a general coding tool. Model search and network-brokered commands exist. [Available tools](windows/src/Mason.Code.Core/CodeSchema.cs) | Add opt-in documentation retrieval with URL, version and excerpt citations. Prefer locally installed SDK docs when the project runs offline. |

## Recommended build order

1. **Pair items 3 and 4.** On an existing multi-package repo, show which files and tests justify each milestone. Interrupt the run, change a dependency, and verify Mason updates its plan from the current checkout. Use a held-out task from item 6 to score the result.
2. **Make it useful from the developer's seat.** Start with preview-element and console handoff, then editor selections and diagnostics. Test by selecting a broken control, making an unrelated unsaved edit, and verifying the control in the running app.
3. **Make long work survive the window.** Build the local task service before scheduling multiple workers. Close and reconnect the desktop during a task that needs approval. Verify that the same task resumes once and preserves its evidence.
4. **Give local models distinct jobs and measure the difference.** Compare a single Qwen run with Qwen implementing and a compatible LFM vision model inspecting the result. Use the same starting revision, acceptance checks and resource budget. Show the result, elapsed time and peak GPU memory before choosing a default.

The comparison covered documented capabilities in [Claude Code extensions](https://code.claude.com/docs/en/features-overview), [custom subagents](https://code.claude.com/docs/en/sub-agents), [project memory](https://code.claude.com/docs/en/memory), the [Codex app server](https://developers.openai.com/codex/app-server), [Codex worktrees](https://developers.openai.com/codex/app/worktrees), [VS Code browser feedback](https://code.visualstudio.com/docs/agents/run/browser-tools) and the [Model Context Protocol architecture](https://modelcontextprotocol.io/docs/learn/architecture). The build order reflects Mason's local Windows and GPU constraints.
