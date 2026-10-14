# Source code and licences

KeaClip is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later version (see `LICENSE`). It is distributed WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.

## KeaClip's own source

The complete corresponding source of this build is included next to the app as `KeaClip-<version>-source.zip` (the exact Git revision recorded in `BUILD_INFO.json`). It builds with the .NET 10 SDK: `dotnet build Klippi.sln -c Release`.

## Included in the download

| Component | Version | Licence | Corresponding source |
| --- | --- | --- | --- |
| LibVLC native libraries and plugins (VideoLAN.LibVLC.Windows), used for the preview player | 3.0.24 | LGPL-2.1-or-later; some plugins GPL-2.0-or-later | `KeaClip-<version>-third-party-source.zip` next to the download (VLC source at the built revision `6de05adcbaf2e8b85fe86aad4169393098628119` and the 3.0.24 release tarball); libraries VLC builds in from its `contrib` folder are listed with their source addresses in that source's `contrib/src/*/rules.mak` and mirrored by VideoLAN at https://download.videolan.org/pub/contrib/ |
| LibVLCSharp | 3.10.1, source `4896d0e06d19ac40b46802cf7dc167d432f3fd63` | LGPL-2.1-or-later | in `KeaClip-<version>-third-party-source.zip`; also https://code.videolan.org/videolan/LibVLCSharp |
| Avalonia, .NET runtime, SkiaSharp, HarfBuzzSharp, CommunityToolkit and other managed packages | see `Licenses/` | MIT, BSD-3-Clause, Apache-2.0 and others as listed | see each notice in `Licenses/` |
| Inter font | as packaged by Avalonia | SIL Open Font License 1.1 | https://github.com/rsms/inter |
| Bricolage Grotesque font (headings) | commit 84745e5 | SIL Open Font License 1.1 | https://github.com/ateliertriay/bricolage |

Full licence and notice texts for every included component are in the `Licenses` folder.

The LGPL libraries (LibVLC, LibVLCSharp) are shipped as separate, replaceable files in the app folder; you may replace them with modified versions of the same interface.

## Downloaded by KeaClip on your PC, not included

The public download does **not** contain FFmpeg, yt-dlp or Deno. When you choose **Prepare components**, KeaClip downloads them on your PC directly from their publishers and checks them:

| Component | Downloaded from | Licence |
| --- | --- | --- |
| FFmpeg and FFprobe ("essentials" build) | https://www.gyan.dev/ffmpeg/builds/ (source: https://github.com/FFmpeg/FFmpeg) | GPL-3.0-or-later |
| yt-dlp (standalone Windows executable) | https://github.com/yt-dlp/yt-dlp/releases | yt-dlp source Unlicense; the executable bundles GPL-3.0-or-later parts |
| Deno | https://github.com/denoland/deno/releases | MIT, with third-party notices |

Those publishers distribute them and provide their source. Personal builds made with `build/package.ps1 -IncludeComponents` do include them; such a copy is for the builder's own use and carries `PRIVATE_BUILD.txt`.

## Written offer

For at least three years after you received this copy, the person or organisation who distributed it to you will, on request, provide the complete corresponding source code of the GPL- and LGPL-licensed components included in it (as built for this release), on a medium customarily used for software interchange, for no more than the cost of physically performing the distribution. Requests: the distributor of your copy (GitHub: Langkurst).

This file describes licensing obligations as understood by the project; it is not legal advice.
