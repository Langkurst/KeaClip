<#
.SYNOPSIS
Builds KeaClip-<version>-third-party-source.zip: the corresponding source of the LGPL/GPL libraries the public download
includes (VLC and LibVLCSharp), to publish next to every public release (see docs/SOURCE.md).

.DESCRIPTION
Uses the source archives collected in local/license-audit/sources (docs/research/REDISTRIBUTION.md) and refuses to run if
any of them differs from its recorded SHA-256. Nothing is downloaded or uploaded here.
#>
param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
$version = ([xml](Get-Content -LiteralPath (Join-Path $workspace 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$sources = Join-Path $workspace 'local\license-audit\sources'
# Recorded in docs/research/REDISTRIBUTION.md when the archives were collected from their publishers.
$expected = [ordered]@{
  'vlc-6de05adcba.zip'     = '415D4F9010D1731899839FA2A1EACF497296A507D83D7EC9C773BEF17C0E2D3A'
  'vlc-3.0.24.tar.xz'      = 'E7CAB503D1D7D5849B89D2CF0E1EE60D0EF6D012407791B644B9CFC0CC225FDF'
  'libvlcsharp-4896d0e.zip' = '129058BE9C243F2438ADF554A440F16F5AAACDB7A84FB1F951D941BF26F04018'
}
foreach ($name in $expected.Keys) {
  $path = Join-Path $sources $name
  if (-not (Test-Path -LiteralPath $path)) { throw "Missing $name in local\license-audit\sources." }
  if ((Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash -ne $expected[$name]) { throw "$name does not match its recorded SHA-256." }
}
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $workspace "artifacts\release\$version" }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $output.StartsWith($workspace + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'The output must stay within this workspace.' }
New-Item -ItemType Directory -Path $output -Force | Out-Null
$staging = Join-Path $output 'third-party-source'
if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
New-Item -ItemType Directory -Path $staging | Out-Null
foreach ($name in $expected.Keys) { Copy-Item -LiteralPath (Join-Path $sources $name) -Destination $staging }
$readme = @"
Corresponding source for the LGPL/GPL libraries included in KeaClip $version (public download).

vlc-6de05adcba.zip       VLC source at revision 6de05adcbaf2e8b85fe86aad4169393098628119, the revision the bundled
                         VideoLAN.LibVLC.Windows 3.0.24 libraries were built from (https://code.videolan.org/videolan/vlc).
vlc-3.0.24.tar.xz        The official VLC 3.0.24 release tarball, for comparison.
libvlcsharp-4896d0e.zip  LibVLCSharp 3.10.1 at revision 4896d0e06d19ac40b46802cf7dc167d432f3fd63.

Libraries that VLC builds into its plugins from its contrib folder are listed, with the exact source address and
checksum of each, in contrib/src/<name>/rules.mak and SHA512SUMS inside the VLC source; VideoLAN mirrors those archives
at https://download.videolan.org/pub/contrib/. KeaClip's own source is KeaClip-$version-source.zip.

SHA-256:
$(($expected.GetEnumerator() | ForEach-Object { "$($_.Value.ToLowerInvariant())  $($_.Key)" }) -join "`n")
"@
[IO.File]::WriteAllText((Join-Path $staging 'README.txt'), $readme.Replace("`r`n", "`n"), [Text.UTF8Encoding]::new($false))
$zip = Join-Path $output "KeaClip-$version-third-party-source.zip"
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip -CompressionLevel Optimal
Remove-Item -LiteralPath $staging -Recurse -Force
Write-Output "Third-party source: $zip ($([math]::Round((Get-Item -LiteralPath $zip).Length / 1MB, 1)) MB)"
