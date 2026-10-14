<#
.SYNOPSIS
Writes the winget manifests (Langkurst.KeaClip) for a built release installer.

.DESCRIPTION
The installer URL is the GitHub release asset that build/release-manifest.ps1 also names, so run this for the
exact KeaClip-<version>-Setup.exe that is uploaded to the v<version> release. The three files land in
<Output>/manifests/l/Langkurst/KeaClip/<version>/, the layout of github.com/microsoft/winget-pkgs; submit them
there as a pull request (or with wingetcreate) once the release is public. Check them with
`winget validate --manifest <folder>`.
#>
param(
  [Parameter(Mandatory)][string]$Installer,
  [string]$Output,
  [string]$Repository = 'Langkurst/KeaClip'
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
if (-not $Output) { $Output = Join-Path $root 'artifacts\winget' }
$version = ([xml](Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Directory.Build.props has no usable Version.' }
$installerPath = (Resolve-Path -LiteralPath $Installer).Path
$sha = (Get-FileHash -Algorithm SHA256 -LiteralPath $installerPath).Hash
$folder = Join-Path $Output "manifests\l\Langkurst\KeaClip\$version"
New-Item -ItemType Directory -Path $folder -Force | Out-Null
$id = 'Langkurst.KeaClip'; $schema = '1.9.0'
$utf8 = [Text.UTF8Encoding]::new($false)
function Write-Manifest([string]$name, [string]$text) { [IO.File]::WriteAllText((Join-Path $folder $name), $text.Replace("`r`n", "`n"), $utf8) }

Write-Manifest "$id.yaml" @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.version.$schema.schema.json
PackageIdentifier: $id
PackageVersion: $version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: $schema
"@

# KeaClip.Setup.exe --silent installs per user (no administrator) into %LOCALAPPDATA%\Programs\KeaClip and
# registers HKCU\...\Uninstall\KeaClip, which winget uses to see the installed version.
Write-Manifest "$id.installer.yaml" @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.installer.$schema.schema.json
PackageIdentifier: $id
PackageVersion: $version
InstallerType: exe
Scope: user
InstallModes:
- interactive
- silent
- silentWithProgress
InstallerSwitches:
  Silent: --silent
  SilentWithProgress: --silent
UpgradeBehavior: install
ProductCode: KeaClip
AppsAndFeaturesEntries:
- DisplayName: KeaClip
  Publisher: Langkurst
  DisplayVersion: $version
  ProductCode: KeaClip
Installers:
- Architecture: x64
  InstallerUrl: https://github.com/$Repository/releases/download/v$version/KeaClip-$version-Setup.exe
  InstallerSha256: $sha
ManifestType: installer
ManifestVersion: $schema
"@

Write-Manifest "$id.locale.en-US.yaml" @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.defaultLocale.$schema.schema.json
PackageIdentifier: $id
PackageVersion: $version
PackageLocale: en-US
Publisher: Langkurst
PublisherUrl: https://github.com/Langkurst
PublisherSupportUrl: https://github.com/$Repository/issues
Author: Langkurst
PackageName: KeaClip
PackageUrl: https://github.com/$Repository
License: GPL-3.0-or-later
LicenseUrl: https://github.com/$Repository/blob/main/LICENSE
ShortDescription: Save, trim and share video clips. Free, no ads, no account.
Description: |-
  KeaClip saves videos and audio from a link, trims and shrinks clips to fit Discord or email in one click,
  turns a moment into a GIF, and shares a clip through an encrypted link that expires on its own.
  It uses yt-dlp and FFmpeg, which it fetches from their publishers on first run.
Moniker: keaclip
Tags:
- clip
- discord
- ffmpeg
- gif
- trim
- video
- video-downloader
- yt-dlp
ReleaseNotesUrl: https://github.com/$Repository/releases/tag/v$version
ManifestType: defaultLocale
ManifestVersion: $schema
"@

Write-Output "winget manifests for $id $version (installer SHA256 $sha): $folder"
