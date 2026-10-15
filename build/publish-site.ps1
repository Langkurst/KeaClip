<#
.SYNOPSIS
Builds the KeaClip web page (web/site plus its images and fonts) and, with -Push, publishes it to the gh-pages
branch of the public repository, which GitHub Pages serves for free at https://langkurst.github.io/KeaClip/.
#>
param([switch]$Push, [string]$Remote = 'https://github.com/Langkurst/KeaClip.git')
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
$stage = Join-Path $root 'artifacts\site'
New-Item -ItemType Directory -Path (Join-Path $stage 'images'), (Join-Path $stage 'fonts') -Force | Out-Null
foreach ($page in 'index.html', 'code-signing.html') { Copy-Item -LiteralPath (Join-Path $root "web\site\$page") -Destination $stage -Force }
foreach ($image in 'kea-128.png', 'keaclip-demo.gif', 'editor-dark.png') { Copy-Item -LiteralPath (Join-Path $root "docs\images\$image") -Destination (Join-Path $stage 'images') -Force }
foreach ($font in 'BricolageGrotesque-SemiBold.ttf', 'BricolageGrotesque-Bold.ttf', 'BricolageGrotesque-OFL.txt') { Copy-Item -LiteralPath (Join-Path $root "src\Klippi.Desktop\Assets\Fonts\$font") -Destination (Join-Path $stage 'fonts') -Force }
# Served as plain files; no Jekyll processing.
Set-Content -LiteralPath (Join-Path $stage '.nojekyll') -Value '' -Encoding ascii
Write-Output "Site staged: $stage"
if (-not $Push) { return }

$author = @('-c', 'user.name=Langkurst', '-c', 'user.email=145878628+Langkurst@users.noreply.github.com')
Push-Location -LiteralPath $stage
try {
  if (-not (Test-Path -LiteralPath '.git')) { git init -q -b gh-pages; git remote add origin $Remote }
  git add -A
  git diff --cached --quiet
  if ($LASTEXITCODE -ne 0) { git @author commit -q -m 'KeaClip web page'; if ($LASTEXITCODE -ne 0) { throw 'Commit failed.' } }
  git push -u origin gh-pages
  if ($LASTEXITCODE -ne 0) { throw 'Push failed.' }
  Write-Output 'Published to the gh-pages branch.'
} finally { Pop-Location }
