# KeaClip quick guide

KeaClip is the provisional display name for the Klippi project. Use the current private Windows build, and check [KNOWN_LIMITATIONS](KNOWN_LIMITATIONS.md) before relying on a workflow. Download only media you own or have permission to save.

## Save something

1. Copy a link and open KeaClip. If clipboard detection is on, the link box fills in for you to review; nothing ever downloads automatically.
2. Choose **Analyse** to see the title, creator, thumbnail, available qualities and a size range, or go straight to **Download**.
3. Pick what to keep: **Video + audio**, **Audio only**, **Video only** or **Archive**. Quality and format sit beside each other; **More options** holds codec, frame rate, subtitles, embedding, browser sign-in and naming.
   - *Original audio* is recommended for fidelity: converting a lower-quality source to 320 kbps MP3 only makes a bigger file. Opus goes up to 256 kbps.
   - *Convert HDR to SDR* only changes HDR sources; SDR video is kept exactly as downloaded.
   - *Archive* keeps the best original streams untouched, with the description, thumbnail, subtitles, metadata and a `SHA256SUMS.txt` you can check with `sha256sum -c`. An optional ZIP copy is verified too.
4. **Download** adds it to the Queue; **Download & edit** opens it in the editor when it finishes. Turn on **Share when finished** to share each of these downloads once it finishes (see Share); its starting position is a Settings choice, off by default.
5. Playlists list their entries with none selected. Select entries, a range, or all. An entry that is itself a list (a channel tab, a multi-video post) shows **Open as playlist**.
6. If you already have the same media in the same mode and format, KeaClip asks first: **Open existing**, **Download again** (saved as "Name (2)"), **Replace** (not for archives) or **Skip**.

## Queue and library

The Queue shows real stages (analyse, download, process, done), speed and time left. Pause holds new starts; downloads already running continue until they finish or you cancel them. Failed items keep their reason and can be retried. Removing an item, or a Library record, never deletes the saved file.

The queue survives a restart. Waiting downloads, ones that were still running (they start again from the beginning) and failed ones (with their reason and Retry) come back in the same order the next time KeaClip opens. If you had paused new starts, they stay paused. A download whose folder no longer exists is left out, and the status says so. Links that need private sign-in or session values to work are never saved, so those downloads are left out too; add them again. Finished downloads are in the Library instead.

The Library lists everything you saved or exported, newest first, with search. Each row can open the file, show it in its folder, edit, share, copy its path or link, or download it again.

## Edit a clip

Open a file from the Library or Queue, with **Open local media**, or by dropping it on the window.

- Play with Space, seek by clicking or dragging on the waveform, and set the range with the orange handles, the I and O keys, or the Start/End timecodes (`05:48` or `00:05:48.250`).
- **Lossless** cuts are fast and may start at a keyframe; **Exact** re-encodes and is frame-accurate.
- Video or Audio clip outputs, compression presets or a size target, encoder choice, height and frame rate, fades, volume, normalise, trim silence at start and end, mono and sample rate.
- **Make a Discord soundboard sound** sets a short MP3 within the soundboard limits from Settings.
- Your original is kept by default. *Delete the original afterwards* only happens after the new file exports and passes verification.

| Shortcut | Action |
| --- | --- |
| Space | Play or pause |
| I / O | Mark the start / end at the playhead |
| Left / Right | Step 0.1 s (Shift: 1 s) |
| + / - | Zoom the timeline |
| F11, Esc | Fullscreen preview, leave fullscreen |
| Ctrl+1 to Ctrl+5 | Download, Queue, Library, Editor, Settings |
| Ctrl+O | Open local media |

## Share

**Share** encrypts the file on this PC, then uploads it to File.Kiwi. Keep the whole link: the part after `#` is the decryption key and is never saved to history. Links expire: free links from KeaClip last up to 24 hours, less for very large files, and KeaClip labels expiry as an estimate. A very large upload asks for confirmation first, because an interrupted upload cannot resume yet. Cancel an upload from its own Cancel in the Share card; the header's Cancel leaves a running upload alone unless it is the only task.

**Share when finished** (Download page) shares each download queued while it is on, after it finishes: the same encrypted upload, one at a time (a share waits for any upload already running). The full link is copied to the clipboard and the status says **Shared: link copied**; **Copy link** on the finished Queue item and on its Library row copies it again until KeaClip closes. The link is kept in memory only. A very large file waits for your confirmation (choose **Share** on it). If sharing fails, the download is still saved and the Queue item says why, with **Retry share**. The switch never downloads anything by itself.

## Settings and components

Choose folders (with optional separate Audio, Clips and Archives folders), theme, reduced motion, how many downloads run at once, clipboard detection, close and tray behaviour, startup, completion notices, whether Share when finished starts switched on, history retention and soundboard limits. Changes that need it are saved with the bar at the bottom; a dot on Settings shows unsaved changes.

Components shows the app-managed yt-dlp, FFmpeg, FFprobe and deno versions, with verified install, update and restore. No PATH or PowerShell setup is needed. **Clean temporary files** removes only the app's own finished work folders and never touches your media or history.

## Updates

Once a day KeaClip asks its release page whether a newer version exists (Settings, About: **Check for updates automatically**, on by default; **Check for updates** asks at once). Offline or on a metered connection the daily check simply waits. When a newer version exists, a small **Update to …** button appears in the sidebar and Settings, About shows its notes with **Update now** and **Later**. Nothing is downloaded or installed until you choose Update now, and an older or the same version is never installed.

Update now downloads the update and checks it against its published SHA-256 before anything runs. An installed copy then closes, installs the update (your settings, history and media are kept) and opens again by itself. If downloads, an export, an upload or unsaved settings would be stopped, KeaClip says so first: choose **Restart anyway** or **Not yet** (then **Restart and update** when you're ready). A portable copy downloads the verified ZIP and shows it in File Explorer; then close KeaClip, unzip it to a new folder and move the `Data` folder from the old KeaClip folder into the new one.

## Portable and installed

Portable mode keeps settings in a `Data` folder beside the app and needs no admin rights. Installed mode keeps personal data under your local application data. Startup is off until you turn it on. Browser extensions and the link protocol only deliver a checked link; they never run commands or start downloads by themselves.
