# Releasing and updates

GitForge ships as a self-contained Windows x64 app packaged with [Velopack](https://velopack.io) 1.2.161. Installed copies update themselves from GitHub releases, an https mirror, or a folder / UNC share.

## How updates work

- Installed copies check the feed about 15 seconds after launch, then at most once a day. A check that finds an update shows **Update to x.y.z** in the status bar; nothing installs until the user clicks **Update and restart**.
- **Check for updates…** (command palette) and **Settings › Updates › Check now…** check immediately.
- Updating downloads a delta package when one applies (otherwise the full package), checks its SHA-256 against the feed's release index, restarts GitForge, and reopens the user's tabs. A package that fails the check isn't installed, and the running version keeps running.
- A check reads the feed's release index. From an https feed, the request's query string carries only the platform, package ID, and installed version (`arch=x64&os=win&rid=win-x64&id=GitForge&localVersion=0.1.0`). From GitHub, it lists the repository's releases through the GitHub API and downloads the release index. A folder feed is read from disk. No user, machine, or repository data is sent; as with any web request, the server sees the client's IP address.
- Copies run from source (`dotnet run`) or the dev host don't update; **Check for updates…** says so.
- Unauthenticated GitHub API requests are limited to 60 per hour per IP address. Large sites behind one NAT should use a mirror (see below).

## Publishing a release

1. Confirm CI is green on `main`.
2. Tag and push:

   ```bash
   git tag v0.2.0
   ```

   ```bash
   git push origin v0.2.0
   ```

3. [.github/workflows/release.yml](../.github/workflows/release.yml) then:
   - runs the tests;
   - publishes a self-contained, ReadyToRun build stamped with the tag's version;
   - packs it with vpk, including a delta from the previous release and the release notes (commit subjects since the last tag);
   - uploads the release, `SHA256SUMS.txt`, and a signed build provenance attestation.

Tags must be `vMAJOR.MINOR.PATCH`. A suffix (`v0.2.0-beta.1`) publishes a GitHub pre-release, which installed copies ignore.

| Asset | Purpose |
|---|---|
| `GitForge-win-Setup.exe` | Per-user install, no administrator rights; adds a Start menu shortcut |
| `GitForge-win.msi` | Windows Installer package for deployment tools (Intune, Configuration Manager) |
| `GitForge-win-Portable.zip` | Runs without installing |
| `GitForge-x.y.z-full.nupkg`, `GitForge-x.y.z-delta.nupkg`, `releases.win.json` | Update packages and the index the updater reads |
| `SHA256SUMS.txt` | SHA-256 of each asset above |

To confirm a download was built by this repository's workflow, use the GitHub CLI:

```bash
gh attestation verify GitForge-win-Setup.exe --repo finepointcgi/GitForge
```

## Code signing

Unsigned releases trigger SmartScreen warnings, and application control (WDAC, AppLocker) may block them. The release workflow signs every unsigned binary, the installer, and the MSI (SHA-256 with an RFC 3161 timestamp) when these repository secrets exist:

| Secret | Value |
|---|---|
| `WINDOWS_SIGNING_PFX_BASE64` | Base64 of a code-signing certificate (.pfx) |
| `WINDOWS_SIGNING_PASSWORD` | The .pfx password |

A .pfx suits a certificate from an internal enterprise CA. Publicly trusted code-signing certificates issued since June 2023 must keep their keys in hardware (CA/Browser Forum), so they can't be exported as a .pfx. For those, change the Pack step to use `--azureTrustedSignFile` (Azure Trusted Signing) or `--signTemplate` with your HSM's signing command.

## Managing updates in an organization

Policy values under `HKLM\SOFTWARE\Policies\GitForge` (or `HKCU\...`) override user settings. In Settings, the controls they govern are disabled and labeled as managed.

| Value | Type | Effect |
|---|---|---|
| `DisableUpdates` | REG_DWORD `1` | No update checks. Set in either hive. |
| `UpdateFeed` | REG_SZ | Feed to use. If both hives set one, the machine's wins. |

```cmd
reg add HKLM\SOFTWARE\Policies\GitForge /v UpdateFeed /t REG_SZ /d \\fileserver\software\gitforge /f
```

Feed formats (also accepted in **Settings › Updates › Update feed**):

- `https://github.com/owner/repo`: that repository's releases (the default is this repository).
- `https://updates.example.com/gitforge`: a static site holding `releases.win.json` and the `.nupkg` files.
- `\\server\share\gitforge` or `D:\mirror\gitforge`: the same files on a share or disk, for disconnected networks.

Plain `http://` is rejected except for `localhost`, because the release index sets the checksums.

**To run a mirror,** copy each release's `releases.win.json`, `GitForge-x.y.z-full.nupkg`, and `GitForge-x.y.z-delta.nupkg` into the folder or site, replacing the old `releases.win.json`. Verify the files against `SHA256SUMS.txt` before copying them inside the boundary. To build a copy that uses the mirror by default, publish with `-p:GitForgeUpdateFeed=<feed>`.

## Testing an update locally

1. Publish and pack two versions into one folder with a test package ID, so the test install can't collide with a real one:

   ```bash
   dotnet publish src/GitForge.Desktop -c Release -r win-x64 --self-contained -p:Version=0.9.0 -o out/0.9.0
   ```

   ```bash
   dotnet vpk pack -u GitForgeTest -v 0.9.0 -p out/0.9.0 -e GitForge.exe -r win-x64 -o feed
   ```

   Repeat both commands with 0.9.1. Pack 0.9.0 a second time into another folder to keep its `Setup.exe`, because each pack overwrites `feed/GitForgeTest-win-Setup.exe`.

2. Serve the feed folder on localhost (for example `python -m http.server 47613 --bind 127.0.0.1 --directory feed`).
3. Set `GITFORGE_SETTINGS` to a test settings file containing `{ "updateFeed": "http://127.0.0.1:47613" }`, install 0.9.0 with its `Setup.exe`, and launch it from the Start menu shortcut.
4. Update from the status bar. Uninstall with `%LOCALAPPDATA%\GitForgeTest\Update.exe uninstall`.
