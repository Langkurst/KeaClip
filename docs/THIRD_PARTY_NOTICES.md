# Third-party components and distribution record

KeaClip itself is GPL-3.0-or-later (updated 2026-10-14). Public downloads include LibVLC/VLC and the .NET libraries below; FFmpeg, yt-dlp and Deno are not included and are fetched from their publishers on the user's PC. Full copyright, licence and notice texts are in `Licenses/` in the package and `docs/vendor-notices/` in the source; the audit behind this is `docs/research/REDISTRIBUTION.md`, and the written source offer is in [SOURCE](SOURCE.md).

| Component | Version | Licence/source |
| --- | --- | --- |
| .NET runtime | Packaged target10.0.12 | [MIT and runtime notices](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT) |
| Avalonia/UI/Fluent/Headless | 12.1.3 | [MIT](https://github.com/AvaloniaUI/Avalonia/blob/main/licence.md) |
| Inter font | Avalonia.Fonts.Inter12.1.3 | [SIL Open Font Licence1.1](https://github.com/rsms/inter/blob/master/LICENSE.txt) |
| Bricolage Grotesque font (SemiBold, Bold) | ateliertriay/bricolage `84745e5b96261ae5f8c6c856e262fe78d1d6efdd` | [SIL Open Font Licence 1.1](https://github.com/ateliertriay/bricolage/blob/84745e5b96261ae5f8c6c856e262fe78d1d6efdd/OFL.txt) (copy in `Licenses/upstream/bricolage-grotesque-OFL.txt`) |
| CommunityToolkit.Mvvm | 8.4.0 | [MIT](https://github.com/CommunityToolkit/dotnet/blob/main/License.md) |
| LibVLCSharp.Avalonia | 3.10.1 | [LGPL2.1-or-later](https://github.com/videolan/libvlcsharp/blob/3.x/LICENSE) |
| VideoLAN.LibVLC.Windows | package3.0.24; DLL changeset3.0.24-rc1-0-g6de05adcba | [Publisher package/licence](https://www.nuget.org/packages/VideoLAN.LibVLC.Windows/3.0.24); actual native set mixes LGPL/GPL plugins; exact source/build closure required |
| yt-dlp Windows standalone | observed2026.08.19 | [Combined executable GPL3+; source Unlicense; third-party licensing](https://github.com/yt-dlp/yt-dlp#licensing) |
| FFmpeg/FFprobe Gyan essentials | observed9.0.2 | [GPLv3 build/source information](https://www.gyan.dev/ffmpeg/builds/), [FFmpeg legal](https://ffmpeg.org/legal.html); exact corresponding-source/dependency/build inventory remains pending |
| Deno | observed2.9.7 | [MIT with third-party components](https://github.com/denoland/deno/blob/main/LICENSE.md) |
| File.Kiwi compatibility references | SDK1.0.9/wormhole-crypto0.3.1 | [MIT SDK source](https://github.com/file-kiwi/node); protocol implementation uses .NET standard cryptography and independent fixtures |

Managed component `current.json` records exact launch version, trusted source URL and SHA-256, which may differ after a reviewed update. Do not combine this table's development observations with an updated binary and call it an exact distribution audit. Tests/tooling are separate from distributed runtime dependencies.

The app keeps media engines as subprocesses and native playback DLLs replaceable. Reject FFmpeg `--enable-nonfree`. Preserve the respective copyright/licence texts, transitive notices, required source and build/relink information in release artifacts. A licence link alone does not fulfill every obligation.

All kea graphics were created as original editable vector artwork for this project; see the assets source. No unrelated project artwork is reused.
