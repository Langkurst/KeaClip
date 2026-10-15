# Third-party components

KeaClip itself is free software under the GNU GPL v3 or later. It is built on the open-source projects below. Their full copyright and licence texts ship with the app in the `Licenses` folder (and are in `docs/vendor-notices/` in the source); where to get their source code is in [SOURCE](SOURCE.md).

## Included in the download

| Component | Version | Licence |
| --- | --- | --- |
| .NET runtime | 10.0.12 | [MIT and runtime notices](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT) |
| Avalonia (UI framework) | 12.1.3 | [MIT](https://github.com/AvaloniaUI/Avalonia/blob/main/licence.md) |
| Inter font (fallback) | from Avalonia.Fonts.Inter 12.1.3 | [SIL Open Font Licence 1.1](https://github.com/rsms/inter/blob/master/LICENSE.txt) |
| Bricolage Grotesque font (headings) | ateliertriay/bricolage `84745e5` | [SIL Open Font Licence 1.1](https://github.com/ateliertriay/bricolage/blob/84745e5b96261ae5f8c6c856e262fe78d1d6efdd/OFL.txt) |
| CommunityToolkit.Mvvm | 8.4.0 | [MIT](https://github.com/CommunityToolkit/dotnet/blob/main/License.md) |
| LibVLCSharp and LibVLCSharp.Avalonia | 3.10.1 | [LGPL 2.1 or later](https://github.com/videolan/libvlcsharp/blob/3.x/LICENSE) |
| VLC libraries and plugins (VideoLAN.LibVLC.Windows) | 3.0.24 | [LGPL 2.1 or later; some plugins GPL 2.0 or later](https://www.videolan.org/legal.html) |

The LGPL libraries are separate, replaceable files in the app folder. Only the VLC plugins needed to preview local files are included.

## Downloaded on your PC, not included

KeaClip fetches these from their official sites the first time it starts and keeps them in its own folder. They are not part of the KeaClip download.

| Component | Licence |
| --- | --- |
| yt-dlp (Windows build) | [Unlicense; the Windows executable bundles GPL 3+ parts](https://github.com/yt-dlp/yt-dlp#licensing) |
| FFmpeg and FFprobe (gyan.dev essentials build) | [GPL 3](https://www.gyan.dev/ffmpeg/builds/), see [FFmpeg legal](https://ffmpeg.org/legal.html) |
| Deno | [MIT](https://github.com/denoland/deno/blob/main/LICENSE.md) |

## Services

Sharing uses the public [file.kiwi API](https://file.kiwi/api). KeaClip's upload and encryption code is its own, written against File.Kiwi's published protocol ([their MIT SDK](https://github.com/file-kiwi/node) was used as a reference).

## Artwork

The kea mascot, icons and app artwork were drawn for KeaClip.
