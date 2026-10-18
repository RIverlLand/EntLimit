param([string]$Instance = $env:ENT_LIMIT_INSTANCE)
$ErrorActionPreference = 'Stop'
# Explicit development escape hatch. No process enumeration or termination.
$identity = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$pipeName = 'EntLimit.v1.' + ($identity -replace '[^a-zA-Z0-9-]', '_')
if (-not [string]::IsNullOrWhiteSpace($Instance)) {
    if ($Instance.Length -gt 64 -or $Instance -match '[^a-zA-Z0-9-]') { throw 'Invalid isolated instance name.' }
    $pipeName += '.' + $Instance
}
$pipe = [IO.Pipes.NamedPipeClientStream]::new('.', $pipeName, [IO.Pipes.PipeDirection]::InOut)
try {
    $pipe.Connect(5000)
    $encoding = [Text.UTF8Encoding]::new($false)
    $writer = [IO.StreamWriter]::new($pipe, $encoding, 1024, $true)
    $writer.AutoFlush = $true
    $reader = [IO.StreamReader]::new($pipe, $encoding, $false, 1024, $true)
    try {
        $writer.WriteLine('{"Command":"debug_stop"}')
        $replyTask = $reader.ReadLineAsync()
        if (-not $replyTask.Wait(10000)) { throw 'Debug stop timed out; no process was terminated.' }
        $reply = $replyTask.Result | ConvertFrom-Json
        if ($null -eq $reply -or -not $reply.Ok) { throw "Debug stop failed: $($reply.Error)" }
        Write-Host 'EntLimit: monitoring, startup, guard and reminders disabled; monitor will exit. Learning data preserved.'
        Write-Host 'Browser extension rules clear at the next sync (up to one minute), or sync manually.'
    } finally { $reader.Dispose(); $writer.Dispose() }
} finally { $pipe.Dispose() }
