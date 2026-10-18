param(
    [string]$DotnetPath = '',
    [string]$IsccPath = '',
    [switch]$KeepStaging
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
function Remove-InstallerStaging([string]$directory) {
    $resolved=[IO.Path]::GetFullPath($directory)
    $workRoot=[IO.Path]::GetFullPath((Join-Path $repo 'work')).TrimEnd('\','/')
    if(-not [string]::Equals([IO.Path]::GetDirectoryName($resolved),$workRoot,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notmatch '^(package|emergency)-[0-9a-f]{32}$'){
        throw 'Refusing to clean a path outside generated installer staging.'
    }
    if(-not(Test-Path -LiteralPath $resolved)){return}
    if(((Get-Item -LiteralPath $resolved).Attributes -band [IO.FileAttributes]::ReparsePoint) -or @(Get-ChildItem -LiteralPath $resolved -Force -Recurse -Attributes ReparsePoint).Count){
        throw 'Refusing to clean linked staging paths.'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
# Optional machine-local paths are build tools only, never application/API settings.
$localToolsPath = Join-Path $repo 'work/build-tools.local.json'
$localTools = if(Test-Path -LiteralPath $localToolsPath){Get-Content -LiteralPath $localToolsPath -Raw | ConvertFrom-Json}else{$null}
function Resolve-BuildTool([string]$requested,[string[]]$candidates,[string]$description) {
    foreach($candidate in @($requested)+$candidates) {
        if([string]::IsNullOrWhiteSpace($candidate)){continue}
        if(Test-Path -LiteralPath $candidate -PathType Leaf){return [IO.Path]::GetFullPath($candidate)}
        $command=Get-Command $candidate -CommandType Application -ErrorAction SilentlyContinue
        if($command){return $command.Source}
    }
    throw "$description not found. Install .NET 10 SDK / Inno Setup 6+, or supply -DotnetPath and -IsccPath."
}
$DotnetPath=Resolve-BuildTool $DotnetPath @($localTools.DotnetPath,(Join-Path $repo 'work/tools/dotnet/dotnet.exe'),(Join-Path $env:ProgramFiles 'dotnet/dotnet.exe'),'dotnet') '.NET SDK'
$IsccPath=Resolve-BuildTool $IsccPath @('ISCC.exe',$localTools.IsccPath,(Join-Path $repo 'work/tools/inno/compiler/ISCC.exe'),(Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe'),(Join-Path $env:ProgramFiles 'Inno Setup 7/ISCC.exe')) 'Inno Setup compiler'
$sdks=& $DotnetPath --list-sdks
if($LASTEXITCODE -ne 0 -or -not($sdks -match '^10\.')){throw '.NET 10 SDK is required to build EntLimit.'}
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE='false'
$env:DOTNET_CLI_HOME=Join-Path $repo 'work/cli-home'
$buildTemp=Join-Path $repo 'work/temp'
New-Item -ItemType Directory -Force -Path $buildTemp | Out-Null
$env:TEMP=$buildTemp
$env:TMP=$buildTemp
if([string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES) -and $localTools.NugetPackages){$env:NUGET_PACKAGES=[string]$localTools.NugetPackages}
Set-Location -LiteralPath $repo
# A new staging directory on EVERY run; never package the user's live artifacts/app.
$payload = Join-Path $repo ('work/package-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $payload | Out-Null
foreach ($name in @('Monitor','Desktop','NativeHost','AdminBroker')) {
    & $DotnetPath publish --disable-build-servers -m:1 "src/EntLimit.$name/EntLimit.$name.csproj" -c Release -r win-x64 --self-contained true -o $payload --configfile Publish.NuGet.Config -p:NuGetAudit=false -p:RestoreDisableParallel=true -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $name" }
}
$escape = Join-Path $repo ('work/emergency-' + [Guid]::NewGuid().ToString('N'))
& $DotnetPath publish --disable-build-servers -m:1 src/EntLimit.EmergencyStop/EntLimit.EmergencyStop.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o $escape --configfile Publish.NuGet.Config -p:NuGetAudit=false -p:RestoreDisableParallel=true
if ($LASTEXITCODE -ne 0) { throw 'Emergency executable publish failed' }
Copy-Item -LiteralPath (Join-Path $escape 'EntLimit.EmergencyStop.exe') -Destination $payload
$java = Join-Path $payload 'plugins/java'
& $DotnetPath publish --disable-build-servers -m:1 src/EntLimit.JavaPlugin/EntLimit.JavaPlugin.csproj -c Release -r win-x64 --self-contained true -p:DebugType=None -p:DebugSymbols=false -o $java --configfile Publish.NuGet.Config -p:NuGetAudit=false -p:RestoreDisableParallel=true
if ($LASTEXITCODE -ne 0) { throw 'Java plugin publish failed' }
# Plugin publishing helper generates hashes from THIS clean build, not local trust/config files.
& (Join-Path $repo 'scripts/package-java-plugin.ps1') -AppDirectory $payload
if ($LASTEXITCODE -ne 0) { throw 'Java plugin manifest failed' }
& (Join-Path $repo 'scripts/copy-runtime-notices.ps1') -AppDirectory $payload -DotnetPath $DotnetPath
Copy-Item -LiteralPath (Join-Path $repo 'installer/installed.flag') -Destination $payload
Copy-Item -LiteralPath (Join-Path $repo 'scripts/emergency-stop.ps1'),(Join-Path $repo 'scripts/EmergencyStop.cmd') -Destination $payload
Copy-Item -LiteralPath (Join-Path $repo 'README.md') -Destination $payload
Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination $payload
Copy-Item -LiteralPath (Join-Path $repo 'browser-extension') -Destination (Join-Path $payload 'browser-extension') -Recurse
$documentation=Join-Path $payload 'docs'
New-Item -ItemType Directory -Path $documentation | Out-Null
foreach ($name in @('DISTRIBUTION.md','PLUGINS.md','CONTENT-PACKS.md','ONLINE.md')) {
    $source=Join-Path $repo ('docs/'+$name)
    if(Test-Path -LiteralPath $source){Copy-Item -LiteralPath $source -Destination $documentation}
}
& (Join-Path $repo 'scripts/verify-release.ps1') -PayloadDirectory $payload
if ($LASTEXITCODE -ne 0) { throw 'Clean payload verification failed' }
$release = Join-Path $repo 'artifacts/installer'
New-Item -ItemType Directory -Force -Path $release | Out-Null
& $IsccPath ('/DPayloadDir='+$payload) ('/DReleaseDir='+$release) (Join-Path $repo 'installer/EntLimit.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compiler failed' }
$installer=Join-Path $release 'EntLimit-Setup-0.3.1-win-x64.exe'
Get-FileHash -LiteralPath $installer -Algorithm SHA256 | ForEach-Object { $_.Hash+'  '+[IO.Path]::GetFileName($_.Path) } | Set-Content -LiteralPath ($installer+'.sha256')
if($KeepStaging){$payload | Set-Content -LiteralPath (Join-Path $release 'payload-location.txt')}
else { Remove-InstallerStaging $payload; Remove-InstallerStaging $escape }
Write-Output $installer
