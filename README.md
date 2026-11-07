# Bentar Second Brain

A Windows app that gives your PC and every AI agent on it one shared, local **second brain** (file search + long-term memory over MCP), together with safe PC cleanup and private GitHub backup.

## PC cleanup

The app scans your drives and sorts the results into 4 groups:

1. **Safe to delete**: Recycle Bin, temp files, browser caches (Chrome, Edge, Cốc Cốc), npm-cache, crash dumps. The app cleans these itself and can be scheduled to run at every startup.
2. **Needs your review**: large files, duplicates, old backups, installers. You choose to move them to the Recycle Bin or to drive D.
3. **Keep**: Windows, installed software, browser profiles, personal documents. The app never touches these.
4. **Unused folders**: each folder is rated by its latest file change, Windows recent-file history and traces left by AI tools. You tick what to delete.

## Language and theme

The app starts in English with a dark theme. Open **Settings** (left sidebar) to switch to **Vietnamese** and/or the **Light** theme, click **Apply**, and the app restarts. The choice is saved in `%LOCALAPPDATA%\BentarSecondBrain\ui.ini` and also applies to the MCP server / power station (messages returned to agents follow the chosen language). The 3D brain map always keeps its night-sky background.

## Build

Nothing to install: it uses the C# compiler that ships with Windows (.NET Framework 4.x):

```powershell
& C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /codepage:65001 /optimize /win32icon:src\app.ico "/out:Bentar Second Brain.exe" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll /r:Microsoft.VisualBasic.dll /r:System.Web.Extensions.dll /r:System.IO.Compression.dll /r:System.Security.dll src\*.cs
```

## Install and uninstall

The first time you open the `.exe`, the app asks whether to install it (no admin rights needed). It copies itself to `%LOCALAPPDATA%\Programs\Bentar Second Brain`, creates Start Menu and Desktop shortcuts, and appears in **Settings → Apps**. Scheduled cleanup and all agents use this installed copy.

Uninstall from **Settings → Apps** (or run `"Bentar Second Brain.exe" /uninstall`). The app removes only what it installed: program files, shortcuts, scheduled tasks and the `second-brain` entry in agent configs. **Your files, documents and projects are never touched**; settings, logs and the index are kept in `%LOCALAPPDATA%\BentarSecondBrain`.

## Updates

Click **Check for updates** in the app. It reads the latest release from this repo's GitHub Releases; if a newer one exists, it downloads it, closes, replaces the old file and reopens. On every launch it also checks quietly and turns the button yellow when an update is available.

Publishing a new version:

1. Bump `AssemblyVersion` at the top of `src/BentarSecondBrain.cs`, e.g. `2.10.0.0`.
2. Run the release script, which builds, zips and publishes to GitHub Releases:

```powershell
.\release.ps1 -Version 2.10.0 -Title "Short title" -Notes "What's new..."
```

## Second Brain for agents

The app indexes every drive on the machine (hard drives, USB sticks, memory cards): file names and the contents of text files, source code and Word (.docx) documents. Search ignores Vietnamese accents. When a USB stick or card is removed, the index keeps what it already knew.

Agents in any project can query it through the MCP server (`BentarSecondBrain.exe /mcp`). Click **Connect agents** on the Second Brain tab (or run `BentarSecondBrain.exe /connect`). The app plugs Second Brain into every agent it finds: Claude Code, Claude Desktop, Cursor, Windsurf, Codex, Gemini CLI. Old config files are backed up as `*.bak-secondbrain`. Quit Claude Desktop completely before connecting, since it overwrites its config while running. For Claude Code you can also do it manually:

```powershell
claude mcp add --scope user second-brain -- "$env:LOCALAPPDATA\Programs\Bentar Second Brain\Bentar Second Brain.exe" /mcp
```

Tools available to agents: `search`, `list_projects`, `project_overview`, `read_file`, `list_dir`, `copy`, `index_status`, plus the memory tools `recall`, `remember`, `revise_memory`, `connect`.

### Memory that learns like a brain

Second Brain is long-term memory shared by all agents, stored in `%LOCALAPPDATA%\BentarSecondBrain\tri-nho.json` (your files are never touched):

- **Neurons**: every memory (decision, lesson, fact, todo, note), every project and every file that has been used.
- **Synapses**: weighted links. A new memory links itself to its source project/file and to related memories.
- **Self-learning**: when agents read files, copy documents, use several projects in one session, or recall ideas together, those links get stronger.
- **Fades, never deleted**: unused memories and links slowly fade (90–120 day half-life); memories marked outdated are kept but ranked lower.
- **Associative recall**: `recall` returns matching memories and spreads to connected ideas; `search` and `project_overview` also include related memories.
- Near-duplicate memories reinforce the existing one instead of duplicating it; old versions stay in history.

