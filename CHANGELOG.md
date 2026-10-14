# Changelog

## 0.3.2 - beta copies (2026-10-14)

- Beta copies (`build/package.ps1 -Beta`) open a short **Beta test** checklist on first start: six things to try, Works or Problem for each, a notes box and **Save report**, which writes a plain-text report (answers, Windows version, screen scaling, last error, personal paths removed) to the Desktop to email to keaclip@proton.me. Normal builds show none of it.
- Updates and the download link now point at the public repository `Langkurst/KeaClip`.
- Share says how long links last: free to download for 24 hours (12 hours for 10 to 20 GB, 6 hours for 20 GB or more), with a **How long links last** link to file.kiwi's table, and after an upload the time file.kiwi gave that file. The README credits the file.kiwi API. The large-upload warning now says correctly that an upload has 36 hours to finish.
- The source zip in public packages leaves out private working notes.
- The installer accepts `--silent` (for winget: per-user install or upgrade, no window) and is about 60 MB smaller (compressed). `build/winget-manifest.ps1` writes the winget manifests for a release.

## 0.3.1 - tidier export, recordings switch (2026-10-14)

- The Messaging target dropdown is gone: the Fit for buttons cover it, and a custom size is Compression, Target size.
- Settings, Privacy has **Show my recordings in the Editor** (on by default); off, the Videos folder is not read at all.
- Debug symbols (about 100 MB) are no longer shipped; the public download is an 87 MB zip.

## 0.3.0 - clips, parts and GIFs (2026-10-14)

- **Fit for**: one click aims a clip just under Discord (10 MB), Email (25 MB), Nitro Basic (50 MB) or Nitro (500 MB).
- **Your recordings**: the Editor lists new game clips and captures from Videos (Xbox Game Bar, AMD ReLive, NVIDIA, OBS).
- **Just part of it**: save only a time range of a link; only that part is downloaded and the file is named for it.
- **Export as GIF**: a silent, looping GIF of the selection (up to 60 s, 480 px wide).
- **Smaller download**: unused VLC plugins (network, streaming-out, encoders, subtitle rendering and more) are left out, about 106 MB down to 47 MB; 11 common formats are checked to still play.
- **Smarter copied links**: only links from video and audio sites or direct media files are filled in automatically.
- **Quiet yt-dlp updates**: checked a minute after start and daily, only while nothing is downloading.

## 0.2.4 - polish (2026-10-14)

- The app icon sits on the new warm tile, the first-run notice names FFmpeg and FFprobe properly, and the version label no longer says "local preview".
- File.Kiwi uploads identify themselves as KeaClip.

## 0.2.3 - the kea redesign (2026-10-14)

- New look: kea olive accents on warm alpine-night or limestone-day backgrounds, the kea's orange reserved for the one main action on each page, Bricolage Grotesque headings and crisper shapes.
- Calmer pages: no explainer line under each title, sections rise in one after another, and the main button lifts slightly under the pointer (all off with Reduce motion).
- Simpler Settings: Appearance, Downloads and About first; everything else under Advanced settings, which opens by itself when one of its settings needs fixing.
- Plainer wording across the app, NZ spelling, and a Support KeaClip button in About that opens the Ko-fi page.
- Public builds leave out watch links and do not bundle FFmpeg, yt-dlp or Deno (KeaClip fetches them from their publishers on first run).

## 0.2.2 - watch links go live, free-allowance limit (2026-10-14)

