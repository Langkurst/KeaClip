param([switch]$SkipUi)
$ErrorActionPreference='Stop'
Push-Location (Split-Path -Parent $PSScriptRoot)
try {
  dotnet restore Klippi.sln --locked-mode --disable-parallel
  if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
  dotnet build Klippi.sln -c Release --no-restore -m:1 -p:BuildInParallel=false
  if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
  dotnet test tests/Klippi.Tests/Klippi.Tests.csproj -c Release --no-build --no-restore --logger 'console;verbosity=minimal'
  if ($LASTEXITCODE -ne 0) { throw 'Service tests failed.' }
  if (-not $SkipUi) {
    dotnet test tests/Klippi.UiTests/Klippi.UiTests.csproj -c Release --no-build --no-restore --logger 'console;verbosity=minimal'
    if ($LASTEXITCODE -ne 0) { throw 'Headless UI tests failed.' }
  }
  git diff --check
  if ($LASTEXITCODE -ne 0) { throw 'Whitespace check failed.' }
} finally { Pop-Location }
