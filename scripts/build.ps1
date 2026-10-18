param([string]$DotnetPath = 'dotnet', [switch]$FrameworkDependent)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location -LiteralPath $repo
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_CLI_HOME = Join-Path $repo 'work/cli-home'
if ([string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) { $env:NUGET_PACKAGES = Join-Path $repo 'work/nuget-packages' }
$sdkList = & $DotnetPath --list-sdks
if ($LASTEXITCODE -ne 0 -or -not ($sdkList -match '^10\.')) { throw '需要 .NET 10 SDK；可用 -DotnetPath 指定 dotnet.exe。' }
$projects = @('Core', 'Online', 'Runner', 'JavaPlugin', 'Monitor', 'Desktop', 'NativeHost', 'AdminBroker', 'EmergencyStop')
foreach ($name in $projects) {
    & $DotnetPath build -m:1 "src/EntLimit.$name/EntLimit.$name.csproj" -c Release --configfile (Join-Path $repo 'NuGet.Config')
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $name" }
}
& $DotnetPath build -m:1 'tests/EntLimit.Core.Tests/EntLimit.Core.Tests.csproj' -c Release --configfile (Join-Path $repo 'NuGet.Config')
if ($LASTEXITCODE -ne 0) { throw 'Core test build failed' }
& $DotnetPath run --project 'tests/EntLimit.Core.Tests/EntLimit.Core.Tests.csproj' -c Release --no-build
if ($LASTEXITCODE -ne 0) { throw 'Core tests failed' }
& $DotnetPath build -m:1 'tests/EntLimit.Scan.Tests/EntLimit.Scan.Tests.csproj' -c Release --configfile (Join-Path $repo 'NuGet.Config')
if ($LASTEXITCODE -ne 0) { throw 'Scan test build failed' }
& $DotnetPath run --project 'tests/EntLimit.Scan.Tests/EntLimit.Scan.Tests.csproj' -c Release --no-build
if ($LASTEXITCODE -ne 0) { throw 'Scan policy tests failed' }
& $DotnetPath build -m:1 'tests/EntLimit.Process.Tests/EntLimit.Process.Tests.csproj' -c Release --configfile (Join-Path $repo 'NuGet.Config')
if ($LASTEXITCODE -ne 0) { throw 'Process test build failed' }
& $DotnetPath run --project 'tests/EntLimit.Process.Tests/EntLimit.Process.Tests.csproj' -c Release --no-build
if ($LASTEXITCODE -ne 0) { throw 'Application process matching tests failed' }
foreach ($suite in @('Exercise', 'Runner', 'Analysis', 'Content', 'Online', 'Guard', 'Recovery')) {
    & $DotnetPath build -m:1 "tests/EntLimit.$suite.Tests/EntLimit.$suite.Tests.csproj" -c Release --configfile (Join-Path $repo 'NuGet.Config')
    if ($LASTEXITCODE -ne 0) { throw "$suite test build failed" }
    if ($suite -notin @('Guard', 'Recovery')) {
        & $DotnetPath run --project "tests/EntLimit.$suite.Tests/EntLimit.$suite.Tests.csproj" -c Release --no-build
        if ($LASTEXITCODE -ne 0) { throw "$suite tests failed" }
    }
}
if (Get-Command node -ErrorAction SilentlyContinue) {
    & node 'browser-extension/verify.mjs'
    if ($LASTEXITCODE -ne 0) { throw 'Extension tests failed' }
}
$destination = Join-Path $repo 'artifacts/app'
foreach ($name in @('Monitor', 'Desktop', 'NativeHost', 'AdminBroker', 'EmergencyStop')) {
    if ($FrameworkDependent) {
        & $DotnetPath publish -m:1 "src/EntLimit.$name/EntLimit.$name.csproj" -c Release --self-contained false -o $destination --configfile (Join-Path $repo 'NuGet.Config')
    } else {
        & $DotnetPath publish -m:1 "src/EntLimit.$name/EntLimit.$name.csproj" -c Release -r win-x64 --self-contained true -o $destination --configfile (Join-Path $repo 'Publish.NuGet.Config') -p:NuGetAudit=false
    }
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $name" }
}
$javaDestination = Join-Path $destination 'plugins/java'
& $DotnetPath publish -m:1 'src/EntLimit.JavaPlugin/EntLimit.JavaPlugin.csproj' -c Release -r win-x64 --self-contained true -o $javaDestination --configfile (Join-Path $repo 'Publish.NuGet.Config') -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { throw 'Java plugin publish failed' }
& (Join-Path $repo 'scripts/package-java-plugin.ps1') -AppDirectory $destination
& (Join-Path $repo 'scripts/copy-runtime-notices.ps1') -AppDirectory $destination -DotnetPath $DotnetPath
& $DotnetPath build -m:1 'tests/EntLimit.Plugin.Tests/EntLimit.Plugin.Tests.csproj' -c Release --configfile (Join-Path $repo 'NuGet.Config')
if ($LASTEXITCODE -ne 0) { throw 'Plugin test build failed' }
if ($env:ENT_LIMIT_TEST_JDK) {
    & $DotnetPath run --project 'tests/EntLimit.Plugin.Tests/EntLimit.Plugin.Tests.csproj' -c Release --no-build -- $javaDestination $env:ENT_LIMIT_TEST_JDK
    if ($LASTEXITCODE -ne 0) { throw 'Java plugin tests failed' }
} else { Write-Host 'Java runtime checks skipped: set ENT_LIMIT_TEST_JDK to an existing JDK directory.' }
Copy-Item -LiteralPath (Join-Path $repo 'scripts/emergency-stop.ps1') -Destination $destination
Copy-Item -LiteralPath (Join-Path $repo 'scripts/EmergencyStop.cmd') -Destination $destination
Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination $destination
$desktopTest = Join-Path $repo 'work/desktop-test-published'
& $DotnetPath publish -m:1 'tests/EntLimit.Desktop.Tests/EntLimit.Desktop.Tests.csproj' -c Release -r win-x64 --self-contained true -o $desktopTest --configfile (Join-Path $repo 'Publish.NuGet.Config') -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { throw 'Desktop check publish failed' }
& (Join-Path $desktopTest 'EntLimit.Desktop.Tests.exe')
if ($LASTEXITCODE -ne 0) { throw 'Offline Desktop checks failed' }
$guardData = Join-Path $repo ('work/guard-check-' + [Guid]::NewGuid().ToString('N'))
& $DotnetPath run --project 'tests/EntLimit.Guard.Tests/EntLimit.Guard.Tests.csproj' -c Release --no-build -- $destination $guardData
if ($LASTEXITCODE -ne 0) { throw 'Guard IPC tests failed' }
$recoveryData = Join-Path $repo ('work/recovery-check-' + [Guid]::NewGuid().ToString('N'))
& $DotnetPath run --project 'tests/EntLimit.Recovery.Tests/EntLimit.Recovery.Tests.csproj' -c Release --no-build -- $destination $recoveryData (Join-Path $destination 'emergency-stop.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Recovery and login-command tests failed' }
Write-Host "完成：$destination/EntLimit.Desktop.exe"
