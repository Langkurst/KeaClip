param(
  # The public origin of the R2 bucket that holds the encrypted objects, for example https://media.example.com (no path).
  [string]$MediaOrigin,
  # Instead of a public bucket: serve the objects from the page's own origin through the Pages Function in
  # web/watch/functions, with the bucket bound as MEDIA. No custom domain or CORS rule is needed.
  [switch]$SameOrigin,
  [string]$Bucket = 'keaclip-watch',
  [string]$ProjectName = 'keaclip-watch',
  [string]$OutDir
)
# Builds the deployable KeaClip watch site (Cloudflare Pages) from web/watch/site: writes the media origin into
# assets/config.js and the Content-Security-Policy in _headers, adds the licence, and records the source commit.
# With -SameOrigin the output is a Pages project folder (site\, functions\, wrangler.toml): deploy it with
# "npx wrangler pages deploy" run inside that folder. It does not deploy anything; see docs/WATCH_LINKS.md.
$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
$source = Join-Path $workspace 'web\watch\site'
if (-not $OutDir) { $OutDir = Join-Path $workspace 'artifacts\watch-site' }
$OutDir = [IO.Path]::GetFullPath($OutDir)
$artifacts = Join-Path $workspace 'artifacts'
if (-not $OutDir.StartsWith($artifacts + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'The output folder must be inside artifacts\ so a rebuild can safely replace it.' }

if ($SameOrigin) {
  if ($MediaOrigin) { throw 'Give -MediaOrigin or -SameOrigin, not both.' }
  if ($Bucket -notmatch '^[a-z0-9][a-z0-9-]{1,61}[a-z0-9]$' -or $ProjectName -notmatch '^[a-z0-9][a-z0-9-]{0,56}[a-z0-9]$') { throw 'Bucket and project names are lower-case letters, digits and hyphens.' }
  # config.js: an empty base means /w/<id>/... on the page's own origin; the CSP keeps connect-src 'self' only.
  $origin = ''; $cspOrigin = ''
} else {
  if (-not $MediaOrigin) { throw 'Give -MediaOrigin https://media.example.com, or -SameOrigin.' }
  $uri = $null
  if (-not [Uri]::TryCreate($MediaOrigin, [UriKind]::Absolute, [ref]$uri)) { throw 'MediaOrigin must be an absolute URL such as https://media.example.com.' }
  $local = $uri.Scheme -eq 'http' -and $uri.IsLoopback
  if (($uri.Scheme -ne 'https' -and -not $local) -or $uri.AbsolutePath -ne '/' -or $uri.Query -or $uri.Fragment -or $uri.UserInfo) {
    throw 'MediaOrigin must be https://host (no path, query or user name); http is accepted only for 127.0.0.1/localhost tests.'
  }
  $origin = $uri.GetLeftPart([UriPartial]::Authority); $cspOrigin = ' ' + $origin
}

if (Test-Path -LiteralPath $OutDir) { Remove-Item -LiteralPath $OutDir -Recurse -Force }
$siteDir = if ($SameOrigin) { Join-Path $OutDir 'site' } else { $OutDir }
New-Item -ItemType Directory -Path $siteDir -Force | Out-Null
Copy-Item -Path (Join-Path $source '*') -Destination $siteDir -Recurse
foreach ($file in @('assets\config.js', '_headers')) {
  $path = Join-Path $siteDir $file
  $text = [IO.File]::ReadAllText($path).Replace(' __MEDIA_ORIGIN__', $cspOrigin).Replace('__MEDIA_ORIGIN__', $origin)
  [IO.File]::WriteAllText($path, $text, [Text.UTF8Encoding]::new($false))
}
$left = Get-ChildItem -LiteralPath $siteDir -Recurse -File | Select-String -SimpleMatch '__MEDIA_ORIGIN__' -List
if ($left) { throw "A placeholder was not replaced: $($left.Path)" }
Copy-Item -LiteralPath (Join-Path $workspace 'LICENSE') -Destination (Join-Path $siteDir 'LICENSE.txt')
if ($SameOrigin) {
  Copy-Item -LiteralPath (Join-Path $workspace 'web\watch\functions') -Destination (Join-Path $OutDir 'functions') -Recurse
  # Pages reads its bindings from wrangler.toml beside the functions folder.
  $toml = "name = `"$ProjectName`"`npages_build_output_dir = `"site`"`ncompatibility_date = `"2026-09-01`"`n`n[[r2_buckets]]`nbinding = `"MEDIA`"`nbucket_name = `"$Bucket`"`n"
  [IO.File]::WriteAllText((Join-Path $OutDir 'wrangler.toml'), $toml, [Text.UTF8Encoding]::new($false))
}
Push-Location $workspace
try { $commit = (git rev-parse HEAD).Trim(); $dirty = [bool](git status --porcelain -- web/watch/site web/watch/functions) } finally { Pop-Location }
[ordered]@{ SourceCommit = $commit; SiteSourceChanged = $dirty; MediaOrigin = $(if ($SameOrigin) { 'same origin (Pages Function, bucket ' + $Bucket + ')' } else { $origin }); BuiltAtUtc = [DateTimeOffset]::UtcNow.ToString('o'); Licence = 'GPL-3.0-or-later' } |
  ConvertTo-Json | Set-Content -LiteralPath (Join-Path $siteDir 'build-info.json') -Encoding utf8
Write-Host "Watch site ready: $OutDir"
if ($SameOrigin) { Write-Host "Deploy: cd that folder, then npx wrangler pages deploy --branch main" } else { Write-Host 'Deploy (after reading docs/WATCH_LINKS.md): npx wrangler pages deploy "<that folder>" --project-name keaclip-watch' }