### Brain map

The **Brain** tab (opened first) draws the actual neurons and synapses in memory:

- The 3D brain is a cloud of glowing particles joined into a mesh, gently rotating with perspective; a star network behind it turns more slowly. Drag to rotate, scroll to zoom. Agent memories sit in the frontal lobe, colored by type (decision, lesson, fact...); projects in the parietal lobe, files in the temporal lobe.
- Neurons grow bigger and brighter with use and dim when idle; link thickness follows synapse strength, dotted lines join projects in the same folder.
- Pulses travel along links; when an agent actually recalls, reads or writes, that neuron flashes and the pulse spreads to related ideas.
- Type a keyword and press Enter to highlight matches and open the best match's card; click a neuron to inspect it, double-click to reveal it in Explorer.

Second Brain **never edits or deletes files**. `copy` only creates a new copy and refuses to overwrite. Skipped when indexing: system folders, AppData, `node_modules`, `.git`, virtual environments, and the contents of secret files (`.env`, keys, credentials).

The index refreshes in the background when an agent queries it (if older than 30 minutes), or click **Update index**, or run `BentarSecondBrain.exe /index`.

## Power station: one brain, every agent plugs in

Second Brain is the power source; agents and tools are the light bulbs. Once installed, the power station (`BentarSecondBrain.exe /serve`) starts in the background at boot and offers three kinds of sockets to the same brain:

| Socket | For | Address |
|---|---|---|
| MCP stdio | Claude Code, Codex, Gemini CLI, Cursor... | `"Bentar Second Brain.exe" /mcp` |
| MCP over HTTP | Agents that support remote MCP | `POST http://127.0.0.1:7801/mcp` |
| REST + OpenAPI | n8n, scripts, browser extensions, custom agents | `GET/POST http://127.0.0.1:7801/api/<tool>`, described at `/openapi.json` |

HTTP sockets listen on this machine only. Every request needs a token (`Authorization: Bearer <token>`) found in `%LOCALAPPDATA%\BentarSecondBrain\o-cam.json`; foreign host names pointing at the machine are blocked. Name a device with the `X-Client-Name` header. Every connection and call is logged to `hoat-dong.jsonl`.

## Keep your brain for life: private GitHub backup

On first launch the app asks: restore an existing brain, create a new one and back it up to GitHub, or decide later (the app shows a red warning until a backup exists).

- The brain is backed up to a private GitHub repo; every backup is a commit, so every version is kept forever.
- **No password by default**: the repo holds `tri-nho.json` (raw data) and `tri-nho.md` (readable copy). Restoring only needs a GitHub sign-in and picking the repo. Anyone with access to the GitHub account or repo can read the memory, so keep the repo private.
- **Extra encryption is optional** (checkbox during setup): `bo-nao.sbak` is encrypted with a recovery key, and `khoa.enc` is that recovery key encrypted with your password. Both use the OpenSSL format, so they can be opened without the app (`openssl enc -d -aes-256-cbc -pbkdf2 -iter 200000 -md sha256 ...`). **Remove encryption** switches back to plain mode.
- The power station backs up automatically when memory changes (checked every 10 minutes) and test-restores the backup weekly; on errors, or after 3 days without a backup, the app shows a red warning.
- New or lost PC: install the app, choose **Restore**, sign in to GitHub and pick the repo marked ★ (a password is needed only for encrypted backups). The file index is rebuilt on the new machine.
- Test without touching your real brain: set the `BSB_DATA_DIR` environment variable to another folder.

## Command-line options

- `/auto`: silent cleanup using saved settings (used by Task Scheduler).
- `/report <file>`: scan only, delete nothing, and write the classification to a file.
- `/mcp`: run the MCP server over stdio for coding agents.
- `/index`: update the Second Brain index and exit.
- `/connect`: install into AppData and connect Second Brain to every agent on the machine.
- `/install`: install on this machine.
- `/uninstall [/quiet]`: uninstall, keeping your data.
- `/serve`: run the power station (HTTP sockets, automatic backup).
- `/backup-prepare [repo name]`: create / check the private backup repo on GitHub, printing each step (never touches passwords).
- `/backup-setup <repo> [password]`, `/backup`, `/backup-verify`, `/restore <repo> [password or recovery key]`, `/backup-plain`: backup and restore from the command line.
