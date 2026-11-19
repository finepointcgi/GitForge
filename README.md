# GitForge

A fast, keyboard-first Git client for Windows, built with C#/.NET 10, Blazor, and MudBlazor. It is designed as a lightweight replacement for Sourcetree, with GitHub, GitLab, Bitbucket, and Azure DevOps integration (cloud and self-hosted).

- **Graph-centric workspace:** refs on the left, the commit graph in the center (with a WIP row for uncommitted work, always at the top and joined to your branch by a dashed line), and a commit panel on the right for staging, messages, and commit details.
- **Fast on big repositories:** streamed history, incremental graph layout, and virtualized lists and diffs.
- **High-velocity staging:** hunk- and line-level stage, unstage, and discard from the keyboard, including new (untracked) files.
- **Stashes you can see into:** click a stash to preview its files and diffs, then apply, pop, or drop it. Stash just a hunk or selected lines (like `git stash push --patch`); popping them back works even when the file still has your other edits.
- **Multi-select commits:** Ctrl+click or Shift+click (Shift+↓/↑ from the keyboard) to cherry-pick several commits at once, oldest first, or compare any two commits (or a commit with your working tree).
- **Drag and drop branches:** drop one branch on another, in the sidebar or the graph, to merge or rebase.
- **Customizable keyboard shortcuts:** rebind any command, with conflict warnings (press ?, or Settings › Customize keyboard shortcuts).
- **Undo/redo (Ctrl+Z / Ctrl+Y):** commit, checkout, branch create/delete, reset, and discard (including untracked files).
- **Command palette (Ctrl+P):** fuzzy search across actions, branches, and recent repositories; hide or solo branches in the graph.
- **Repository tabs:** several repositories open at once (Ctrl+Tab to switch, Ctrl+W to close); tabs are restored on launch.
- **Built-in conflict editor:** Mine / Base / Theirs side by side for every conflict; take mine, theirs, both, base, or edit by hand, then save and mark resolved.
- **File history and blame:** commits that touched a file (following renames) and who last changed each line, at the working tree or any commit.
- **Syntax highlighting** in diffs and blame using VS Code's TextMate grammars, computed only for the lines on screen.
- **Git LFS:** LFS file badges, tracked patterns, and server file locks (lock, unlock, force-unlock with confirmation).
- **Automatic recursive submodules:** after checkout, pull, merge, rebase, reset, or undo, submodules whose recorded commit changed are synced and updated (`--init --recursive`); submodules with local changes are never overwritten. Uninitialized submodules are initialized on open. Toggle in **Settings**.
- **Submodule safety:** a push that points to submodule commits that aren't on the submodule's remote is stopped, with an offer to push the submodule branch holding them first. Committing on a detached HEAD (usual inside a submodule) shows a warning with **Create a branch here**. A changed submodule shows the commits it brings in or drops. Add, remove, update one (at any depth), or move submodules to the newest commit on their remote branch.
- **Worktrees:** check out another branch in its own folder (**New worktree…**, or right-click a branch). Branches checked out in another worktree are marked, and double-clicking one opens its worktree.
- **Bisect:** right-click a commit › **Bisect** to mark it good or bad; the banner tracks progress (Good, Bad, Skip) until git names the first bad commit.
- **Patches and bundles:** save commits or uncommitted changes as .patch files and apply them (commits keep their author and message); create and fetch from bundles to move work between networks without a connection.
- **File right-click menu:** open, open in editor, show in Explorer, copy path, diff tool, history, blame, add to .gitignore, stop tracking, resolve using mine or theirs, or restore a file to a commit's version. GitForge never runs programs or scripts from a repository; those open in the editor.
- **External tools:** terminal and Explorer buttons; editor, diff tool (`git difftool`), and merge tool in **Settings › External tools**.
- **Advanced Git:** visual interactive rebase, reflog recovery, conflict tools, submodules.
- **Multi-account hosting:** tokens in Windows Credential Manager, repository browser, pull request list.
- **Forest-green dark theme** matching the GitForge icon. The accent color defaults to GitForge green (a darker green in light mode, for contrast) and can be changed in **Settings**.
- **Automatic updates:** installed copies check once a day and show **Update to x.y.z** in the status bar; updating downloads a small delta, verifies it, and restarts with your tabs. IT can pin the update source (including an offline share) or turn updates off by policy.
- **Secure by default:** no telemetry (the update check sends only the app version and platform), HTTPS APIs by default (plain HTTP only for self-hosted servers you explicitly configure that way), no plaintext secrets on disk, hooks and signing honored.

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for design and data models, [docs/ROADMAP.md](docs/ROADMAP.md) for the phased plan, and [docs/RELEASING.md](docs/RELEASING.md) for releases, code signing, and update policy.

## Install

