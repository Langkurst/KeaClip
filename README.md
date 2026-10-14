<p align="center"><img src="docs/images/kea-128.png" width="96" alt="KeaClip kea"></p>

<h1 align="center">KeaClip</h1>

<p align="center"><b>Save it. Trim it. Share it.</b><br>
A free Windows app for saving videos and audio you're allowed to keep, cutting out the good part and sending it to someone.<br>
No ads, no account, no tracking.</p>

<p align="center"><a href="https://ko-fi.com/keaclip">Support KeaClip on Ko-fi</a></p>

![KeaClip: paste a link, analyse, download, then trim](docs/images/keaclip-demo.gif)

## What it does

- **Save** video or audio from a link: pick Video + audio, Audio only, Video only or a full Archive, choose quality and format, or just press Download. Playlists let you pick which videos you want, and **Just part of it** saves only the bit you need (say 0:45 to 1:20 of a long stream) without downloading the rest.
- **Queue** as many as you like. Downloads keep going while you use the rest of the app, can be paused and retried, and your queue is still there after a restart.
- **Trim** any video or audio file: drag the orange handles, then export just that part. Pull out the sound as MP3, fade in and out, make a short soundboard clip, or export the selection as a **GIF**.
- **Fit it for Discord** in one click: Discord (10 MB), Email (25 MB), Nitro Basic (50 MB) or Nitro (500 MB) aims the clip just under that size.
- **Your game clips** are waiting when you open the Editor: new recordings from Xbox Game Bar, AMD ReLive, NVIDIA and OBS show up ready to trim.
- **Share** with an encrypted [file.kiwi](https://file.kiwi) link. Sharing uses the [file.kiwi API](https://file.kiwi/api): the file is encrypted on your PC before it uploads, and the link expires on its own.
- **Keep** everything in your Library to open, edit or share again later. Removing a record never deletes the file.

| Trim and export | Your library |
| --- | --- |
| ![The editor with the trim timeline](docs/images/editor-dark.png) | ![The library in the light theme](docs/images/library-light.png) |

## Get it

KeaClip runs on Windows 10 and 11 (64-bit).

1. Download the installer from the [Releases page](https://github.com/Langkurst/KeaClip/releases).
2. Run it. It installs just for you, no admin rights needed.
3. On first launch choose **Prepare components**. KeaClip fetches the free tools it uses (yt-dlp, FFmpeg and Deno) from their official sites into its own folder. Nothing else on your PC changes.

Windows may show an "unknown publisher" warning until KeaClip is code-signed. That's what the [Ko-fi goal](https://ko-fi.com/keaclip) is for.

## Good to know

- **Only save media you own or are allowed to keep.** KeaClip never gets around DRM or paywalls.
- **Your stuff stays on your PC.** Settings, history and any browser sign-in stay on this device. Nothing is uploaded unless you choose Share.
- **Your originals are safe.** Editing makes a new file. KeaClip only deletes an original if you tick that option, and only after the new file checks out.
- **Share links are free to download for 24 hours** (12 hours for files of 10 to 20 GB, 6 hours for 20 GB or more), then they stop working. These are file.kiwi's times for API uploads and differ from uploads made on the file.kiwi website; see [free download hours by file size](https://file.kiwi/api#free-download-hours-by-file-size).

## Support

KeaClip is free and always will be. If it saved you some time, you can [shout me a coffee on Ko-fi](https://ko-fi.com/keaclip). It helps me keep making it better.

Found a bug or want something added? Open an issue.

## Licence

KeaClip is free software under the [GNU GPL v3 or later](LICENSE). It's built with [Avalonia](https://avaloniaui.net), [LibVLC](https://www.videolan.org/vlc/libvlc.html), and works with [file.kiwi](https://file.kiwi/api) for sharing, [yt-dlp](https://github.com/yt-dlp/yt-dlp), [FFmpeg](https://ffmpeg.org) and [Deno](https://deno.com). Their licences ship with the app; source details are in [SOURCE.md](docs/SOURCE.md).

Building from source: see [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md).
