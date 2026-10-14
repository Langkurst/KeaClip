<#
.SYNOPSIS
Builds KeaClip and publishes it to the local update feed, so the installed app on this PC offers it.

.DESCRIPTION
One command for a new version on this PC: bump <Version> in Directory.Build.props, commit, then run
this. It builds the portable package (with components), builds and self-tests the installer, and
publishes the installer to %LOCALAPPDATA%\KeaClip-Releases with keaclip-update.json beside it. The
installed app reads that feed at every start (no network, no public hosting) and offers
"Update now"; its download is still size-capped and SHA-256 checked, and the installer refuses the
same or an older version. Older installers in that folder are removed (they are copies of build
output). Use -Installer/-Portable to publish an already built installer instead of building.

.EXAMPLE
pwsh -File build/release-local.ps1 -Notes "Faster trimming and a new share option."
#>
param([string]$Notes, [string]$Installer, [string]$Portable)
$ErrorActionPreference = 'Stop'
$localWorkspace = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
$localVersion = ([xml](Get-Content -LiteralPath (Join-Path $localWorkspace 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if ($localVersion -notmatch '^(0|[1-9]\d{0,4})\.(0|[1-9]\d{0,4})\.(0|[1-9]\d{0,4})$') { throw "Directory.Build.props has no usable Version ($localVersion)." }

if (-not $Installer) {
    if (-not $Portable) {
        $packageOutput = & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'package.ps1') -IncludeComponents -Edition Personal
        if ($LASTEXITCODE -ne 0) { throw 'Packaging failed.' }
        $Portable = ($packageOutput | Where-Object { $_ -like 'Portable archive: *' } | Select-Object -Last 1) -replace '^Portable archive: ', ''
        if (-not $Portable -or -not (Test-Path -LiteralPath $Portable)) { throw 'Packaging did not report a portable archive.' }
    }
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'installer-publish.ps1') -Payload $Portable -SelfTest | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Installer build or self-test failed.' }
    $Installer = Join-Path $localWorkspace 'artifacts\installer\KeaClip.Setup.exe'
}
$Installer = (Resolve-Path -LiteralPath $Installer).Path
$installerVersion = (Get-Item -LiteralPath $Installer).VersionInfo.ProductVersion
if ($installerVersion -notmatch ('^' + [regex]::Escape($localVersion) + '($|[+-])')) { throw "The installer reports version '$installerVersion', not $localVersion. Rebuild it from this source." }

$feedFolder = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'KeaClip-Releases'
New-Item -ItemType Directory -Path $feedFolder -Force | Out-Null
if ((Get-Item -LiteralPath $feedFolder).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'The local feed folder is a link; refusing to publish into it.' }
$assetName = "KeaClip-$localVersion-Setup.exe"
$assetPath = Join-Path $feedFolder $assetName
Copy-Item -LiteralPath $Installer -Destination $assetPath -Force
$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $assetPath).Hash.ToLowerInvariant()
if ($hash -ne (Get-FileHash -Algorithm SHA256 -LiteralPath $Installer).Hash.ToLowerInvariant()) { throw 'Copying the installer changed it.' }

$releaseNotes = if ($Notes) { $Notes } else { "KeaClip $localVersion, built on this PC." }
$releaseNotes = $releaseNotes.Replace("`r`n", "`n").Trim()
if ($releaseNotes.Length -gt 8000) { throw 'Release notes are limited to 8,000 characters.' }
$manifest = [ordered]@{
    schema = 1
    product = 'KeaClip'
    version = $localVersion
    publishedAt = [DateTimeOffset]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ', [Globalization.CultureInfo]::InvariantCulture)
    notes = $releaseNotes
    # A local feed names files beside it, never links.
    installer = [ordered]@{ url = $assetName; sha256 = $hash; size = (Get-Item -LiteralPath $assetPath).Length }
}
# Written last and swapped in whole, so the app never reads a feed whose installer isn't fully there.
$feedPath = Join-Path $feedFolder 'keaclip-update.json'
[IO.File]::WriteAllText("$feedPath.write", ($manifest | ConvertTo-Json -Depth 4) + "`n", [Text.UTF8Encoding]::new($false))
Move-Item -LiteralPath "$feedPath.write" -Destination $feedPath -Force

Get-ChildItem -LiteralPath $feedFolder -File -Filter 'KeaClip-*-Setup.exe' | Where-Object Name -ne $assetName | Remove-Item -Force
Write-Output "Local feed: $feedPath"
Write-Output "KeaClip $localVersion is ready; the installed app offers it at its next start (or Settings > Check for updates)."
