# Changelog

## 0.5.0 (2026-10-24)

- **Vertical clips for TikTok, Shorts and Reels**: under Shape, pick Vertical 9:16 (or Square 1:1), see exactly what stays in frame on a still from your clip, and slide it left or right.
- **Links that point to a moment**: paste a link with a time in it (like ?t=1m30s) and press Analyse; Just part of it is set around that moment, ready to save.
- **Quick picks for game replays**: Last 15 s, Last 30 s or Last 60 s keeps just the ending, where the good bit usually is.
- **Make it louder**: one switch brings quiet game or phone audio up to a clear, even level.

## 0.4.6 (2026-10-24)

- **What's new after an update**: when KeaClip updates itself, a card says what this version added, with a link to the full release notes. It stays until you close it.
- **Ready to share, from the start**: the welcome line now mentions dropping any files on KeaClip to share them.

## 0.4.5 (2026-10-22)

- **You'll notice when it's done**: if KeaClip is behind a game or minimised when a download or upload finishes, its taskbar button flashes and the notice stays until you've seen it. Hidden in the tray? Hover the tray icon to see what finished. Uploads now get a "Link ready" notice too. (Settings, Completion notices turns all of this off.)
- **Fixed**: the "Download finished" notice never actually appeared. It's now a card in the bottom-right corner of the window, with a close button.

## 0.4.4 (2026-10-22)

- **Drag anything over KeaClip** and the whole window says what letting go will do: "Drop to edit clip.mp4", "Drop to share these 3 items with one link" or "Drop to paste this link", on any page.
- **Ctrl+V pastes files**: copy files or a folder in File Explorer, press Ctrl+V in KeaClip, and it's the same as dropping them.
- **Share copies the link for you**: when an upload finishes, the link is already on the clipboard, ready to paste.

## 0.4.3 (2026-10-22)

- **Share any files or folders**: drag them onto KeaClip and a Ready to share card appears; press Share for one encrypted file.kiwi link. Several files or a folder are packed into one .zip for you (videos and photos aren't squashed), and the .zip is deleted once the link is ready. Nothing uploads until you press Share. A single video or audio file still opens in the Editor, now including WMV, FLV, MPG, TS, 3GP and similar.
- **Fixed**: clicking the "Your clip is ready" card could reopen the clip in the Editor, as if it had been dropped back onto KeaClip.

## 0.4.2 (2026-10-16)

- **Copy clip**: when a clip is ready, one button copies the file itself, so Ctrl+V in Discord, a chat or an email attaches it. You can also drag the finished clip straight out of KeaClip. In the Library, More actions has **Copy file** too.
- **Links from a second launch arrive on busy PCs**: opening several links at once while KeaClip starts no longer loses one.

## 0.4.1 (2026-10-15)

- **Feedback email**: choosing Email also copies keaclip@proton.me, since many PCs have no email app; there's a Copy the email address item too.
- **Safer first setup**: FFmpeg now comes from its maker's official GitHub mirror, checked against the checksum GitHub itself recorded (like yt-dlp and Deno), instead of a checksum stored next to the file on the same website.
- **History stays quick**: if history is kept forever, KeaClip keeps the newest 5,000 records so saving stays fast. Your videos are never touched.

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