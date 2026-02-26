# GitCopilot

`GitCopilot` is a .NET file-based CLI that combines GitHub Copilot SDK with a git-first REPL workflow for generating commit messages, committing, and optionally pushing.

It supports:
- GitHub-hosted routing (default cloud profile)
- Local OpenAI-compatible routing (LM Studio, Ollama-compatible gateways, local proxies, etc.)
- Interactive REPL with commit tooling (`/status`, `/diff`, `/commit`, `/amend`, `/push`, `/undo`)
- Extended REPL workflows (`/log`, one-shot `/attach <file>`, interactive `/add` selector)
- Dry-run simulation mode
- Local commit-message cache keyed by git diff context
- Interactive model picker (`/model`) for OpenAI-compatible profiles
- Optional MCP server configuration (global + per-profile merge)

---

## Requirements

- .NET SDK (modern version that supports file-based apps and `#:package` directives)
- Git installed and available on PATH
- GitHub Copilot access for cloud profile usage
- Optional: local OpenAI-compatible endpoint (LM Studio, etc.)

---

## Quick Start (Run from Source)

From repository root:

```bash
dotnet run copilot.cs -- --help
```

One-shot prompt mode:

```bash
dotnet run copilot.cs -- --profile cloud "Summarize current changes"
```

REPL mode (recommended for git commit workflow):

```bash
dotnet run copilot.cs -- --repl --profile cloud
```

> Important: REPL mode is designed to run at repository root.

---

## Configuration

Configuration is read from:

- `./.gitcopilot-config.json` (workspace-local)

If this file is missing and you run from inside a git repository, GitCopilot auto-creates a default `.gitcopilot-config.json` at repository root.

### Example

```json
{
  "Profiles": {
    "cloud": {
      "ProviderType": "GitHub",
      "McpServers": {
        "jira": {
          "Command": "npx",
          "Args": ["-y", "@modelcontextprotocol/server-jira"]
        }
      }
    },
    "lmstudio": {
      "ProviderType": "OpenAICompatible",
      "BaseUrl": "http://localhost:1234/v1",
      "ApiKey": "lm-studio",
      "Model": "openai/gpt-oss-20b",
      "RequestTimeoutSeconds": 300,
      "ModelDiscoveryTimeoutSeconds": 30,
      "McpServers": {
        "localdocs": {
          "Url": "http://localhost:8787/mcp"
        }
      }
    }
  },
  "DefaultProfile": "cloud",
  "McpServers": {
    "github": {
      "Command": "npx",
      "Args": ["-y", "@modelcontextprotocol/server-github"]
    }
  },
  "Git": {
    "AutoPush": false,
    "RequireConfirmations": true,
    "ProtectedBranches": ["main", "master"],
    "UseConventionalCommits": true,
    "DefaultCommitType": "chore",
    "CustomTemplate": "{{message}}",
    "EnableCommitMessageCache": true,
    "CommitMessageCacheFile": ".gitcopilot-cache.json",
    "DryRunDefault": false
  },
  "Repl": {
    "Prompt": "gitcopilot> ",
    "MaxDiffCharacters": 12000,
    "MaxAttachmentCharacters": 8000
  }
}
```

### Key Fields

#### `Profiles`
- `ProviderType`: `GitHub` or `OpenAICompatible`
- `BaseUrl`: required for OpenAI-compatible profile
- `ApiKey`: optional for local providers
- `Model`: optional preferred model; if omitted, tool discovers from `/models`
- `RequestTimeoutSeconds`: optional per-profile request timeout override
- `ModelDiscoveryTimeoutSeconds`: optional `/models` discovery timeout override
- `McpServers`: optional per-profile MCP servers (merged over global MCP servers)

#### `McpServers` (root-level)
- Global MCP server map applied to all profiles
- Profiles may override/add servers with the same server key
- Each server supports `Url` (HTTP endpoint) or `Command` + optional `Args` and `Env`

#### `Git`
- `AutoPush`: auto-run push after successful commit/amend
- `RequireConfirmations`: prompt before mutating operations
- `ProtectedBranches`: deny push to these branches
- `UseConventionalCommits`: apply conventional commit formatting
- `DefaultCommitType`: fallback type when generated subject is non-conventional
- `CustomTemplate`: optional template; supports `{{message}}`, `{{branch}}`, `{{date}}`
- `EnableCommitMessageCache`: enable diff-based message cache
- `CommitMessageCacheFile`: cache file path (relative to repo root unless absolute)
- `DryRunDefault`: start REPL in dry-run mode

