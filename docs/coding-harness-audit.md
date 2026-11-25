# Mason Agent coding harness audit

November 23, 2026. Scope includes the Windows desktop coding loop, JSON and native model adapters, workspace tools, local session journal, and release checks. The reported incident used LFM3-Omni-2B-BF16 in Build, Auto, with an 80,128-token context.

## Incident and corrective changes

The saved 1.7.4 session contains two syntactically valid JSON actions. Their directory arguments were `.}}` and `food-bear}}`. The workspace sandbox denied earlier drive-qualified paths. In this incident the directory lookup failed and the retry budget expired. The earlier test supplied a correct next action after the first failure. It therefore missed the observed second failure.

| Area | What changed | How to verify |
| --- | --- | --- |
| Workspace location | An inventory with a known single directory constrains listing, search, and command working directories to the root. New files use an existing parent. A read-only recovery handles malformed root aliases after checking for a real literal directory. Ambiguous creates return a correction. An intentional same-name child can be created with an explicit relative path. | Replay both captured arguments, inspect receipts and disk, then test a real brace-suffixed directory and an explicitly nested project. |
| Workspace observations | After file edits, directory creation, or commands, the next action sees refreshed files and repository instructions. An exact-size listing is complete. Depth and file limits are visible when they omit entries. | Create a file and a subdirectory mid-run and inspect the following model input. Exercise an exactly full inventory and one beyond the recursion limit. |
| Tool selection | The host generates the tool guide from the active capabilities. On JSON connections explicit finish and the legacy null response share a completion guard. Repeated reads of the same bytes through either read or open return the prior receipt ID and hash. The next model decision omits both aliases. | Compare Ask, Auto and reviewed schemas. Submit an explicit finish before delivery and after inspecting an existing file in Ask. Alternate read and open on the same file. |
| Recovery | Failed continuations receive the current workspace state. Repeated failures across alternating calls are bounded. Rejected responses retain individual receipts, with two repair attempts per consecutive series. On a JSON repair the schema limits the size of a new file action. | Run the alternating failure trace, reject three responses in a row, and verify that a valid action resets the repair budget. |
| Verification | One successfully executed command cannot erase another command's failure. A successful rerun of the same command clears that record. The final transcript and state flag unresolved command failures. | Run a failing check followed by an unrelated passing command. Repeat after fixing the fixture and rerun the original check. |
| Regression gates | The deterministic recovery suite runs in Windows CI and before local or tagged release packaging. The storefront evaluator supports a fresh run and import of model-visible history into a disposable project. An independent Edge check inspects rendered content at two widths. | Run the commands below. The real-model and browser evaluations use local weights and a separately supplied browser. |

## Architecture review

| Capability | Evidence in Mason | Remaining limit |
| --- | --- | --- |
| Tool authority | `CodeSchema` filters the model's choices. `CodeEngine`, `CodeWorkspace`, and the command runtime enforce permissions at execution. Auto has a Windows process sandbox and scoped network review. | Reviewed commands intentionally run with the user's Windows account. Changing permission mode changes that boundary. |
| Context and continuation | `CodeContext` measures the loaded context, reserves action space, and binds checkpoints to a hash of the retained history. User requests, changes, and selected receipts survive compaction. | Large protected inputs can still exceed a small model's usable context. Successful compaction does not establish that a model retained every task requirement. |
| Interruption and auditability | `CodeStore` writes an atomic session snapshot. On restart it marks outstanding operations as having an unknown outcome. The transcript retains intent, approval, and result. | A crash between an external command's effect and its recorded receipt still requires inspecting the workspace. |
| Task completion | Build pins acceptance criteria to the request before implementation. Auto can call `verify_acceptance`. Finish reruns the criteria against the current workspace, records per-feature results in Activity and withholds completion on missing evidence. | Coverage for free-form requirements depends on model interpretation. The transcript exposes the criteria. Unsupported requirements remain unverified. |
| Browser verification | Isolated Edge checks accessible headings, visible text, click-driven changes and horizontal layout at 390 and 1,280 pixels. The host serves bounded workspace assets at a synthetic HTTPS origin. | This verifier covers static local HTML and its assets. Server-backed navigation, external services, subjective design and multi-step workflows require additional evidence. |
| Evaluation coverage | The repository has deterministic protocol, compaction, permission and interruption checks plus real task evaluators. The storefront check exercises both fresh and previously blocked conversations. | Success on this task does not quantify reliability across models, longer changes, or interactive checkout flows. A cross-model, multi-trial release benchmark remains open. |

