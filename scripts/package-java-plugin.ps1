param([Parameter(Mandatory=$true)][string]$AppDirectory)
$ErrorActionPreference = 'Stop'
$appRoot = [IO.Path]::GetFullPath($AppDirectory)
$pluginRoot = Join-Path $appRoot 'plugins/java'
$executable = Join-Path $pluginRoot 'EntLimit.JavaPlugin.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'Publish EntLimit.JavaPlugin into plugins/java first.' }
$manifestPath = Join-Path $pluginRoot 'manifest.json'
$files = [ordered]@{}
foreach ($file in (Get-ChildItem -LiteralPath $pluginRoot -File -Recurse | Sort-Object FullName)) {
    if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Linked plugin files cannot be packaged.' }
    if ($file.Name -eq 'manifest.json' -or $file.Extension -eq '.pdb') { continue }
    $relative = $file.FullName.Substring($pluginRoot.TrimEnd('\').Length + 1).Replace([IO.Path]::DirectorySeparatorChar, [char]'/')
    $files[$relative] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
}
$manifest = [ordered]@{ ProtocolVersion=1; Id='java-local'; Name='Java local compiler runner'; LanguageId='java'; Executable='EntLimit.JavaPlugin.exe'; Files=$files }
$utf8 = New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 20), $utf8)
$index = @([ordered]@{ Id='java-local'; ManifestPath='java/manifest.json'; ManifestSha256=(Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash })
# Only the explicitly shipped built-in plugin is trusted; content packs cannot add executable entries.
[IO.File]::WriteAllText((Join-Path $appRoot 'plugins/installed-plugins.json'), (ConvertTo-Json -InputObject $index -Depth 10), $utf8)
Write-Host 'Packaged hash-pinned Java runner plugin. No JDK is bundled or downloaded.'
