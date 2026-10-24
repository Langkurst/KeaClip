<#
.SYNOPSIS
One command from a committed version bump to a live, verified release on GitHub, the website and this PC.

.DESCRIPTION
Builds once, then runs both test suites while the public and personal packages and installers are made in parallel.
Nothing is published unless every test and the installer self-test pass. Release notes come from the top CHANGELOG
section. The third-party source archive is reused from the previous release when its inputs are unchanged.
Finally: public source export, GitHub release (with a digest check of every uploaded file), website, local update feed,
and a silent install of the personal build on this PC.

.EXAMPLE
pwsh -File build/ship.ps1
#>
param([switch]$NoPublish)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
Set-Location -LiteralPath $root
$clock = [Diagnostics.Stopwatch]::StartNew()
function Step($text) { Write-Output ("[{0:mm\:ss}] {1}" -f $clock.Elapsed, $text) }

$version = ([xml](Get-Content Directory.Build.props -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (git status --porcelain) { throw 'Commit everything first: ship builds exactly what is committed.' }
$changelog = Get-Content CHANGELOG.md -Raw
if ($changelog -notmatch "(?ms)^## $([regex]::Escape($version)) [^\n]*\n(.*?)(?=^## |\z)") { throw "CHANGELOG.md has no '## $version' section." }
$highlights = $Matches[1].Trim()
if (gh release view "v$version" --repo Langkurst/KeaClip 2>$null) { throw "v$version is already released." }
Step "Shipping KeaClip $version"

# 1. Restore and build once.
dotnet restore Klippi.sln --locked-mode --disable-parallel | Out-Null; if ($LASTEXITCODE) { throw 'Restore failed.' }
dotnet build Klippi.sln -c Release --no-restore -m:1 -p:BuildInParallel=false -v q | Out-Null; if ($LASTEXITCODE) { throw 'Build failed.' }
Step 'Built'

# 2. Tests in the background while packaging runs here (they use separate output folders).
$logs = Join-Path $root 'artifacts\ship-logs'; New-Item -ItemType Directory -Force $logs | Out-Null
$tests = Start-Process pwsh -ArgumentList '-NoProfile', '-Command', "Set-Location '$root'; dotnet test tests/Klippi.Tests/Klippi.Tests.csproj -c Release --no-build --no-restore --logger 'console;verbosity=minimal' *> '$logs\core.txt'; `$c = `$LASTEXITCODE; dotnet test tests/Klippi.UiTests/Klippi.UiTests.csproj -c Release --no-build --no-restore --logger 'console;verbosity=minimal' *> '$logs\ui.txt'; exit (`$c -bor `$LASTEXITCODE)" -NoNewWindow -PassThru
try { $tests.PriorityClass = 'BelowNormal' } catch {}

$release = Join-Path $root "artifacts\release-$version"
if (Test-Path $release) { Remove-Item -Recurse -LiteralPath $release }
New-Item -ItemType Directory $release | Out-Null
$out = & ./build/package.ps1
$zip = ($out | Select-String 'Portable archive: (.*)').Matches[0].Groups[1].Value
$folder = ($out | Select-String 'Portable folder: (.*)').Matches[0].Groups[1].Value
& ./build/installer-publish.ps1 -Payload $zip -SelfTest *> "$logs\installer.txt"; if ($LASTEXITCODE) { throw "Installer self-test failed: see $logs\installer.txt" }
Copy-Item artifacts\installer\KeaClip.Setup.exe "$release\KeaClip-$version-Setup.exe"
Copy-Item $zip "$release\KeaClip-$version-win-x64.zip"
Copy-Item (Join-Path $folder "KeaClip-$version-source.zip") $release
Step 'Public package and installer ready (self-test passed)'

# The VLC/LibVLCSharp source archive only changes when those libraries do; reuse the last one, renamed.
$previous = Get-ChildItem artifacts -Directory -Filter 'release-*' | Where-Object { $_.FullName -ne $release } | Sort-Object LastWriteTime -Descending |
    ForEach-Object { Get-ChildItem $_.FullName -Filter '*-third-party-source.zip' } | Select-Object -First 1
if ($previous) { Copy-Item $previous.FullName "$release\KeaClip-$version-third-party-source.zip" } else { & ./build/third-party-source.ps1 -OutputDirectory $release | Out-Null }

$personal = & ./build/package.ps1 -IncludeComponents -Edition Personal
$personalZip = ($personal | Select-String 'Portable archive: (.*)').Matches[0].Groups[1].Value
& ./build/installer-publish.ps1 -Payload $personalZip *> "$logs\installer-personal.txt"; if ($LASTEXITCODE) { throw 'Personal installer build failed.' }
$personalSetup = Join-Path $release "personal-Setup.exe"; Copy-Item artifacts\installer\KeaClip.Setup.exe $personalSetup
Step 'Personal package ready'

$tests.WaitForExit()
if ($tests.ExitCode -ne 0) { Get-Content "$logs\core.txt", "$logs\ui.txt" | Select-String 'Failed |\[FAIL\]|Passed!|Failed!' | Select-Object -Last 15; throw 'Tests failed: nothing was published.' }
Get-Content "$logs\core.txt", "$logs\ui.txt" | Select-String 'Passed!' | ForEach-Object { $_.Line.Trim() }
Step 'All tests passed'

$notes = (($highlights -split "`n" | ForEach-Object { $_.TrimEnd() }) -join "`n")
& ./build/release-manifest.ps1 -Installer "$release\KeaClip-$version-Setup.exe" -Portable "$release\KeaClip-$version-win-x64.zip" -Notes "KeaClip $version" -OutputDirectory $release | Out-Null
if ($NoPublish) { Step 'Built and tested; not published (-NoPublish).'; return }

# 3. Publish and verify.
./build/export-public.ps1 -Push *> "$logs\export.txt"; if ($LASTEXITCODE) { throw 'Public export failed.' }
$body = @"
$notes

**Already have KeaClip?** It offers this update by itself, and after updating a card shows what's new.

**New here?** Download ``KeaClip-$version-Setup.exe`` below and run it. Windows may say "Windows protected your PC" because KeaClip isn't code-signed yet: click **More info**, then **Run anyway**. Prefer no installer? Use ``KeaClip-$version-win-x64.zip``, unzip it and run ``KeaClip.exe``.

Checksums are in ``SHA256SUMS.txt``. Found a problem? Use **Feedback** in the app, open an issue, or email keaclip@proton.me.

[Code signing policy](https://langkurst.github.io/KeaClip/code-signing.html): releases are unsigned for now; KeaClip will reapply for free signing from SignPath.io (certificate by SignPath Foundation) once it's better known.
"@
$notesFile = Join-Path $logs 'notes.md'; Set-Content -LiteralPath $notesFile -Value $body -Encoding utf8NoBOM
$assets = 'Setup.exe', 'win-x64.zip', 'source.zip', 'third-party-source.zip' | ForEach-Object { "$release\KeaClip-$version-$_" }
gh release create "v$version" --repo Langkurst/KeaClip --target main --title "KeaClip $version" --notes-file $notesFile --latest @assets "$release\keaclip-update.json" "$release\SHA256SUMS.txt" | Out-Null
if ($LASTEXITCODE) { throw 'Creating the GitHub release failed.' }
$bad = gh api "repos/Langkurst/KeaClip/releases/tags/v$version" --jq '.assets[] | .name + " " + .digest' | Where-Object {
    $name, $digest = $_.Split(' '); "sha256:" + (Get-FileHash "$release\$name").Hash.ToLower() -ne $digest }
if ($bad) { throw "Uploaded files differ: $bad" }
$live = (Invoke-RestMethod https://github.com/Langkurst/KeaClip/releases/latest/download/keaclip-update.json).version
if ($live -ne $version) { throw "The live update feed says $live, not $version." }
./build/publish-site.ps1 -Push *> "$logs\site.txt"
Step "Published: GitHub release verified (6 files), feed says $live, website updated"

# 4. This PC: local feed for the personal edition, then install it.
& ./build/release-local.ps1 -Installer $personalSetup -Notes "KeaClip $version" *> "$logs\local.txt"
Get-Process KeaClip -ErrorAction SilentlyContinue | Stop-Process; Start-Sleep -Seconds 2
$install = Start-Process $personalSetup -ArgumentList '--silent' -PassThru -Wait
if ($install.ExitCode -ne 0) { throw "Installing on this PC failed ($($install.ExitCode))." }
$installed = (Get-Item "$env:LOCALAPPDATA\Programs\KeaClip\KeaClip.exe").VersionInfo.ProductVersion
Start-Process "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\KeaClip\KeaClip.lnk"
Step "This PC now runs $installed. Done."
