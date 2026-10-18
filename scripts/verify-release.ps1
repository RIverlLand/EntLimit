param([Parameter(Mandatory=$true)][string]$PayloadDirectory)
$ErrorActionPreference='Stop'
$payload=[IO.Path]::GetFullPath($PayloadDirectory)
$required=@('EntLimit.Desktop.exe','EntLimit.Monitor.exe','EntLimit.NativeHost.exe','EntLimit.AdminBroker.exe','EntLimit.EmergencyStop.exe','installed.flag','plugins/java/EntLimit.JavaPlugin.exe','plugins/installed-plugins.json')
foreach($relative in $required){if(-not(Test-Path -LiteralPath (Join-Path $payload $relative))){throw "Required release file absent: $relative"}}
$files=@(Get-ChildItem -LiteralPath $payload -Recurse -File | Where-Object Name -ne 'release-manifest.json')
foreach($file in $files){
  $relative=$file.FullName.Substring($payload.TrimEnd('\','/').Length+1)
  if($relative -match '(^|[\\/])(data|work|\.git|\.env|node_modules|__pycache__)([\\/]|$)' -or $file.Extension -in @('.dpapi','.pdb','.log','.tmp','.bak','.ps1xml') -or $file.Name -in @('config.json','session.json','desktop-preferences.json','api-settings.json','catalog.json')){throw "Private/development file in clean release: $relative"}
  if(($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'Reparse files are forbidden in release payload'}
}
# Inventory deliberately includes only relative paths/hashes; no environment or machine identity.
$files | Sort-Object FullName | ForEach-Object {[pscustomobject]@{Path=$_.FullName.Substring($payload.TrimEnd('\','/').Length+1);Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash;Bytes=$_.Length}} | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $payload 'release-manifest.json')
Write-Output ('Clean release verified: '+$files.Count+' files; personal data/config/keys excluded.')
exit 0
