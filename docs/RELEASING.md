# Releasing an update

How a new KeaClip version reaches installed and portable copies. Today these steps are manual;
each one is a plain command, so a GitHub Actions workflow can run the same commands later.

## How the app finds and installs an update

- **Feed.** The app reads `keaclip-update.json` from
  `https://github.com/Langkurst/KeaClip/releases/latest/download/keaclip-update.json`: the asset of
  that name on the newest published (not draft, not pre-release) GitHub release. Another https feed
  can be set as `UpdateFeedUrl` in `settings.json` (hand-edited; not shown in the app). Plain http,
  links with credentials and redirects to insecure addresses are refused.
- **When.** At most once a day in the background, 20 seconds after startup and then hourly while
  the app stays open, only while **Check for updates automatically** is on (the default) and the
  settings file really loaded. Offline or on a metered connection the daily check waits quietly.
  **Check for updates** in Settings, About always asks at once. The time of the last attempt is in
  `update-state.json` in the data folder, so a failing feed is still asked only once a day.
- **Nothing installs by itself.** A newer version shows its notes with **Update now** and
  **Later**. The same or an older version is never offered, downloaded or installed (the installer
  refuses it again).
- **Download and verification.** HTTPS only, into the app-owned `Updates\<version>` folder in the
  data folder, never more bytes than the feed lists, and the SHA-256 must match the feed exactly
  (the hash is required even though the feed itself comes over HTTPS). The file is checked again
  from disk and kept open (no one can change it) until the installer has started.
- **Authenticode.** If the installer carries a signature, Windows must verify it (WinVerifyTrust,
  offline, no revocation lookups) or the file is deleted and not run. Unsigned installers are
  accepted today because the SHA-256 is required. Once releases are signed with a trusted
  code-signing certificate, the next step is to pin the publisher and refuse unsigned installers
  (see "Signing" below).
- **Installed copies.** After the user's Update now, the app starts
  `KeaClip.Setup.exe --upgrade --path <installed folder> --wait-pid <app> --relaunch` and exits (if
  downloads, an export, an upload or unsaved settings would be lost, it asks first: Restart anyway
  or Not yet). The installer's upgrade mode waits for the app to close, refuses the same or an older
  version, runs the same verified transaction as a manual upgrade (staged, hash-checked, rolled
  back on failure; `Data`, settings and unrelated files kept; existing shortcuts and link handler
  kept), thins out older rollback backups (only exact duplicates are removed), and starts KeaClip
  again, also after a rolled-back failure. The new version says once that it was updated.
- **Portable copies.** Update now downloads and verifies the portable ZIP and shows it in File
  Explorer. The one manual step: close KeaClip, unzip it to a new folder, and move the `Data`
  folder from the old folder into the new one. A self-replacing portable folder was not built: the
  app can be on removable or synced drives where a half-finished in-place swap is the riskiest case,
  and the user keeps the old folder until they are happy.
- Downloads of older or the current version, and leftover partial downloads, are removed from
  `Updates` at the next start. Nothing else in the data folder is touched.

## Updates on this PC (local feed, the current route)

Until releases are public on GitHub, updates for a copy installed on the development PC go through
a local feed:

1. Raise `<Version>` in `Directory.Build.props` and `AppIdentity.Version` in
   `src/Klippi.Core/Models.cs`, add the CHANGELOG entry, run the test suites and commit.
2. `pwsh -File build/release-local.ps1 -Notes "What changed"`. It packages, builds and self-tests
   the installer, copies it to `%LOCALAPPDATA%\KeaClip-Releases\KeaClip-<version>-Setup.exe` and
   writes `keaclip-update.json` beside it (asset entries are plain file names, never links).
3. Start KeaClip: it reads that feed at every start when the file exists (no network, no daily wait)
   and offers **Update now**. The copy is size-capped and SHA-256 checked like a download, and the
   installer's upgrade mode refuses the same or an older version.

The local feed is only used when `%LOCALAPPDATA%\KeaClip-Releases\keaclip-update.json` exists and no
`UpdateFeedUrl` is set in settings.json; a local feed must be named `keaclip-update.json` on a local
drive (network shares are refused). Delete that folder to fall back to the GitHub feed.

