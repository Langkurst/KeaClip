# Known limitations and verification boundaries

Updated 2026-10-15 for 0.4. What has and has not been checked on real hardware, so nobody mistakes a passing test for a guarantee.

## Product scope and identity

- The name KeaClip has had an exact software-name search but no formal trademark clearance.
- KeaClip is licensed GPL-3.0-or-later. Packages include full third-party notices and a written offer for the source of bundled GPL/LGPL components; this is the project's reading of those licences, not legal advice.
- Made for Windows 11 and tested on one Windows 11 PC (plus automated interface tests). Windows 10 (64-bit) should work but has not been tested yet.

## Downloads and sites

- Extractors depend on websites and authentication; there is no permanent or universal supported-site guarantee. Firefox/Chromium cookie access can fail because of profile permissions, encryption or locks. No real personal browser-session test has been performed.
- Live checks used Creative Commons films on YouTube (video, MP3, archive), the Internet Archive and Wikimedia Commons (MP3). Vimeo asked for sign-in; Twitch, X/Instagram multi-video posts and other sites are covered by fakes and argument tests only.
- Playlist analysis is bounded to 500 entries; the status says when a playlist is longer. Selected entries are separate jobs; pause stops new starts, not a transfer in progress. Unfinished queue jobs are saved and restored after a restart; a job that was running starts again from the beginning (no partial resume), and a job whose link needs private sign-in or session values is not saved and must be added again. Share links made with Share when finished are kept in memory only, so Copy link is available until KeaClip closes.
- Cover art is embedded only when chosen. WebM video and WAV audio cannot carry a cover; the option explains this.

## Editor, media engine and compression

- Lossless cuts can start at keyframes (the editor says so). Exact export re-encodes. MPEG-TS/PS and unknown containers decode from up to 15 s before the cut for accuracy, which costs a little time; a source with keyframes more than 15 s apart could still start late.
- MPEG-TS cuts keep the source's sub-frame offset between audio and video (sync within one frame). A source whose video starts after its audio keeps that late first frame, which keeps sync but shows a short gap.
- HDR detection reads the stream-level colour transfer (PQ or HLG). A file that marks HDR only in per-frame side data is treated as SDR (safe, but not converted). An SDR re-encode does not re-tag BT.709 (players assume it for HD).
- Hardware encoding was verified on AMD (AMF: H.264, HEVC, AV1) only. NVENC and QSV arguments are untested; they are probed with a one-frame test encode and skipped when they fail. libsvtav1 is not in the FFmpeg build KeaClip downloads. h264_amf cannot encode very small frames; such exports fall back to the CPU encoder with a note.
- Compression Off is quality-based, so a re-encode can be larger than the source. Size checks apply to presets and size targets only. Estimates are labelled as estimates; lossless clip estimates use the whole-file average bitrate.
- The export watchdog allows time for the final MP4 rewrite based on 20 MB/s; that rate is reasoned, not measured on a slow hard disk with a multi-gigabyte file.
- Waveforms are visual overviews, not sample-accurate measurement. There is no multitrack composition.

## Sharing

- File.Kiwi direct v2 uploads were verified with a small synthetic file and an independent recipient round trip. Large live multi-chunk transfers and live interruption were tested with fakes only; persistent resume of an interrupted upload is not implemented. Uploads of roughly 100 GB or more, or ones estimated to need over 30 hours, ask for confirmation first.
- The public v2 API does not document paid retention, account controls or absolute deletion timestamps, so the app offers free sharing only and labels expiry estimates. Never treat a free link as permanent.
- The header's Cancel stops the visible work and leaves a running upload alone unless it is the only task; an upload is otherwise stopped from its own Cancel in the Share card.

## Windows integration and accessibility

- Natively verified on Windows 11 at one monitor and scale: the custom title bar (drag, maximise, Snap Layouts, top resize), VLC playback of AV1 1080p, paused seeking, keyboard marks, theme switching and history after restart. Other DPI scales, multiple monitors and tray behaviour were checked headlessly or not at all.
- Screen-reader behaviour is checked through Avalonia automation peers in headless tests; Narrator and other screen readers were not used on a real desktop.
- Completion notices are in-app; a Windows-native toast is not delivered.
- Synthetic input only: a click that teleports onto a control that has just appeared (the first dropdown after a page switch) without any prior pointer movement is dropped. Normal mouse use, which always moves first, is unaffected; touch taps were not tested.
- Downloads and exports run at below-normal priority on purpose, so a game or other foreground work keeps the CPU; on a fully loaded PC they slow down rather than compete. Short interactive tasks run at normal priority.

## App updates

- Updates come from the newest release of `Langkurst/KeaClip` on GitHub, read at most once a day. Installers are not code-signed yet, so an update is trusted because it comes from GitHub over HTTPS and matches the SHA-256 listed in the release. See [RELEASING](RELEASING.md).
- The update flow is tested with a local HTTP server, fakes and the installer self-test, and real installed copies have updated themselves from a local feed. An update from a published GitHub release has not run yet.
- Installers are not code-signed yet; updates are verified by their required SHA-256 (and a present but broken signature is refused). Publisher pinning waits for signing.
- Portable copies are not replaced automatically: the verified ZIP is downloaded and the folder swap (move `Data`) is one manual step.
- Metered-connection detection uses the Windows Network List Manager; when Windows cannot say, the daily check runs as on an unmetered connection.
- Each update keeps the previous version in a hidden folder next to KeaClip, so a failed update can roll back. Older copies are trimmed after each update and removed on uninstall; a file you changed by hand is kept for you to review.

## Data safety

- Explicit temporary-file cleanup removes only registered inactive workspaces with matching receipts, directory identity, declared generated filenames and no active writer job. Unknown or reparse contents, recovery copies and interrupted promotions are kept; older unmarked leftovers are not swept.
- Cancelling a queue job while its history record is being saved also cancels that save, so the file exists without a Library record (deliberate: Cancel can stop a hung history write).

