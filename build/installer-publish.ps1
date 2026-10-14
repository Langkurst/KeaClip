param([Parameter(Mandatory=$true)][string]$Payload,[string]$OutputDirectory,[switch]$SelfTest)
$ErrorActionPreference = 'Stop'
$installerWorkspace = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
$installerPayload = (Resolve-Path -LiteralPath $Payload).Path
if ([IO.Path]::GetExtension($installerPayload) -ne '.zip') { throw 'Payload must be a verified portable ZIP.' }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $installerWorkspace 'artifacts\installer' }
$installerOutput = [IO.Path]::GetFullPath($OutputDirectory)
if (!$installerOutput.StartsWith($installerWorkspace + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Installer output must stay within this workspace.' }
Push-Location $installerWorkspace
try {
    $installerRevision = (git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or (git status --porcelain)) { throw 'Commit the reviewed source before publishing the installer.' }
    New-Item -ItemType Directory -Path $installerOutput -Force | Out-Null
    dotnet publish src/Klippi.Installer/Klippi.Installer.csproj -c Release -r win-x64 --self-contained true -o $installerOutput -m:1 -p:UseSharedCompilation=false -p:BuildInParallel=false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:SatelliteResourceLanguages=en -p:RuntimeFrameworkVersion=10.0.12 -p:RestoreLockedMode=true "-p:PayloadZip=$installerPayload"
    if ($LASTEXITCODE -ne 0) { throw 'Installer publish failed.' }
    if ((git rev-parse HEAD).Trim() -ne $installerRevision -or (git status --porcelain)) { throw 'Source changed during installer publishing; verify and checkpoint before retrying.' }
    $installerExe = Join-Path $installerOutput 'KeaClip.Setup.exe'
    Get-FileHash -Algorithm SHA256 -LiteralPath $installerExe | Format-List
    if ($SelfTest) {
        $installerTestRoot = Join-Path $installerWorkspace ('artifacts\installer-tests\' + [Guid]::NewGuid().ToString('N'))
        $installerProcess = Start-Process -FilePath $installerExe -ArgumentList @('--self-test', ('"' + $installerTestRoot + '"')) -WindowStyle Hidden -PassThru -Wait
        if ($installerProcess.ExitCode -ne 0) { throw "Installer self-test failed. Evidence is in $installerTestRoot" }
        $installerReports = @(Get-ChildItem -LiteralPath $installerTestRoot -Filter 'self-test-result.json' -Recurse)
        if ($installerReports.Count -ne 1) { throw 'Installer verification did not return one complete report.' }
        $installerReport = Get-Content -LiteralPath $installerReports[0].FullName -Raw | ConvertFrom-Json
        if ($installerReport.Success -ne $true -or $installerReport.EmbeddedPayloadTested -ne $true -or $installerReport.CopyWorkerTest -notlike 'Passed:*' -or $installerReport.LinkCheck -notlike 'Passed:*' -or $installerReport.RegistryAndShortcutsTouched -ne $false -or $installerReport.VisibleWindowsOpened -ne $false) { throw 'Installer verification skipped or failed a required exact-payload, worker, link or hidden-operation check.' }
        Get-Content -LiteralPath $installerReports[0].FullName
    }
    Write-Output "Private installer: $installerExe"
} finally { Pop-Location }
