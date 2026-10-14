# Developing KeaClip

Notes for working on the source. The user-facing overview is the [README](../README.md).

KeaClip is a Windows app to save, trim and share video and audio. It drives yt-dlp and FFmpeg, previews with LibVLC and shares through File.Kiwi with encryption on the user's PC.

## Build and test

Install the .NET 10 SDK (see `global.json`), then run `./build/verify.ps1`. It restores locked package versions, builds Release and runs the core and headless UI test suites.

- `./build/package.ps1` builds a self-contained Windows x64 portable folder and zip under `artifacts/packages/`. Public builds bundle no FFmpeg, yt-dlp or Deno: KeaClip fetches them from their publishers on first run.
- `./build/installer-publish.ps1 -Payload <portable.zip> -SelfTest` builds and tests the per-user installer.
- `./build/package.ps1 -Beta` builds a tester copy with the in-app Beta test checklist.
- Set `KLIPPI_FFMPEG_DIR` to also run the tests that call a real FFmpeg.

## Layout

- `src/Klippi.Core`: models, validation and contracts, no UI.
- `src/Klippi.Infrastructure`: downloads, media processing, sharing, updates and storage.
- `src/Klippi.Desktop`: the Avalonia app (views, view model, theme in `Theme/`).
- `src/Klippi.Installer`: the per-user installer and upgrader.
- `web/watch`: the optional watch-link player page ([WATCH_LINKS](WATCH_LINKS.md)).
- `extensions`: browser helpers that hand a page's link to KeaClip ([README](../extensions/README.md)).

Pinned versions: .NET 10 (packaged runtime 10.0.12), Avalonia 12.1.3, LibVLCSharp 3.10.1 and native VLC 3.0.24.

## More

- [User guide](USER_GUIDE.md), [known limitations](KNOWN_LIMITATIONS.md) and [design system](DESIGN_SYSTEM.md).
- [Releasing](RELEASING.md), [source and written offer](SOURCE.md), [third-party notices](THIRD_PARTY_NOTICES.md) and [collected licence texts](vendor-notices/README.md).

KeaClip is licensed under **GPL-3.0-or-later** (see [LICENSE](../LICENSE)).