- Watch links are served by the player page's own Cloudflare Pages Function from a private R2 bucket: no domain, public bucket or CORS rule needed.
- KeaClip keeps watch-link storage under 8 GB (inside R2's free 10 GB): every upload is recorded in `watch-usage.json`, counted until a day after the bucket deletes it, and an upload that would pass the limit is refused before anything is sent.

## 0.2.1 - local updates (2026-10-14)

- Updates now work without public hosting: `build/release-local.ps1` builds, self-tests and publishes a new version to `%LOCALAPPDATA%\KeaClip-Releases`, and the installed app reads that feed at every start and offers **Update now**. The installer is still size-capped, SHA-256 checked and refused if it is not newer.
- A local feed must be a `keaclip-update.json` on a local drive and may only name files beside it; network shares, paths and links are refused.

## Unreleased - app updates

- KeaClip checks its release feed (`keaclip-update.json` on the newest GitHub release) at most once a day, quietly, and only while **Check for updates automatically** is on (the default). Settings, About also has **Check for updates**.
- A newer version shows its notes with **Update now** and **Later**; nothing downloads or installs without Update now, and the same or an older version is never installed.
- Updates download over HTTPS only into the app's own folder, are capped at the listed size and must match the listed SHA-256 exactly; a broken Authenticode signature is refused.
- Installed copies restart into the installer's new `--upgrade` mode, which waits for the app to close, keeps settings, media, shortcuts and the link handler, rolls back on failure and opens KeaClip again. Running work or unsaved settings are named first.
- Portable copies get the verified ZIP and one manual step (move the `Data` folder).
- New `build/release-manifest.ps1` writes the feed for a built installer and ZIP; release steps are in `docs/RELEASING.md`.

## 0.2.0 candidate - redesign and reliability (2026-10-12)

Look and feel
- New design system: dark and light themes from shared tokens, hand-drawn stroke icons, rounded focus rings, readable disabled buttons in both themes, and a vector kea mascot that carries a card while downloads run.
- Custom title bar that keeps native Windows behaviour (drag, double-click maximise, Snap Layouts, resize).
- Every page redesigned: one effortless Download flow, a live Queue with a stage stepper, a calm virtualised Library, a 60 FPS editor timeline, and grouped Settings with an unsaved-changes bar.
- Motion uses only opacity and transforms, respects reduced motion, and was measured at 121 FPS (1.42 ms average render) with 0.08 % idle CPU. Ctrl+Shift+F12 shows the frame-rate overlay.

Downloads, queue and library
- The queue survives a restart: waiting, interrupted and failed jobs come back in order with their options (paused if new starts were paused); jobs whose folder is gone are dropped with a note; links holding private values are never saved. The queue file is written atomically with a backup, and an unreadable one is set aside and reported once.
- Work runs in separate lanes, so an upload or export never blocks analysing or downloading.
- Queue updates are coalesced and touch only changed rows; finished jobs add one Library row.
- Duplicate checks match the mode, format and playlist item: an MP3 is never stopped by a saved video, an archive only by an existing archive. "Download again" names copies "<name> (2)".
- Playlist entries that are lists themselves (channel tabs, multi-video posts) offer Open as playlist; positions in a post are downloaded and recorded separately.
- Online thumbnails now display (they never did before), at up to 640 px.
- Archive checksums use the standard format, so `sha256sum -c SHA256SUMS.txt` verifies them.

Editor and media engine
- Exact clips seek on the input: a 5 s clip two minutes into a 1080p file now exports in about 3 s instead of about 25 s, still frame-accurate, with fades on the clip's own timeline.
- Late-starting audio, MPEG-TS sources and normalised audio stay in sync; Trim silence trims only the start and end.
- HDR to SDR runs only for HDR (PQ or HLG) sources and is offered only for them; hardware encoders are verified with a test encode before use and fall back to the CPU.
- Compression presets are bitrate-aware with honest estimates; audio is never encoded above the source bitrate; Opus offers up to 256 kbps.
- The export dialog suggests a free clip name; cancelling an export never stops a running upload.

Sharing
- Share when finished (Download page, default in Settings, off unless chosen): a finished download is shared through the same encrypted File.Kiwi upload, one upload at a time, its link copied and offered again by Copy link on the Queue and Library rows (kept in memory only). A failed share keeps the download and offers Retry share; very large files still ask first.
- File.Kiwi uploads survive stalls, sleep and dropped connections within a bounded outage budget, show the amount sent, and warn before very large uploads (links last 36 hours; no resume yet).

Reliability and safety
- Short tasks the user waits for (link analysis, probes, version checks) run at normal priority, so they no longer stall while a game runs; downloads and exports stay below normal.
- Stronger redaction of paths, tokens and share secrets; recovery from unreadable history; idempotent retry; safer component updates and rollback.
- About 1,050 automated tests, including an opt-in suite that runs the real FFmpeg (set KLIPPI_FFMPEG_DIR).

## Unreleased - repository setup (2026-10-07)

- Established an independent local Git repository on `main`.
- Preserved the supplied product brief and recorded provisional naming.
- Added agent instructions, the Windows-first roadmap, bounded-goal/checkpoint rules and observed-usage accounting.
- Completed the official documentation, service and naming research gate before application code.

## 0.1.0 - personal Windows preview (2026-10-07)

- Added shared C#/.NET services and the Avalonia dark/light UI with original kea icons.
- Added validated URL analysis, video/audio downloads, playlists and queue/progress/cancellation.
- Added local history/settings, verified managed components and rollback.
- Added LibVLC playback, waveform/timecode clips, audio processing, compression and integrity-gated chosen source deletion.
- Added preservation archives with readable metadata, technical information, SHA-256 and optional verified ZIP copies.
- Added explicit encrypted File.Kiwi upload; independently verified a real recipient download/decryption/checksum round trip.
- Added self-contained portable packaging, a real transactional per-user installer/uninstaller and Firefox/Chromium URL helpers.
- Fixed review findings affecting overwrite races, recovery backups, bounded logs/timeouts, late cancellation, optional history and startup/settings rollback.
- Added explicit owned-workspace cleanup, crash-bound media processes and Windows reduced-motion preference support.
- Locked source and publication dependency graphs; packaging refuses changed source and requires complete hidden installer evidence.
- Reproduced a native D3D11 module-unload crash; retained the system module for playback lifetime and passed the subsequent hidden format sequence. Visible native acceptance remains pending.
- Recorded tests, original failures/fixes, self-review, remaining native workflows and exact redistribution source/notice gaps. This version is not a friend/public release.
