# GitForge Roadmap: MVP to V1.0

Status key: ✅ implemented and tested in this repository · 🟡 partially implemented · ⬜ planned.

## Phase 1 — Foundation and MVP (current state)

| Area | Item | Status |
|---|---|---|
| Platform | Layered solution (Core / Git / Platform / Hosting / UI / hosts), central package management, analyzers at zero warnings | ✅ |
| Platform | WPF desktop host (WebView2) and loopback-only dev host | ✅ (desktop app launch and render verified) |
| Git engine | libgit2 reads on a per-repo actor thread; git CLI for writes and network; debounced file watcher | ✅ |
| History | Streaming `git log`, incremental lane layout, virtualized 26 px rows, ref badges | ✅ |
| History | Instant filter: text, SHA, `author:`, `path:`, `since:`, `until:` | ✅ |
| History | Commit context menu: checkout, branch/tag here, cherry-pick, revert, reset soft/mixed/hard, copy SHA | ✅ |
| Staging | Staged/unstaged/conflicted lists; stage, unstage, discard (with confirmation) | ✅ |
| Staging | Hunk- and line-level stage/unstage/discard via generated patches (CRLF-safe) | ✅ |
| Diff | Virtualized inline and side-by-side views, ignore-whitespace toggle, large-diff guard | ✅ |
| Commit | Message box, amend, sign-off, push-after-commit, Ctrl+Enter | ✅ |
| Branches | Create, checkout, track remote, rename, delete/force, merge, rebase, upstream ahead/behind | ✅ |
| Remotes | Fetch (all, prune, tags), pull (merge/rebase), push (set upstream, force-with-lease), delete remote branch, auto-fetch | ✅ |
| Stash | Save (message, untracked, keep-index, staged-only), apply, pop, drop, conflict safety check | ✅ |
| Rebase | Interactive rebase dialog: reorder (drag or Alt+↑/↓), pick/reword/edit/squash/fixup/drop; continue/skip/abort banner | ✅ |
| Recovery | Reflog browser; dangling-commit scan (`git fsck`); one-click recovery branch | ✅ |
| Conflicts | Use ours/theirs, external merge tool (`git mergetool`), mark resolved | ✅ |
| Hosting | GitHub, GitLab, Bitbucket Cloud, Azure DevOps (cloud and server) accounts; token validation; keyring storage | ✅ |
| Hosting | Browse and clone account repositories; list open PRs/MRs; "create PR" deep links | ✅ |
| UX | Keyboard-first (Ctrl shortcuts + vim keys), resizable panes, dark/light/system theme, accent color | ✅ |
| UX | Graph-centric 3-pane layout: refs panel, graph with WIP row and Branch/Tag column, commit panel (summary/description) | ✅ |
| UX | Offline author-initial avatars on graph nodes (no Gravatar or network lookups) | ✅ |
| UX | Command palette (Ctrl+P): actions, branch checkout, recent repositories | ✅ |
| UX | Hide/solo branches in the graph; branch folders; click a ref to jump to its commit | ✅ |
| Safety | Single-level undo/redo: commit, checkout, branch create/delete, reset, discard (tracked and untracked) | ✅ |
| Platform | `GitForge.exe <path>` opens a repository; `GITFORGE_SETTINGS` selects an alternate settings file | ✅ |
| Console | Integrated git command console bound to the active repository | 🟡 (argv console; full PTY terminal in Phase 3) |
| Submodules | Recursive status tree; automatic sync + `update --init --recursive` after HEAD moves (changed pins only, dirty submodules skipped); init on open; manual update (all or one) | ✅ |

## Phase 2 — Power-user workflow (target: +6 weeks)

- ⬜ Syntax highlighting in diffs via TextMateSharp (tokenize visible rows only).
- ⬜ Built-in 3-way merge editor (base/ours/theirs with per-chunk accept) alongside external tools (KDiff3, Beyond Compare, VS Code).
- ⬜ Customizable key bindings; drag a branch onto another to merge or rebase.
- ⬜ File history and blame views; multi-select cherry-pick; compare any two commits/branches.
- ⬜ Stash preview pane and hunk-level stash (`git stash push --patch` equivalent via patches).
- ⬜ Line staging for new (untracked) files via intent-to-add.
- ⬜ Multi-repository tabs and workspaces.

## Phase 3 — Enterprise scale (target: +6 weeks)

- ⬜ Git LFS: tracked-file indicators, lock/unlock, locked-by badges (`git lfs locks --json`).
- ⬜ Submodules: detached-HEAD warnings inside submodules; optional `--remote` tracking updates.
- ⬜ Huge monorepos: `git status --porcelain=v2 -z` path with fsmonitor/untracked cache; sparse checkout and partial clone support; history paging beyond 1M commits.
- ⬜ OAuth device-flow sign-in (GitHub, GitLab) and Microsoft Entra ID for Azure DevOps, alongside PATs.
- ⬜ Bitbucket Data Center REST API (`/rest/api/1.0`).
- ⬜ Embedded terminal: xterm.js front end over Windows ConPTY.
- ⬜ Commit-signing UX: show signature status; guide GPG, SSH, and X.509 (smart card) signing setup.
- ⬜ Policy controls: allow-list of hosting domains, disable force-push, require signed commits (enforced client-side, configured centrally).

## Phase 4 — Hardening and V1.0 release (target: +4 weeks)

- ⬜ Cross-platform hosts (Photino.Blazor or .NET MAUI) and macOS Keychain backend.
- ⬜ Installers: MSIX and WiX MSI with Authenticode signing; offline-capable update channel.
- ⬜ Accessibility audit to Section 508 / WCAG 2.1 AA (keyboard paths, screen-reader names, contrast).
- ⬜ Supply chain: CycloneDX SBOM per build, dependency vulnerability scanning, reproducible builds.
- ⬜ Security review and penetration test of process execution, IPC, and token handling.
- ⬜ Telemetry-free diagnostics: local, user-exportable logs only.

### V1.0 exit criteria

| Metric | Budget |
|---|---|
| Open a 100k-commit repository to first rendered history rows | < 2 s on a standard engineering laptop |
| History scroll | 60 fps, no frame > 50 ms |
| Render a 10,000-line diff | < 250 ms to first paint |
| Status refresh after a file save (50k-file working tree) | < 500 ms |
| Memory with a 100k-commit history loaded | < 400 MB working set |
| Automated tests | All green; graph, patch, and rebase paths covered by integration tests |

These budgets are targets; Phase 4 adds a benchmark suite to measure them in CI before release.
