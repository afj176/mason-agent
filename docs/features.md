# What Mason Agent does

Everything here works in the Windows, macOS and Linux apps. Where a feature is reached differently on Windows, the entry says so. Commands in `code` also work in `mason`.

## Working on a project

- **Sessions by project.** Add a folder and Mason keeps its sessions, files and changes together. Watch each step as it happens.
- **Permissions.** Sessions start read-only. Choose Review edits (approve each edit and command), Workspace edits, or Auto. `/permission` changes it.
- **Auto runs in a sandbox.** Commands can only write inside the project, can't read credential folders such as `~/.ssh`, and have no network unless you turn it on with `/network ask`, and then each destination asks first.
- **Approvals you can save.** **Always Allow…** remembers a command, folder or tool for the project. Saved rules are listed in Settings, where you can revoke them.
- **Undo a whole request.** `/undo` puts back everything the last request changed, including files its commands created or deleted, and `/redo` reapplies it. Files you edited since are left alone.
- **Queue and steer.** While Mason works, queue a follow-up for after it finishes, or steer it to stop and do something else next.
- **Long sessions.** Older messages are summarized when the context fills, and the originals are kept. `/compact` does it now.
- **Fork, export and import.** Start a new session from before an earlier request, or save a session to a file and open it in another project.
- **Search.** Ctrl+K (⌘K on macOS) finds sessions, projects, files, commands and chats.

## Checking the work

- **Language servers.** After each edit, the language server for that file reports errors back to the model, so it fixes them before going on. `/languages` shows which languages are covered and installs a missing server.
- **Your tests.** Name your test command in `mason.workflows.json` and Mason runs it before and after a request that changes files, then tells you whether it passes now, broke, or was already failing. A failure goes back to the model to fix.
- **Pages in a real browser.** Mason can open a project page in a browser, click and type in it, and check it at phone and desktop widths. Write scenarios in `mason.acceptance.json` and ask Mason to verify them.
- **Comments and review.** Pin a comment to a line in a diff and it goes with your next request. **Review** has the model read the session's changes and comment on them.

## Models

- **Quick setup.** Checks your memory, installs llama.cpp and downloads a coding model that fits, after you confirm.
- **Your models.** Lists the model files on your computer, what each can do for the agent and whether it fits in memory. Search and download from Hugging Face with `/models`.
- **Other ways to connect.** Any OpenAI-compatible server on this computer, or Transformers weights through your own Python with PyTorch.
- **More than one model.** Use a second model for reviews and one that can see pictures, and set temperature and similar options per model.

## Your setup

- **Agents and commands.** Markdown files in `.mason/agents` and `.mason/commands`. Mason also reads the ones in `.claude/` and `.opencode/`.
- **Skills.** Instructions in `.mason/skills/NAME/SKILL.md` that the agent reads when the work calls for them. Mason also reads `.claude/skills` and `.agents/skills`, and skills from installed bundles. Settings lists every skill with where it comes from, switches one off for the project, and makes a new one from a template. `/skill NAME request` runs a request that follows a skill.
- **Tools.** Settings lists every tool the agent can use, by group, with a switch for each. `/tools` does the same in `mason`.
- **The web.** The agent can read a page (`web_fetch`) and search DuckDuckGo (`web_search`). It asks before each new site, reads public HTTPS sites only, and never sends cookies or passwords. **Always** keeps a site for the project. `/network off` turns the web off.
- **MCP servers.** Add a server on this computer and switch on the tools Mason may use. Every call asks first unless you saved a rule.
- **Notes.** Short facts about the project, kept in the Notes tray under the request box (the Notes tab, Ctrl+Shift+N on Windows and Linux, ⌥⌘N on macOS, or `/note TEXT`). Mason reads every current note, up to 12, with each request. A note can be about a file; when that file changes it is not sent until you keep it again. After a request Mason may suggest one note for you to Remember or Dismiss.
- **Themes.** Five theme families, each in light and dark, shared by the app and `mason`. Choose one in Appearance (the gear menu on Windows, Settings on macOS and Linux) or with `/theme`.

<picture><source media="(prefers-color-scheme: dark)" srcset="images/themes-dark.png"><img src="images/themes-light.png" alt="The same coding session in each theme family, light above dark: Blueprint, Evergreen, Graphite, Kiln and Retro Neon"></picture>

## Beyond one request

- **Chat.** Talk a problem through with a model, apart from your sessions, with documents and pictures attached. Chat can search and read the web, use skills and call your MCP servers, asking first; **Tools** in the request box switches each on or off. It never reads your project or runs commands. `mason chat` continues the same chats in the terminal.
- **Schedules.** Run a request hourly, daily, on weekdays or weekly. Nothing is approved for a scheduled run unless you allow Auto.
- **Notifications.** Your system tells you when a session finishes or needs your approval.
- **Background tasks.** Run a request in its own process that keeps going after you close the app, with its approvals waiting for you.
- **Experiments.** Copy the project into a Git worktree, try something there, and bring back only the changes you want.
- **Pull requests.** Build a draft pull request from the changes you choose and publish it through the GitHub CLI when you say so.
- **A window you attach.** Let Mason read and press the controls of another app's window. It asks before every action.
- **Code search by meaning.** With an embeddings server, the agent can find code from a description.