Download `GitForge-win-Setup.exe` from [Releases](https://github.com/finepointcgi/GitForge/releases) and run it. It installs for the current user (no administrator rights) and adds GitForge to the Start menu. Releases are self-contained, so no .NET runtime is needed. For deployment tools, use `GitForge-win.msi`; to check a download, compare it with `SHA256SUMS.txt` on the same release.

GitForge updates itself. **Settings › Updates** shows the version, turns the daily check off, or points to another update feed (an https mirror or a folder / UNC share for disconnected networks). **Check for updates…** in the command palette checks immediately.

## Requirements

- Windows 10 version 1809 or later, or Windows 11 (x64), with the WebView2 runtime
- Git for Windows 2.39 or later on `PATH` (Git Credential Manager recommended for HTTPS remotes)
- .NET 10 SDK, to build from source

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

Open a specific repository on launch (for example, from an Explorer shortcut):

```bash
GitForge.exe C:\src\my-repo
```

Set `GITFORGE_SETTINGS` to a JSON file path to use alternate settings (portable installs or isolated test runs).
Set `GITFORGE_TRACE_STARTUP` to a log file path to record startup milestones and step durations (first two minutes only).

Browser dev host for UI work (loopback only, http://127.0.0.1:5199):

```bash
dotnet run --project src/GitForge.DevHost
```

## Keyboard shortcuts

| Keys | Action |
|---|---|
| Ctrl+P | Command palette |
| Ctrl+Tab / Ctrl+Shift+Tab (or Ctrl+PgDn / PgUp) | Next / previous repository tab |
| Ctrl+T / Ctrl+W | Open a repository in a new tab / close the tab |
| m / t / b, n / p, Ctrl+S | Conflict editor: take mine / theirs / both, next / previous conflict, save |
| Ctrl+Z / Ctrl+Y | Undo / redo the last action |
| Ctrl+1 / Ctrl+2 / Ctrl+3 | Select WIP (stage and commit) / Commit graph / Reflog |
| Ctrl+4 | Changes on the current branch since it branched (commits, files, and combined diff) |
| Ctrl+H | Go to the current branch in the graph |
| Esc | Close the diff, reflog, or branch changes and return to the graph |
| Ctrl+O / Ctrl+Shift+O | Open / Clone repository |
| Ctrl+Shift+F / L / P | Fetch / Pull / Push |
| Ctrl+B | New branch |
| Ctrl+Shift+S | Stash |
| Ctrl+Enter | Commit |
| Ctrl+` | Git console |
| j / k, gg / G | Next / previous, first / last (graph, commit panel, or diff) |
| Shift+↓ / Shift+↑ (or Shift+J / Shift+K) | Extend the commit selection in the graph (Esc clears it) |
| h / l | Focus graph / commit panel |
| s / u / d | Stage / unstage / discard (file, hunk, or selected lines) |
| Space | Toggle line selection in a diff |
| / or Ctrl+F | Filter commits |
| ? | Show and customize all shortcuts |

These are the defaults; every shortcut can be changed or removed (press **?**). Commands without a default (bisect, patches, bundles, worktrees, submodules, terminal, Explorer, editor, and actions on the selected commit) can be given keys there, and all of them are in the command palette. Single-key shortcuts work outside text fields while vim-style navigation is on in **Settings**.

Branch folders (feature/, fix/, chore/ …) start collapsed, except the one holding the current branch; the filter at the top of the sidebar stays in view and opens every folder with a match. Click a branch to jump to its commit; double-click to check it out; drag it onto another branch to merge or rebase. Hover a branch to hide it from the graph, or right-click to solo it. Click a stash to preview it. Right-click branches, tags, stashes, and commits for more actions.

Drag the borders in the graph header to resize columns; double-click a border, or right-click the header and choose **Reset column widths**, to restore defaults. Widths are remembered. To put the graph left of the branch labels (Sourcetree's order), right-click the header and choose **Graph before Branch / Tag**.

A **remote deleted** label means the branch's copy on the remote was deleted (usually after its pull request merged) and a fetch removed it. Local commits are untouched. Right-click to push it again, stop tracking, or delete it. **Clean up** in the toolbar (its badge counts these branches), or the note at the top of the Local list, deletes only those whose commits are already in the current branch.

## Hosting accounts

Open **Accounts** in the toolbar, choose a provider, and paste a personal access token. GitForge validates the token, then stores it only in Windows Credential Manager.

| Provider | Credential | Notes |
|---|---|---|
| GitHub / GitHub Enterprise Server | Fine-grained or classic PAT | Enterprise: enter the server URL; API root is `/api/v3`. |
| GitLab / self-managed | PAT with `read_api` | API root is `/api/v4`. |
| Bitbucket Cloud | Atlassian API token + account email, or an access token (leave email empty) | App passwords are retired; repositories are listed per workspace. Data Center is planned. |
| Azure DevOps Services / Server | PAT with Code (Read) | Cloud requires the organization name; Server takes the collection URL. |

Self-hosted base URLs may be `https://` or `http://`. Cloud providers always use HTTPS. An `http://` URL triggers a warning and a confirmation, because the token then travels unencrypted; such accounts are tagged **HTTP** in the list. GitForge sends plain HTTP only to that account's own host and port, and never downgrades an HTTPS account.

Git operations (fetch, pull, push, clone) use your normal Git credential setup, not these tokens.

## License

Apache License 2.0. See [LICENSE](LICENSE).
