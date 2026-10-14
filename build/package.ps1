param([ValidateSet('win-x64')][string]$Runtime='win-x64',[string]$ManagedComponents,[switch]$IncludeComponents,[ValidateSet('Public','Personal')][string]$Edition='Public',[switch]$Beta)
$ErrorActionPreference='Stop'
$packageWorkspace=(Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
$packageRoot=Join-Path $packageWorkspace 'artifacts\packages'
$packageId=Get-Date -Format 'yyyyMMdd-HHmmss'
$packageVersion=([xml](Get-Content -LiteralPath (Join-Path $packageWorkspace 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if($packageVersion -notmatch '^\d+\.\d+\.\d+$'){throw 'Directory.Build.props has no usable Version.'}
$packageOutput=Join-Path $packageRoot ("KeaClip-$packageVersion-$Runtime-$packageId" + $(if($Edition -eq 'Personal'){'-personal'}else{''}) + $(if($Beta){'-beta'}else{''}))
if(-not $packageOutput.StartsWith($packageWorkspace+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe package output.'}
Push-Location $packageWorkspace
try {
  $packageRevision=(git rev-parse HEAD).Trim()
  if($LASTEXITCODE -ne 0 -or (git status --porcelain)){throw 'Commit the reviewed source before packaging.'}
  New-Item -ItemType Directory -Path $packageOutput -Force | Out-Null
  dotnet publish src/Klippi.Desktop/Klippi.Desktop.csproj -c Release -r $Runtime --self-contained true -o $packageOutput -m:1 -p:BuildInParallel=false -p:PublishSingleFile=false -p:RuntimeFrameworkVersion=10.0.12 -p:RestoreLockedMode=true "-p:KeaClipEdition=$Edition" "-p:KeaClipBeta=$(if($Beta){'true'}else{'false'})"
  if($LASTEXITCODE -ne 0){throw 'Publish failed.'}
  if((git rev-parse HEAD).Trim() -ne $packageRevision -or (git status --porcelain)){throw 'Source changed during publishing; verify and checkpoint before retrying.'}
  Set-Content -LiteralPath (Join-Path $packageOutput 'portable.marker') -Value 'Settings stay in Data beside this app.' -Encoding utf8
  Copy-Item -LiteralPath 'docs\USER_GUIDE.md','docs\KNOWN_LIMITATIONS.md','docs\THIRD_PARTY_NOTICES.md','CHANGELOG.md' -Destination $packageOutput
  Copy-Item -LiteralPath 'docs\vendor-notices' -Destination (Join-Path $packageOutput 'Licenses') -Recurse
  Copy-Item -LiteralPath 'LICENSE' -Destination (Join-Path $packageOutput 'LICENSE.txt')
  Copy-Item -LiteralPath 'docs\SOURCE.md' -Destination (Join-Path $packageOutput 'SOURCE.md')
  if($Beta){
    # A beta copy is the owner's own pre-release handed to a tester: KeaClip's source follows with the public
    # release; the bundled third-party components' notices and source offer are included as usual.
    Set-Content -LiteralPath (Join-Path $packageOutput 'BETA.txt') -Encoding utf8 -Value "KeaClip $packageVersion beta: a pre-release test copy. Please don't pass it on. KeaClip's source code is published with the first public release at https://github.com/Langkurst/KeaClip. Third-party licences and the source offer for bundled components are in SOURCE.md and Licenses."
  } else {
    # GPL corresponding source for KeaClip itself: the exact committed revision, without the private working
    # notes listed in build/public-exclude.txt (the same tree as the public repository).
    # The public repository itself has no list: nothing private is in it.
    $publicExclude=@(if(Test-Path -LiteralPath 'build\public-exclude.txt'){Get-Content -LiteralPath 'build\public-exclude.txt'}) | Where-Object { $_.Trim() -and -not $_.StartsWith('#') } | ForEach-Object { ':(exclude)' + $_.Trim() }
    git archive --format=zip --prefix="KeaClip-$packageVersion-source/" -o (Join-Path $packageOutput "KeaClip-$packageVersion-source.zip") $packageRevision -- . @publicExclude
    if($LASTEXITCODE -ne 0){throw 'Source archive failed.'}
  }
  [ordered]@{Version=$packageVersion;Edition=$Edition;Channel=$(if($Beta){'Beta'}else{'Release'});SourceCommit=$packageRevision;WorkingTreeChanged=$false;Runtime=$Runtime;DotnetRuntime='10.0.12';PackagedAtUtc=[DateTimeOffset]::UtcNow.ToString('o');Distribution=$(if($Beta){'GPL-3.0-or-later; pre-release test copy, KeaClip source published with the public release (see BETA.txt)'}else{'GPL-3.0-or-later; source included (see SOURCE.md)'})} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $packageOutput 'BUILD_INFO.json') -Encoding utf8
  if($IncludeComponents){
    if(-not $ManagedComponents){$ManagedComponents=Join-Path $packageWorkspace 'tools\runtime'}
    $manifestPath=Join-Path $ManagedComponents 'current.json'
    $manifest=Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
    $componentOutput=Join-Path $packageOutput 'Components'; New-Item -ItemType Directory -Path $componentOutput -Force | Out-Null
    foreach($entry in $manifest.Values){
      $componentSource=[IO.Path]::GetFullPath((Join-Path $ManagedComponents $entry.RelativePath))
      $componentManagedRoot=[IO.Path]::GetFullPath($ManagedComponents)
      if(-not $componentSource.StartsWith($componentManagedRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe component source.'}
      if((Get-FileHash -Algorithm SHA256 -LiteralPath $componentSource).Hash -ne $entry.Sha256){throw 'Component integrity mismatch.'}
      $componentTarget=Join-Path $componentOutput $entry.RelativePath
      New-Item -ItemType Directory -Path (Split-Path -Parent $componentTarget) -Force | Out-Null
      Copy-Item -LiteralPath $componentSource -Destination $componentTarget
    }
    Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $componentOutput 'current.json')
    Set-Content -LiteralPath (Join-Path $packageOutput 'PRIVATE_BUILD.txt') -Value 'Personal build of KeaClip (GPL-3.0-or-later). KeaClip source is included; see SOURCE.md for the bundled components, their licences and the written source offer. Not legal advice.'
  }
  $hashLines=Get-ChildItem -LiteralPath $packageOutput -File -Recurse | Sort-Object FullName | ForEach-Object { $fileRelative=[IO.Path]::GetRelativePath($packageOutput,$_.FullName).Replace('\','/'); '{0}  {1}' -f (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant(),$fileRelative }
  # LF line endings: the standard "sha256sum -c" format (CRLF makes it look for "name`r").
  [IO.File]::WriteAllText((Join-Path $packageOutput 'checksums.sha256'), (($hashLines -join "`n") + "`n"), [Text.UTF8Encoding]::new($false))
  if((git rev-parse HEAD).Trim() -ne $packageRevision -or (git status --porcelain)){throw 'Source changed during packaging; verify and checkpoint before retrying.'}
  $zipPath=$packageOutput+'.zip'
  Compress-Archive -LiteralPath $packageOutput -DestinationPath $zipPath -CompressionLevel Fastest
  Write-Output "Portable folder: $packageOutput"
  Write-Output "Portable archive: $zipPath"
} finally {Pop-Location}