## Release steps (manual until GitHub Actions is available)

1. Raise `<Version>` in `Directory.Build.props` and `AppIdentity.Version` in
   `src/Klippi.Core/Models.cs` (a test checks they match), add the CHANGELOG entry and commit.
2. Build and verify as for any release: `dotnet build Klippi.sln -c Release --no-restore`, both
   test suites, then `pwsh -File build/package.ps1 -IncludeComponents` and
   `pwsh -File build/installer-publish.ps1 -Payload <portable zip> -SelfTest` (the installer
   self-test includes the upgrade-mode checks and a real `--test-upgrade` process run).
3. Write the feed:
   `pwsh -File build/release-manifest.ps1 -Installer artifacts\installer\KeaClip.Setup.exe -Portable <portable zip> -NotesFile <notes.md>`
   It checks that both files are the version in `Directory.Build.props`, copies them to
   `artifacts\release\<version>\` as `KeaClip-<version>-Setup.exe` and
   `KeaClip-<version>-win-x64.zip`, and writes `keaclip-update.json` and `SHA256SUMS.txt` with the
   exact hashes and sizes. The notes are plain text (up to 8,000 characters).
4. Create the GitHub release `v<version>` (a normal release, not a draft or pre-release) and upload
   the four files from `artifacts\release\<version>\` unchanged. Uploading them under other names
   breaks the links in the feed.
5. Check it: in an installed copy of the previous version choose Settings, About, Check for
   updates; the new version and its notes should appear. Optionally download the feed and compare
   `SHA256SUMS.txt` with `Get-FileHash`.

To withdraw a bad release, publish a fixed higher version (the app never goes back to an older one),
or delete the release so `latest` points at the previous one again.

## The public repository

The public source is `Langkurst/KeaClip`, which `UpdateServiceOptions.DefaultFeed` reads releases
from. `build/export-public.ps1` copies the committed tree into it as one new commit per release,
leaving out the paths in `build/public-exclude.txt` (private working notes), and refuses to commit
if it finds personal details. Never put a token into the app or the feed. GitHub serves release
assets of a private repository only to signed-in users with access, so until it is public the
default feed answers 404 and the app says no update information was published yet.

- **GitHub Actions**: a workflow on a pushed `v*` tag can run steps 2 to 4 on a
  `windows-latest` runner (`dotnet build`, the two test suites, `package.ps1`,
  `installer-publish.ps1 -SelfTest`, `release-manifest.ps1`, then `gh release create v<version>`
  with the four files). Keep `-SelfTest` in the workflow; a release without it is not verified.

## winget

After the release is public, `pwsh -File build/winget-manifest.ps1 -Installer <KeaClip-<version>-Setup.exe>`
writes the three `Langkurst.KeaClip` manifests under `artifacts/winget/manifests/l/Langkurst/KeaClip/<version>/`.
Check them with `winget validate --manifest <folder>`, then submit that folder to
`microsoft/winget-pkgs` as a pull request. winget runs `KeaClip.Setup.exe --silent`: a per-user install or
upgrade into `%LOCALAPPDATA%\Programs\KeaClip` with no window, which keeps an existing installation's choices.

## Signing

Signing is optional today. To add it: sign `KeaClip.Setup.exe` (and `KeaClip.exe` inside the
payload) with a trusted code-signing certificate (SignPath Foundation is free for open-source projects;
Azure Trusted Signing is paid) before step 3,
so the hashes in the feed are of the signed files. The app already refuses a signature that fails
verification. Then make signing required: pin the expected publisher in `UpdateService` and treat
`SignatureState.Unsigned` as a refusal. A self-signed certificate is not trusted by Windows and
would make every update fail verification.

## Testing without GitHub

`tests/Klippi.Tests/UpdateTests.cs` runs the real update service against a small HTTP server on
127.0.0.1 (plain http is allowed only there and only by an option the app never sets): feed
validation, the daily limit, offline and metered behaviour, hash and size refusal, downgrade
refusal, re-verification before the installer starts, and cleanup. For a manual end-to-end run,
publish a test feed on any https host and set `UpdateFeedUrl` in `settings.json` to it.
