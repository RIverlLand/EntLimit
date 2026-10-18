param([string]$ChromeExtensionId, [string]$EdgeExtensionId, [switch]$Unregister)
$ErrorActionPreference = 'Stop'
$keys = @('HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.entlimit.bridge','HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\com.entlimit.bridge')
if ($Unregister) {
    foreach ($key in $keys) { if (Test-Path -LiteralPath $key) { Remove-Item -LiteralPath $key } }
    Write-Host '已移除当前用户的 EntLimit 桥接注册；扩展仍需在浏览器中卸载。'
    return
}
$origins = @()
foreach ($id in @($ChromeExtensionId, $EdgeExtensionId)) {
    if (-not [string]::IsNullOrWhiteSpace($id)) {
        if ($id -notmatch '^[a-p]{32}$') { throw '扩展 ID 应是 32 位 a–p 字母，从浏览器扩展管理页面复制。' }
        $origins += "chrome-extension://$id/"
    }
}
if ($origins.Count -eq 0) { throw '至少提供一个 -ChromeExtensionId 或 -EdgeExtensionId。' }
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$hostExe = Join-Path $repo 'artifacts/app/EntLimit.NativeHost.exe'
if (-not (Test-Path -LiteralPath $hostExe)) { throw '请先构建 EntLimit。' }
$data = Join-Path $repo 'artifacts/app/data'
New-Item -ItemType Directory -Path $data -Force | Out-Null
$manifestPath = Join-Path $data 'native-host.json'
@{ name='com.entlimit.bridge'; description='EntLimit local learning companion'; path=$hostExe; type='stdio'; allowed_origins=@($origins | Select-Object -Unique) } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding utf8
if ($ChromeExtensionId) { New-Item -Path $keys[0] -Force | Out-Null; Set-Item -LiteralPath $keys[0] -Value $manifestPath }
if ($EdgeExtensionId) { New-Item -Path $keys[1] -Force | Out-Null; Set-Item -LiteralPath $keys[1] -Value $manifestPath }
Write-Host '桥接已注册到当前用户，不需要管理员权限。打开 EntLimit 后，在扩展弹窗点击同步与授权。'