#### `Repl`
- `Prompt`: custom REPL prompt
- `MaxDiffCharacters`: diff truncation limit for model prompt payload
- `MaxAttachmentCharacters`: max size for one-shot `/attach` file context

---

## REPL Commands

- `/help` — show command help
- `/status` — `git status --short --branch`
- `/diff [--staged|--unstaged]` — show diff (defaults to staged if present)
- `/add [paths]` — stage files (default `.`)
- `/add [paths]` — stage files; when omitted opens interactive file picker
- `/reset [paths]` — unstage files (default `.`)
- `/commit` — generate commit message and commit staged changes
- `/amend` — generate message and amend latest commit
- `/push [remote] [branch]` — push with policy checks
- `/log [count]` — show recent commits (default 10)
- `/attach <file>|clear` — attach one-shot context file for next `/commit` or `/amend`
- `/model` — list available models and pick active one (OpenAI-compatible profiles)
- `/dry-run [on|off|toggle]` — toggle/force simulation mode for mutating commands
- `/cache [stats|clear]` — cache diagnostics or clear cache
- `/undo` — `git reset --soft HEAD~1` (with confirmation policy)
- `/exit` — exit REPL

Tip: entering plain text (without `/`) stores a style hint for next `/commit` or `/amend`.

---

## Install for Global Invocation

After install, you can invoke:

```bash
gitcopilot
```

### Windows

Run from repository root:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\install-gitcopilot.ps1
```

Optional AOT install:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\install-gitcopilot.ps1 -Aot
```

Strict AOT (fail instead of fallback):

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\install-gitcopilot.ps1 -Aot -ForceAot
```

This script:
- runs `dotnet build -c Release`
- publishes release output
- optionally publishes AOT (`-Aot`) with fallback to standard Release publish unless `-ForceAot` is set
- installs executable under `%LOCALAPPDATA%\gitcopilot`
- creates `gitcopilot.cmd` shim
- adds install `bin` directory to user PATH
- runs a strict post-install launcher self-check (`gitcopilot --help`) and fails if it does not execute

### Linux/macOS (bash)

Run from repository root:

```bash
bash ./scripts/install-gitcopilot.sh
```

Optional AOT install:

```bash
bash ./scripts/install-gitcopilot.sh --aot
```

Strict AOT (fail instead of fallback):

```bash
bash ./scripts/install-gitcopilot.sh --aot --force-aot
```

Custom install/runtime:

```bash
bash ./scripts/install-gitcopilot.sh --install-root "$HOME/.local/share/gitcopilot" --runtime linux-x64
```

This script:
- runs `dotnet build -c Release`
- publishes release output
- optionally publishes AOT (`--aot`) with fallback to standard Release publish unless `--force-aot` is set
- installs under `~/.local/share/gitcopilot`
- creates launcher at `~/.local/bin/gitcopilot`
- appends PATH export to shell rc if missing
- runs a strict post-install launcher self-check (`gitcopilot --help`) and fails if it does not execute

> Open a new shell after installation.

---

## Operational Notes

- Mutating git commands respect dry-run mode.
- Commit generation cache prevents duplicate generation for identical context.
- On first cache write, GitCopilot checks `.gitignore` and auto-adds the cache file path if missing.
- Cache metadata stores a one-time `GitIgnoreCheckCompleted` marker to skip repeated `.gitignore` checks on future runs.
- `/model` updates active model for the current REPL session by recreating the underlying SDK session.
- `/attach` context is one-shot: it is applied to the next `/commit` or `/amend`, then cleared.
- `/add` without explicit paths opens a multi-select file picker for unstaged files.
- REPL enforces repository-root execution for safer and more predictable git behavior.

---

## Troubleshooting

### "Current directory is not a git repository"
Run the tool from repository root.

### "No model configured or discoverable"
- Ensure provider `BaseUrl` is correct
- Ensure endpoint supports `GET /models`
- Set explicit profile `Model` in config

### NativeAOT install fails (Windows)
- Install NativeAOT prerequisites (especially Visual Studio "Desktop development with C++").
- Retry AOT install, or use non-AOT install mode.
- Installer fallback now forces non-AOT publish settings when AOT fails (unless strict AOT mode is requested).

### Push blocked on protected branch
Adjust `Git.ProtectedBranches` in config if intentional.

### PATH changes not recognized
Open a new terminal session (or source your shell profile).

---

## Security Notes

- Keep API keys out of source control.
- Prefer environment-specific config management for sensitive values.
- Keep `RequireConfirmations` enabled in shared repositories.
