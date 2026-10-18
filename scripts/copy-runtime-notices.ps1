param(
    [Parameter(Mandatory=$true)][string]$AppDirectory,
    [string]$DotnetPath='dotnet'
)
$ErrorActionPreference='Stop'
$packageRoot=$env:NUGET_PACKAGES
if([string]::IsNullOrWhiteSpace($packageRoot)) {
    $location=& $DotnetPath nuget locals global-packages --list
    if($LASTEXITCODE -ne 0){throw 'Cannot locate the NuGet package cache for runtime notices.'}
    $line=@($location | Where-Object {$_ -match '^[^:]+:\s*(.+)$'}) | Select-Object -First 1
    if(-not $line){throw 'Cannot parse the NuGet global package directory.'}
    $packageRoot=($line -replace '^[^:]+:\s*','').Trim()
}
$packages=@{}
foreach($file in Get-ChildItem -LiteralPath $AppDirectory -Filter '*.deps.json' -Recurse -File) {
    $deps=Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
    foreach($library in $deps.libraries.PSObject.Properties) {
        if($library.Name -match '^runtimepack\.(Microsoft\.[A-Za-z0-9._-]+)/([0-9][A-Za-z0-9.+-]*)$') {
            $packages[$Matches[1].ToLowerInvariant()+'/'+$Matches[2]]=$true
        }
    }
}
foreach($package in $packages.Keys) {
    $origin=Join-Path $packageRoot $package
    $notices=@(Get-ChildItem -LiteralPath $origin -File | Where-Object {$_.Name -match '^(LICENSE(\.TXT)?|THIRD-PARTY-NOTICES(\.TXT)?)$'})
    if(-not @($notices | Where-Object {$_.Name -match '^LICENSE'}).Count){throw ('Runtime license missing from package: '+$package)}
    $destination=Join-Path $AppDirectory ('licenses/runtime/'+$package)
    New-Item -ItemType Directory -Force -Path $destination | Out-Null
    foreach($notice in $notices){Copy-Item -LiteralPath $notice.FullName -Destination $destination}
}
Write-Output ('Preserved notices for '+$packages.Count+' bundled runtime packages.')
