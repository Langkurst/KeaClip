# Bundled preview licence and corresponding-source audit

Audited 2026-10-07. Scope: the private Windows x64 preview, its actual published dependency inventory, cached NuGet archives, and installed managed engine binaries. No publication, account submission, paid action, or application/package edit was performed for this audit.

**Update 2026-10-13:** KeaClip is now licensed GPL-3.0-or-later, which is compatible with the bundled GPL FFmpeg/yt-dlp executables and the GPL LibVLC plugins loaded in-process, and the 0.2.0 package ships KeaClip's complete source, all collected notices and a written offer for the bundled components' corresponding source ([SOURCE](../SOURCE.md)). That closes the application-licence question below; the deeper transitive source mapping items remain recorded for completeness. This is the project's own reading, not legal advice.

**Original result (2026-10-07): personal testing can continue; giving the bundled build to a friend or publishing it is not cleared.** Complete notice texts and several matching main-project source snapshots have been collected. The native dependency/source closure remains incomplete. A free private copy given to another person still conveys the GPL/LGPL binaries; calling it a private preview does not remove the licence obligations. The relevant terms are in [GPLv3 sections 0, 1, 5 and 6](https://www.gnu.org/licenses/gpl-3.0.html) and [LGPLv2.1 sections 4 and 6](https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html).

## Exact inventory

The inspected portable snapshot is `artifacts/packages/KeaClip-0.1.0-win-x64-20261007-185952`. Its `.deps.json` identifies 22 runtime NuGet packages; native VideoLAN build assets and the self-contained .NET runtime are additional delivered components. Current project settings now select VLC x64 only; the inspected older snapshot contains x86 and ARM64 too. Regenerate and inspect the final artifact before relying on that reduction.

| Delivered component | Exact version / source identity | Licence and material collected |
| --- | --- | --- |
| .NET self-contained Windows x64 runtime | 10.0.12 | MIT plus actual runtime `THIRD-PARTY-NOTICES.TXT` and `.version` metadata from the matching runtime package. |
| Avalonia family: Avalonia, Desktop, Fonts.Inter, Themes.Fluent, FreeDesktop, FreeDesktop.AtSpi, HarfBuzz, Native, Remote.Protocol, Skia, Win32, X11 | All 12.1.3; source `8eeda4f6f546165b3f72e63c9f42247abb306905` | MIT wrapper/source text collected. Font licence separately below. Inventory includes platform assemblies actually present, even in a Windows build. |
| CommunityToolkit.Mvvm | 8.4.0; `638b41dad30dffabb123a39aa38eabc7e3721371` | MIT; package `License.md` collected. |
| MicroCom.Runtime | 0.11.6; `76785efcafd91b5902fd19dd11145f6dd655b7b4` | MIT; pinned upstream text collected. |
| Tmds.DBus.Protocol | 0.94.1; `b4a7fed0b878f74cb54f7cca84d2889af4e596ba` | MIT; pinned upstream `COPYING` collected. |
| SkiaSharp + SkiaSharp.NativeAssets.Win32 | 3.119.4; `f568ac94dd768ef9a2f593537cfde2dd0d348ef5` | MIT wrapper; actual native package licence and 139,775-byte third-party notice bundle collected. Retain the bundled native licence texts too. |
| HarfBuzzSharp + HarfBuzzSharp.NativeAssets.Win32 | 8.3.1.3; `2888c737ad016d584c74525e2d35db5097ea8576` | MIT wrapper; actual package licence and native third-party notices collected. |
| Avalonia.Angle.Windows.Natives | 2.1.27548.20260419; `1c89805903c1482166356d3b950d474973180e61` | Package `LICENSE` is BSD 3-clause, not a blanket Avalonia MIT notice. Collected; underlying ANGLE dependency notices still need closure. |
| LibVLCSharp + LibVLCSharp.Avalonia | 3.10.1; `4896d0e06d19ac40b46802cf7dc167d432f3fd63` | LGPL 2.1-or-later; full licence and pinned source archive collected. |
| VideoLAN.LibVLC.Windows native assets | NuGet 3.0.24; actual DLL identifies `3.0.24-rc1-0-g6de05adcba`, GCC 6.4.0 | NuGet declares LGPL 2.1-or-later; delivered plugins include separately GPL-licensed modules. Actual native revision source candidate collected; matching native contributions/build provenance incomplete. |
| Inter font assets from Avalonia.Fonts.Inter | Package 12.1.3; Regular font in the pinned Avalonia source identifies `Version 3.019;git-0a5106e0b` | SIL Open Font License 1.1. Font copyright/licence metadata and [matching upstream licence](https://github.com/rsms/inter/blob/0a5106e0b/LICENSE.txt) collected. Keep font copyright/OFL text; font assets are not covered solely by the package's MIT metadata. Final package resource byte matching remains to be checked. |
| yt-dlp Windows x64 standalone | 2026.08.19 on embedded Python 3.10.11; source `3a08beaf031ab68f966401ead017ac81fe8486cf` | Combined executable GPL 3-or-later, while yt-dlp source is Unlicense. Exact embedded third-party licence bundle and main source/build requirements collected. |
| FFmpeg + FFprobe, Gyan essentials | 9.0.2; source `946fcce07b6dcd0331c8cc609192aeff5e1924f8`; GCC 16.2.0 Rev3/MSYS2 | Publisher calls these static GPLv3 builds; actual `--enable-gpl --enable-version3 --enable-static`, without `--enable-nonfree`. Full delivered publisher notice/readme, actual build configuration and main source archive collected. External source closure incomplete. |
| Deno Windows x64 | 2.9.7; V8 15.0.245.2-rusty; TypeScript 6.0.3 | MIT top-level text and exact tag Cargo.lock collected. Full native/JS dependency licence and source obligations remain unverified. |

Exact NuGet licence metadata, source commits and copied notice filenames are in the ignored local `package-inventory.json`. These observations apply to these exact versions; they must be renewed when components update. Test packages, headless UI tooling and the independent File.Kiwi JS compatibility SDK are not dependencies in this published runtime inventory.

## Native and bundled engine findings

**VLC:** the native NuGet archive contains 1,560 entries across three architectures, including 321 x64 plugin DLLs. It does not provide a full licence/source bundle; its NuGet licence URL alone is insufficient. The x64 list includes `plugins/codec/libx26410b_plugin.dll`. The [x264 module source at the actual native revision](https://github.com/videolan/vlc/blob/6de05adcbaf2e8b85fe86aad4169393098628119/modules/codec/x264.c) explicitly uses GPL 2-or-later. This is evidence of a mixed native distribution; the package's LGPL expression does not describe every plugin, native contribution or bundled Lua/JAR/resource asset.

Calling the version/compiler/changeset exports required no VLC instance or playback window. The result resolves to full VLC source commit `6de05adcbaf2e8b85fe86aad4169393098628119`. Both this revision and the official final 3.0.24 tarball were collected; **the final tarball is not proof of the RC1 DLL's matching source**. The [official source download](https://images.videolan.org/vlc/download-sources.html) and [native NuGet project](https://code.videolan.org/videolan/libvlc-nuget) identify the source/build route, but matching native contribution revisions, patches, package build configuration and scripts still need verification. Generic older contribution archives are not accepted as a match.

**FFmpeg:** [FFmpeg's licence guidance](https://ffmpeg.org/legal.html) distinguishes LGPL builds, GPL-enabled builds and non-redistributable nonfree combinations. This preview uses the GPL-enabled build, corroborated by its executable configuration and [publisher build page](https://www.gyan.dev/ffmpeg/builds/). Its readme names 37 external library versions, recorded in ignored `ffmpeg-external-versions.txt`; examples include x264 `v0.165.3223`, x265 `4.3-45-g116b875`, rubberband `v4.0.0` and vidstab `v1.1.2-214-ge2445c4`. Build configuration also enables libraries whose exact versions are absent from that readme section, including GnuTLS, fontconfig, iconv, GMP, libxml2, bzip2, lzma and zlib. Resolve and collect incorporated source, patches and build scripts; the FFmpeg core archive alone is incomplete. The mutable `ffmpeg-release-essentials.zip` URL must be paired with exact binary hashes and retained provenance, not treated as a durable version pin.

**yt-dlp:** the [official licensing statement](https://github.com/yt-dlp/yt-dlp#licensing) treats the combined standalone executable as GPL 3-or-later. Its actual PyInstaller archive has 151 entries and an embedded `THIRD_PARTY_LICENSES.txt` of 248,243 bytes. This text was extracted without executing embedded code and matches the pinned source text after line-ending normalization. Its SHA-256 is `5B7BC105C562B81B90D1A2CD5A8CC8B336EE107AE0AE81CE58450CD8B1BC2AD4`. Embedded metadata directly verifies curl_cffi 0.16.0 and websockets 16.1.1; it does not establish every native dependency version. The comprehensive notice text includes components for multiple platforms and is not, by itself, proof every named library is in this Windows executable.

Collected build instructions include the [exact build workflow](https://github.com/yt-dlp/yt-dlp/blob/3a08beaf031ab68f966401ead017ac81fe8486cf/.github/workflows/build.yml), PyInstaller packaging script, curl-cffi requirements and hash-pinned Windows requirements. These point to the publisher's PyInstaller 6.22.0 build `2026.08.19.215425`. Before distribution, collect and map the actual Python/native/package sources, including the matching PyInstaller fork/bootloader source and build inputs. Preserve relevant PSF/MIT/BSD/Apache/MPL/GPL/LGPL notices and comply with any source obligations of components actually included. A source-only Unlicense notice does not describe the combined executable.

**Deno:** [the pinned MIT licence](https://github.com/denoland/deno/blob/v2.9.7/LICENSE.md) covers Deno's original code; native Rust crates/V8 and copied JS/TypeScript components have their own terms. Cargo.lock and the exact version output are useful provenance, not a completed notice/source audit. No complete official binary-specific third-party notice bundle was established in this review. That remains a gate rather than a claim that Deno contains only MIT code.

## Corresponding-source materials and delivery plan

Collected materials are ignored under `local/license-audit/`; they contain no credentials or personal media. `materials-manifest.json` records 90 relative file paths, byte counts and SHA-256 values. `source-manifest.json`, `native-source-manifest.json` and `native-package-manifest.json` preserve primary URLs, pinned identities and hashes. `supplement-manifest.json` records the successfully recovered pinned Inter licence and actual native VLC revision files; an initial unavailable Avalonia OFL path remains documented in the original collection manifest. Upstream URLs were accessed 2026-10-07.

| Source candidate collected | Exact archive identity | Current completeness |
| --- | --- | --- |
| LibVLCSharp | [4896d0e source](https://codeload.github.com/videolan/libvlcsharp/zip/4896d0e06d19ac40b46802cf7dc167d432f3fd63); SHA-256 `129058BE9C243F2438ADF554A440F16F5AAACDB7A84FB1F951D941BF26F04018` | Matches NuGet source identity; contains library source/build projects. Final delivery and replacement demonstration remain. |
| Native VLC revision | [6de05adcba source](https://codeload.github.com/videolan/vlc/zip/6de05adcbaf2e8b85fe86aad4169393098628119); `415D4F9010D1731899839FA2A1EACF497296A507D83D7EC9C773BEF17C0E2D3A` | Matches reported main-source changeset; contribution source/configuration and package-specific patches not established. |
| VLC final 3.0.24, comparison only | [Official tarball](https://download.videolan.org/pub/videolan/vlc/3.0.24/vlc-3.0.24.tar.xz); `E7CAB503D1D7D5849B89D2CF0E1EE60D0EF6D012407791B644B9CFC0CC225FDF` | Hash matches official download information; cannot substitute for the RC1 runtime source. |
| FFmpeg | [946fcce source](https://codeload.github.com/FFmpeg/FFmpeg/zip/946fcce07b6dcd0331c8cc609192aeff5e1924f8); `8CFFA6CCF78DF6D39D57BD61BAAF19E8BF0A7D590EEF0D838DE69932E8DE3572` | Matches publisher main source; external sources/patches/build recipe incomplete. |
| yt-dlp | [3a08beaf source](https://codeload.github.com/yt-dlp/yt-dlp/zip/3a08beaf031ab68f966401ead017ac81fe8486cf); `FE8E4EF0B67554C7C2F2C016671FB3A130BB1333BD47DBBA9268BE188D825950` | Main source/build requirements matched; bundled dependency/bootloader source closure incomplete. |

Concrete delivery route: provide the complete matching source alongside the preview, rather than relying on an informal promise or assuming upstream links alone satisfy every licence. Build a `Licenses/` directory with full copyright/licence/notice texts, and a `CorrespondingSource/` directory or companion archive containing source, applicable patches, interface definitions, generation/build/install scripts and a manifest mapping every covered binary to its inputs. Include straightforward rebuild/replacement instructions and a hash list. Obtain matching inputs from the primary supplier or perform a controlled build from a fully recorded source set when supplier provenance cannot be established. This is local preparation; no public hosting is needed to supply source alongside a friend copy.

Loose native DLLs and managed LibVLCSharp assemblies are a useful start for LGPL replacement, but the final portable/installed app must demonstrate loading a compatible user-modified library. Provide prominent library notices/licence copies and terms allowing the reverse engineering needed to debug library modifications. LGPL section 6's shared-library option does not remove section 4 obligations when the library binary itself is delivered. If replacement is blocked, supply a compliant relink route instead. No such final-artifact demonstration was performed in this audit.

## Public distribution decision (2026-10-14)

KeaClip is GPL-3.0-or-later (see `LICENSE`), which settles the application-licence question below: the GPL-2.0-or-later
VLC plugins and LGPL-2.1-or-later libraries are compatible with it.

- **The public download leaves FFmpeg, yt-dlp and Deno out** (`build/package.ps1` without `-IncludeComponents`). KeaClip
  downloads them on the user's PC only when the user chooses Prepare components, directly from their publishers
  (`ManagedComponentService` accepts only `github.com/yt-dlp/yt-dlp/releases/download/`, `github.com/denoland/deno/releases/download/`
  and the gyan.dev essentials build). The project therefore does not redistribute them, and their FFmpeg static-dependency,
  yt-dlp bootloader and Deno notice gates below no longer block a public release. Personal builds with
  `-IncludeComponents` stay private (`PRIVATE_BUILD.txt`).
- **Included and covered:** VLC 3.0.24 (VideoLAN.LibVLC.Windows) and LibVLCSharp 3.10.1. Their corresponding source is
  published next to each public release as `KeaClip-<version>-third-party-source.zip` (`build/third-party-source.ps1`, which
  checks the archives against the hashes in the table above), with directions to VLC's contrib sources (each listed with
  address and checksum in `contrib/src/*/rules.mak` and `SHA512SUMS`, 117 and 111 files in the 6de05adcba source; mirrored
  at download.videolan.org/pub/contrib). The managed packages' notices are in `Licenses/`.
- **Still worth doing before or soon after the first public release:** demonstrate replacing LibVLC/LibVLCSharp with a
  user-built copy in the installed app (LGPL replacement). Done in 0.3.0: the VLC plugins were trimmed from 321 to the
  about 190 the local preview needs (no network access, streaming-out, encoders such as x264, subtitle rendering or
  scripting), see `VlcWindowsX64ExcludeFiles` in `src/Klippi.Desktop/Klippi.Desktop.csproj`.

This is good-faith compliance as understood by the project, not legal advice.

## Satisfiable now versus remaining gates

Can be completed locally now without spending or publication:

- Copy the collected full .NET, managed MIT, BSD/ANGLE, Inter OFL, Skia/HarfBuzz native notices, VLC/LibVLCSharp LGPL/GPL and exact yt-dlp notices into the package. Preserve the FFmpeg publisher licence/readme already retained by component provisioning.
- Deliver the matching LibVLCSharp source and collected main-project source candidates with an honest completeness record; retain exact binary/source hashes and immutable source identities.
- Keep x64-only native assets in the next package; inventory the regenerated output and demonstrate replaceable LGPL libraries before any delivery claim.

Still unresolved before friend or public distribution:

- Complete VLC native contribution, plugin/resource licence and exact source/build/patch mapping. Resolve how the delivered GPL plugins interact with the application's chosen licence and actual loading. Original application-code licensing is currently undecided. This audit does not establish that the combined native playback arrangement permits a proprietary application, nor does package metadata alone prove the whole application must be GPL.
- Complete FFmpeg's statically incorporated dependency sources, exact versions, patches and build recipe. Complete the actual yt-dlp executable's bundled native/Python/bootloader source mapping.
- Complete Deno and ANGLE transitive notice/source review and close any non-permissive dependency obligations actually present.
- Update packaging to carry the complete notice/source materials, then inspect actual portable and installer outputs. The inspected `package.ps1` copies the brief notice inventory and component executables/manifest, but not the collected full notices or FFmpeg notice directory. `PRIVATE_BUILD.txt` accurately leaves the distribution gate pending.
- Resolve a compatible application licence/delivery policy and preserve LGPL modification/debugging permissions. Subprocess separation helps describe FFmpeg/yt-dlp use but does not by itself settle every GPL aggregation question; native GPL plugin loading is a different arrangement that needs an explicit decision.

These are distribution gates, separate from source tests, media workflow tests and personal installed-preview usability. Main archives and notice collection are verified preparation, not a claim of complete Corresponding Source or redistribution approval.

## Audit validation

Read the current package recipe, dependency metadata, native NuGet archive contents, actual component configurations and primary publisher sources. Source archive hashes and exact embedded yt-dlp notice extraction were checked. The pinned Inter licence was recovered using the font's own source identity. No dependency build, GPU/player launch, live upload or visible browser interaction was required. This document and ignored audit material are the only changes owned by this audit.