## Release verification

### Mason Agent 1.8.0

The Windows suite exercised 374 checks. The feature acceptance evaluator asked LFM3-Omni-2B-BF16 to build a page with a named heading and a button that reveals a confirmation. The first implementation displayed the confirmation before the click. The host rejected completion, supplied its observations to the model and reran both checks on the repaired page. A disposable copy with a disconnected button retained its passing heading and failed the click check.

The evaluator also submits a free-form request covering three distinct products, a quantity discount and checkout. It records the model's proposed criteria and verifies them against the incomplete page. Explicit labels can bind directly to browser assertions. Other wording depends on the planner's interpretation and coverage review. Unsupported observations remain visible in the feature checklist.

The in-place Windows upgrade from 1.7.9 to 1.8.0 preserved all 21 saved profile files checked before and after setup. All 790 installed payload hashes and all 790 portable archive entries matched the release manifest. The installed app reopened its main window. A separate runner verified the installed engine, model adapter, Playwright assembly and driver identity, passed 42 focused checks and ran two fresh trials with the local BF16 model at 80,128 tokens. Both trials rejected the initial implementation, accepted the model's repair on both viewport sizes and rejected the broken-button and incomplete-store controls.

### Mason Agent 1.7.9

Mason Agent 1.7.9 passed 367 Windows coding checks. After an in-place upgrade from 1.7.4, all 669 installed payload hashes matched the release manifest. A separate runner verified the installed engine and model-adapter assembly hashes before running the 37 focused recovery checks.

Using those same release binaries and LFM3-Omni-2B-BF16 at 80,128 tokens, four isolated trials completed. Two started fresh and two resumed from the saved failed conversation. All four produced HTML directly in the selected project. Both continuations recovered from an incomplete response and a denied attempt to recreate the project. Edge rendered visible bear-store content at both 390 and 1,280 pixels in every trial, with no page-script exceptions. These trials exercise one task, model, runtime, and permission configuration.

## Reproduce

```powershell
dotnet run --project windows/tests/Mason.CodeChecks -c Release -- --harness-recovery-checks $env:TEMP
dotnet run --project windows/tests/Mason.CodeChecks -c Release -- --evaluate-acceptance $endpoint $modelId $freshAcceptanceTrial
dotnet run --project windows/tests/Mason.CodeChecks -c Release -- --evaluate-empty-project $endpoint $modelId $freshTrial
dotnet run --project windows/tests/Mason.CodeChecks -c Release -- --evaluate-empty-project $endpoint $modelId $resumeTrial $savedSessionJson
node windows/tests/browser/check-bear-store.cjs $chromiumExecutable $freshTrial $resumeTrial
```

Use a fresh directory for each trial and a local endpoint hosting the selected weights. The optional saved session supplies model-visible history and its terminal state. All new tools run in the disposable project. The built-in acceptance evaluator uses the packaged Playwright driver and installed Microsoft Edge. It writes the passing run to `report.json`, the disconnected-button result to `negative.json` and the three-feature assessment to `coverage.json`. The separate Node storefront evaluator needs its own Playwright installation.

The comparison used published engineering practices for making the running application observable, preserving progress across context windows, and scoring the resulting environment over repeated isolated trials. [OpenAI on harness engineering](https://openai.com/index/harness-engineering/) · [Anthropic on long-running harnesses](https://www.anthropic.com/research/effective-harnesses-for-long-running-agents) · [Anthropic on agent evaluations](https://www.anthropic.com/research/demystifying-evals-for-ai-agents)
