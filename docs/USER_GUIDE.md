# KeaClip guide

KeaClip saves videos from links, cuts them down and helps you send them. This guide covers KeaClip 0.4. Only save or share what you're allowed to.

## First start

The first time KeaClip opens, it downloads a few free helper tools (yt-dlp, FFmpeg and Deno) from their official sites. This happens once and takes a minute or two. If you're offline, press **Download the tools** later.

If Windows says "Windows protected your PC" when you install, click **More info**, then **Run anyway**. That message shows for new apps that aren't code-signed yet.

## Save a video

1. Copy a link and paste it on the **Download** page. (If you copied a video link before opening KeaClip, it's filled in for you. Nothing downloads until you press a button.)
2. Press **Download**. Or press **Analyse** first to see the title, picture and quality.
3. Pick what you want:
   - **Video + audio**, **Audio only** or **Video only**.
   - **Archive** keeps the original with its description, picture and subtitles in one folder, plus a checksum list so you can confirm nothing changed.
   - **Just part of it** saves only a time range, like 0:45 to 1:20.
4. **Download & edit** opens the video in the Editor when it's done.

For a playlist, tick the videos you want. Nothing downloads until you add them.

Videos you can only see when logged in: open **More options**, then **Use my browser sign-in**. Firefox works best. KeaClip reads the sign-in only while downloading and never keeps it.

## Queue and Library

The **Queue** shows what's downloading, how fast, and how long is left. **Pause** stops new downloads from starting. Anything that failed says why and can be tried again. If you close KeaClip, the queue comes back next time.

The **Library** lists everything you saved, newest first, with search. From a row you can play it, show it in its folder, edit it, share it or download it again. Removing something from the Queue or Library never deletes the file.

## Edit a clip

Open a video from the Library, with **Open local media**, by dropping it on the window, or from **Your recordings** (new game clips from Xbox Game Bar, NVIDIA, AMD and OBS).

- Press Space to play. Drag the orange ends on the timeline to keep the part you want, or press **I** and **O** to mark the start and end where the playhead is. You can also type times like `1:20.5`.
- **Fit for** makes the clip small enough for Discord (10 MB), email (25 MB), Nitro Basic (50 MB) or Nitro (500 MB) in one click.
- **Make it smaller** offers lighter or stronger size savings.
- **Audio clip** saves just the sound. **Make a Discord soundboard sound** sets everything up for the soundboard.
- **Make a GIF of this part** makes a short, silent, looping GIF.
- When the clip is ready, **Copy clip** puts the file itself on the clipboard: press Ctrl+V in Discord, a chat or an email to attach it. You can also drag the "Your clip is ready" card straight into another app. In the Library, **More actions, Copy file** does the same for anything you saved.
- **Advanced** has the cut type, video settings and **Delete the original afterwards**. Your original is always kept unless you turn that on, and even then only after the new file checks out.

| Key | What it does |
| --- | --- |
| Space | Play or pause |
| I / O | Mark start / end |
| Left / Right | Step 0.1 s (with Shift: 1 s) |
| + / - | Zoom the timeline |
| F11, Esc | Fullscreen, leave fullscreen |
| Ctrl+1 to Ctrl+5 | Download, Queue, Library, Editor, Settings |
| Ctrl+O | Open a video from your PC |

## Share

**Share** locks (encrypts) the file on your PC, uploads it to [file.kiwi](https://file.kiwi/api) and copies a link. Anyone with the whole link can download it. Links are free to download for 24 hours (12 hours for files of 10 to 20 GB, 6 hours for 20 GB or more), then stop working.

**Share when finished** on the Download page does the same for each download once it's done.

**Share any files or a whole folder:** drag them onto KeaClip. A **Ready to share** card shows what's in it and how big it is, and nothing uploads until you press **Share**. One file goes as it is; several files or a folder are packed into one .zip first (you don't need to zip anything yourself), and the .zip is deleted from your PC once the link is ready. Your original files are never changed. Dropping a single video or audio file still opens it in the Editor.

## Settings

**Appearance** (dark or light), **Downloads** (where files go, how many at once) and **About** are at the top. Everything else is under **Advanced settings**: startup, tray, history, editor presets, privacy, the helper tools and storage clean-up.

## Updates

Once a day KeaClip checks for a new version (you can turn this off in Settings, About). When there is one, an **Update** button appears in the sidebar. **Update now** downloads it, checks it, installs it and reopens KeaClip. Your settings, history and videos are kept.

## Feedback

**Feedback** at the bottom of the sidebar opens a ready-made problem report or idea on GitHub (free account), or an email to keaclip@proton.me. KeaClip adds its version and the last error, with your folder names left out, and you send it yourself.

## Uninstall

Windows **Settings > Apps > Installed apps > KeaClip > Uninstall**. This removes KeaClip and the tools it downloaded. Tick **Also remove my settings and history** to remove those too. Your videos are never touched.

The portable (zip) version keeps everything in its own folder: delete the folder to remove it.
