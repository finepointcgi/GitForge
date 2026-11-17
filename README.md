# GitForge

A fast, keyboard-first Git client for Windows, built with C#/.NET 10, Blazor, and MudBlazor. It is designed as a lightweight replacement for Sourcetree, with GitHub, GitLab, Bitbucket, and Azure DevOps integration (cloud and self-hosted).

- **Fast on big repositories:** streamed history, incremental graph layout, and virtualized lists and diffs.
- **High-velocity staging:** hunk- and line-level stage, unstage, and discard from the keyboard.
- **Advanced Git:** visual interactive rebase, reflog recovery, conflict tools, stash manager, submodules.
- **Multi-account hosting:** tokens in Windows Credential Manager, repository browser, pull request list.
- **Secure by default:** no telemetry, HTTPS-only APIs, no plaintext secrets, hooks and signing honored.

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for design and data models, and [docs/ROADMAP.md](docs/ROADMAP.md) for the phased plan.

## Requirements

- Windows 10 or 11 (x64) with the WebView2 runtime
- .NET 10 SDK
- Git for Windows 2.39 or later on `PATH` (Git Credential Manager recommended for HTTPS remotes)

## Build, test, run

```bash
dotnet build GitForge.slnx
```

```bash
dotnet test --solution GitForge.slnx
```

Desktop app (WPF + WebView2):

```bash
dotnet run --project src/GitForge.Desktop
```

Browser dev host for UI work (loopback only, http://127.0.0.1:5199):

```bash
dotnet run --project src/GitForge.DevHost
```

## Keyboard shortcuts

| Keys | Action |
|---|---|
| Ctrl+1 / Ctrl+2 / Ctrl+3 | File status / History / Reflog |
| Ctrl+O / Ctrl+Shift+O | Open / Clone repository |
| Ctrl+Shift+F / L / P | Fetch / Pull / Push |
| Ctrl+B | New branch |
| Ctrl+Shift+S | Stash |
| Ctrl+Enter | Commit |
| Ctrl+` | Git console |
| j / k, gg / G | Next / previous, first / last |
| h / l | Focus file list / diff |
| s / u / d | Stage / unstage / discard (file, hunk, or selected lines) |
| Space | Toggle line selection in a diff |
| / or Ctrl+F | Filter history |
| ? | Show all shortcuts |

Right-click branches, tags, stashes, and commits for more actions.

## Hosting accounts

Open **Accounts** in the toolbar, choose a provider, and paste a personal access token. GitForge validates the token, then stores it only in Windows Credential Manager.

| Provider | Credential | Notes |
|---|---|---|
| GitHub / GitHub Enterprise Server | Fine-grained or classic PAT | Enterprise: enter the server URL; API root is `/api/v3`. |
| GitLab / self-managed | PAT with `read_api` | API root is `/api/v4`. |
| Bitbucket Cloud | Atlassian API token + account email, or an access token (leave email empty) | App passwords are retired; repositories are listed per workspace. Data Center is planned. |
| Azure DevOps Services / Server | PAT with Code (Read) | Cloud requires the organization name; Server takes the collection URL. |

Git operations (fetch, pull, push, clone) use your normal Git credential setup, not these tokens.
