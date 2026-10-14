<#
.SYNOPSIS
Writes keaclip-update.json, the release feed KeaClip checks, for a built installer and portable ZIP.

.DESCRIPTION
Reads the version from Directory.Build.props, checks that the installer and the portable ZIP really
are that version, copies them into artifacts\release\<version>\ under stable release-asset names,
hashes them (SHA-256) and writes keaclip-update.json plus SHA256SUMS.txt beside them. Upload all four
files to the GitHub release tagged v<version> (see docs/RELEASING.md). Nothing is uploaded here.

.EXAMPLE
pwsh -File build/release-manifest.ps1 -Installer artifacts\installer\KeaClip.Setup.exe -Portable artifacts\packages\KeaClip-0.3.0-win-x64-20261102-101500.zip -NotesFile notes.md
#>
param(
    [string]$Installer,
    [string]$Portable,
    [string]$NotesFile,
    [string]$Notes,
    [string]$Repository = 'Langkurst/KeaClip',
    [string]$BaseUrl,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$releaseWorkspace = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
$releaseVersion = ([xml](Get-Content -LiteralPath (Join-Path $releaseWorkspace 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
# The same strict form the app accepts (ReleaseVersion): three numbers, no leading zeros, no suffix.
if ($releaseVersion -notmatch '^(0|[1-9]\d{0,4})\.(0|[1-9]\d{0,4})\.(0|[1-9]\d{0,4})$') { throw "Directory.Build.props has no usable Version ($releaseVersion)." }
if (-not $Installer -and -not $Portable) { throw 'Give -Installer and/or -Portable.' }
if ($Repository -notmatch '^[A-Za-z0-9-]+/[A-Za-z0-9._-]+$') { throw 'Repository must look like owner/name.' }
$releaseTag = "v$releaseVersion"
if (-not $BaseUrl) { $BaseUrl = "https://github.com/$Repository/releases/download/$releaseTag/" }
if (-not $BaseUrl.EndsWith('/')) { $BaseUrl += '/' }
$releaseBase = [Uri]$BaseUrl
if (-not $releaseBase.IsAbsoluteUri -or $releaseBase.Scheme -ne 'https' -or $releaseBase.UserInfo) { throw 'BaseUrl must be an https link without credentials.' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $releaseWorkspace "artifacts\release\$releaseVersion" }
$releaseOutput = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $releaseOutput.StartsWith($releaseWorkspace + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'The release output must stay within this workspace.' }
New-Item -ItemType Directory -Path $releaseOutput -Force | Out-Null

function Add-ReleaseAsset([string]$Source, [string]$Name, [string]$Extension) {
    $full = (Resolve-Path -LiteralPath $Source).Path
    if ([IO.Path]::GetExtension($full) -ne $Extension) { throw "$Source is not a $Extension file." }
    $target = Join-Path $releaseOutput $Name
    if ($full -ne $target) { Copy-Item -LiteralPath $full -Destination $target -Force }
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $target).Hash.ToLowerInvariant()
    if ($hash -ne (Get-FileHash -Algorithm SHA256 -LiteralPath $full).Hash.ToLowerInvariant()) { throw "Copying $Name changed it." }
    $size = (Get-Item -LiteralPath $target).Length
    if ($size -le 0 -or $size -gt 2GB) { throw "$Name has an implausible size ($size bytes)." }
    [ordered]@{ url = ([Uri]::new($releaseBase, [Uri]::EscapeDataString($Name))).AbsoluteUri; sha256 = $hash; size = $size }
}

$releaseNotes = if ($NotesFile) { [IO.File]::ReadAllText((Resolve-Path -LiteralPath $NotesFile).Path, [Text.Encoding]::UTF8) } elseif ($Notes) { $Notes } else { "KeaClip $releaseVersion. See the release page for details." }
$releaseNotes = $releaseNotes.Replace("`r`n", "`n").Trim()
if ($releaseNotes.Length -gt 8000) { throw 'Release notes are limited to 8,000 characters; the app would cut them.' }

$manifest = [ordered]@{
    schema = 1
    product = 'KeaClip'
    version = $releaseVersion
    publishedAt = [DateTimeOffset]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ', [Globalization.CultureInfo]::InvariantCulture)
    notes = $releaseNotes
    releasePage = "https://github.com/$Repository/releases/tag/$releaseTag"
}
if ($Installer) {
    # An installer of another version would be refused by its own upgrade mode, so it is refused here first.
    $installerVersion = (Get-Item -LiteralPath $Installer).VersionInfo.ProductVersion
    if ($installerVersion -notmatch ('^' + [regex]::Escape($releaseVersion) + '($|[+-])')) { throw "The installer reports version '$installerVersion', not $releaseVersion. Rebuild it from this source." }
    $manifest.installer = Add-ReleaseAsset $Installer "KeaClip-$releaseVersion-Setup.exe" '.exe'
}
if ($Portable) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $Portable).Path)
    try {
        $buildInfo = $zip.Entries | Where-Object { $_.Name -eq 'BUILD_INFO.json' -and ($_.FullName.Replace('\', '/').Split('/').Count -le 2) } | Select-Object -First 1
        $marker = $zip.Entries | Where-Object { $_.Name -eq 'portable.marker' -and ($_.FullName.Replace('\', '/').Split('/').Count -le 2) } | Select-Object -First 1
        if (-not $buildInfo -or -not $marker) { throw 'The portable ZIP has no BUILD_INFO.json and portable.marker. Build it with build/package.ps1.' }
        $reader = [IO.StreamReader]::new($buildInfo.Open())
        try { $portableVersion = ($reader.ReadToEnd() | ConvertFrom-Json).Version } finally { $reader.Dispose() }
    } finally { $zip.Dispose() }
    if ($portableVersion -ne $releaseVersion) { throw "The portable ZIP is version '$portableVersion', not $releaseVersion." }
    $manifest.portable = Add-ReleaseAsset $Portable "KeaClip-$releaseVersion-win-x64.zip" '.zip'
}

$manifestPath = Join-Path $releaseOutput 'keaclip-update.json'
[IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 4) + "`n", [Text.UTF8Encoding]::new($false))
$sums = @($manifest.installer, $manifest.portable) | Where-Object { $_ } | ForEach-Object { '{0}  {1}' -f $_.sha256, [Uri]::UnescapeDataString(([Uri]$_.url).Segments[-1]) }
[IO.File]::WriteAllText((Join-Path $releaseOutput 'SHA256SUMS.txt'), (($sums -join "`n") + "`n"), [Text.UTF8Encoding]::new($false))
Write-Output "Release feed: $manifestPath"
Write-Output "Upload to the GitHub release $releaseTag (not a draft or pre-release): keaclip-update.json, SHA256SUMS.txt and the listed assets."
