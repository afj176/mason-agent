# The mason command line

`mason` is the same agent as the app, in your terminal. The macOS installer and the Linux packages put it on your PATH; on Windows, tick **Add the mason command to PATH** in the installer.

```sh
cd my-project
mason
```

No options are needed. `mason` uses a model that is already running, restarts the one you used last, or lets you pick one of your models, and offers to download one that fits if you have none.

Type a request and each step prints as it finishes. Approvals take one key: `y` once, `a` always for this project, `n` decline. Esc stops a run.

## Commands

| | |
| --- | --- |
| `/diff` · `/undo` · `/redo` | See what changed; put back or reapply everything the last request did |
| `/permission read\|review\|edits\|auto` | What Mason may do without asking. `auto` runs commands in your system's sandbox |
| `/ask` · `/build` | Answer questions only, or make changes |
| `/new` · `/resume` · `/compact` · `/status` | Start, continue, shrink or inspect a session |
| `/model` | Switch between running models |
| `/models` | The models on this computer and what each can do for the agent |
| `/models start` · `/models stop` | Run one of your models, with a context size that fits your memory |
| `/models search TEXT` · `/models download REPO` | Find and download a model from Hugging Face |
| `/models setup` | Install llama.cpp and download a model that fits |
| `/rules` · `/agent` · `/commands` | Saved approvals, and your agents and commands |
| `/skills` · `/skills new NAME` · `/skills on\|off NAME` | Every skill and where it comes from; make one; switch one for this project |
| `/skill NAME request` | Run a request that follows a skill |
| `/tools` · `/tools on\|off NAME` | Every tool by group; switch one for this project |
| `/network default\|ask\|off` | The web asks before each site; `ask` lets commands in Auto ask too; `off` is no internet |
| `/mcp` · `/bundle` · `/policy` | MCP servers, installed bundles and the project's policy |
| `/languages` | The language servers that check your code; install a missing one |
| `/notes` · `/note TEXT` | The notes Mason reads with every request; add one, `/notes about PATH TEXT`, `/notes remove N`, `/notes clear` |
| `/theme` | Choose the theme the app and `mason` share |
| `/help` | Everything else |

## Chat

`mason chat` is a plain conversation, apart from your project, that shares its history with the app's Chat. It can search and read the web, use skills and call MCP servers, asking first. `/attach FILE` adds a document or picture, `/reasoning` sets how long the model thinks, `/tools` switches web, skills and MCP, and `/resume` picks up an earlier chat. `mason chat --resume` continues the latest.

## Options

`mason --workspace PATH` opens another folder, `--resume` continues the last session, and `--read-only`, `--review`, `--edits` or `--auto` sets the permission. To use a server Mason didn't start, pass `--endpoint URL --model ID` for any OpenAI-compatible server on localhost.

Approval rules, models, agents and commands are shared with the app. Sessions started in the terminal are kept separately from the app's.

## In scripts

`mason exec` runs one request and prints its progress as JSON Lines. It is read-only unless you pass `--auto`.

```sh
mason exec --workspace . --endpoint http://127.0.0.1:8080 --model MODEL_ID --auto --prompt-file request.txt
```

`mason --help` lists the other operations.
