# Changelog

## 0.4.0 - first public release (2026-10-15)

- **Smoother Editor**: the clock reads like a player's (0:04 / 0:10, no milliseconds) and moves every screen refresh while playing (144 times a second on a 144 Hz screen); Start, End and Length show tenths while keeping the exact time behind them.
- **Bigger video**: with a file open the Editor uses up to 1440px of width and the preview grows with the window (up to 900px tall); the file name and facts share one slim line.
- **Native look**: Windows 11's own Segoe UI Variable for body text with the Bricolage headings, and calm sentence-case section labels.
- **First run**: KeaClip fetches FFmpeg and yt-dlp by itself on its first start, with progress on the setup card.
- **Clean uninstall**: removes the tools KeaClip downloaded; a tick box also removes settings and history. Videos are never touched. The setup window now matches KeaClip and offers Open KeaClip when done.
- **Lighter**: about 6 MB smaller (no debugger files) and less idle memory.

## Before 0.4: private previews (October 2026)

KeaClip was built and tested privately before its first public release. Along the way it gained:

- Downloads from links, playlists and a queue that survives restarts; an Archive mode that keeps the original with its details.
- The Editor: preview, waveform timeline, exact and quick cuts, Fit for Discord and email, audio clips, GIFs and Your recordings.
- Encrypted sharing through file.kiwi, Share when finished, and safe in-app updates checked against their published SHA-256.
- The kea design in dark and light, plain wording, and a per-user installer that keeps your settings on update.