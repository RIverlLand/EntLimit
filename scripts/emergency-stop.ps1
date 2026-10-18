param(
    [string]$AppDirectory,
    [string]$DataDirectory,
    [string]$Instance = '',
    [int[]]$ProcessIds = @()
)
$ErrorActionPreference = 'Stop'
# Independent escape hatch: never waits for EntLimit IPC or rewrites its learning data.
if ([string]::IsNullOrWhiteSpace($AppDirectory)) {
    if (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'EntLimit.Monitor.exe')) { $AppDirectory = $PSScriptRoot }
    else { $AppDirectory = Join-Path $PSScriptRoot '../artifacts/app' }
}
$app = [IO.Path]::GetFullPath($AppDirectory)
if (-not (Test-Path -LiteralPath (Join-Path $app 'EntLimit.Monitor.exe'))) { throw 'Cannot find this installation. Specify -AppDirectory.' }
if ([string]::IsNullOrWhiteSpace($DataDirectory)) {
    $DataDirectory = if (Test-Path -LiteralPath (Join-Path $app 'installed.flag')) { Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'EntLimit/data' } else { Join-Path $app 'data' }
}
$dataRoot = [IO.Path]::GetFullPath($DataDirectory)
if ($Instance -and ($Instance.Length -gt 64 -or $Instance -match '[^a-zA-Z0-9-]')) { throw 'Invalid isolated instance name.' }
if ($Instance -and $ProcessIds.Count -eq 0) { throw 'Isolated tests require exact -ProcessIds; no process-name stopping is permitted.' }
if (-not $Instance -and $ProcessIds.Count -gt 0) { throw '-ProcessIds is restricted to isolated tests with -Instance.' }
$problems = [Collections.Generic.List[string]]::new()
$flag = Join-Path $dataRoot 'emergency-stop.flag'
try {
    [IO.Directory]::CreateDirectory($dataRoot) | Out-Null
    $file = [IO.File]::Open($flag, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::Write, [IO.FileShare]::ReadWrite)
    $file.Dispose()
    Write-Host 'Emergency latch enabled. Startup, guard and monitoring stay paused until explicitly resumed.'
} catch { $problems.Add('Cannot persist emergency latch: ' + $_.Exception.Message) }

function Quote-Startup([string]$value) {
    $count = $value.Length - $value.TrimEnd('\').Length
    return '"' + $value + ('\' * $count) + '"'
}
if (-not $Instance) {
    try {
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Run', $true)
        if ($key) {
            try {
                $expected = (Quote-Startup (Join-Path $app 'EntLimit.Monitor.exe')) + ' --data-dir ' + (Quote-Startup $dataRoot)
                $actual = $key.GetValue('EntLimit', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
                if ($actual -eq $expected) { $key.DeleteValue('EntLimit', $false); Write-Host 'Removed login startup for this installation.' }
                elseif ($null -ne $actual) { Write-Warning 'Another startup command was preserved. This installation remains paused by the emergency latch.' }
            } finally { $key.Dispose() }
        }
    } catch { $problems.Add('Cannot remove login startup: ' + $_.Exception.Message) }
}

$session = [Diagnostics.Process]::GetCurrentProcess().SessionId
$paths = @((Join-Path $app 'EntLimit.Monitor.exe'), (Join-Path $app 'EntLimit.Desktop.exe'), (Join-Path $app 'EntLimit.AdminBroker.exe'))
$stopped = [Collections.Generic.HashSet[int]]::new()
# Stop only this installation, in this interactive session. Repeat to catch an
# already-in-flight recovery spawn. Never stop managed apps, games or browsers.
for ($round = 0; $round -lt 3; $round++) {
    $candidates = @(Get-CimInstance Win32_Process -Filter "Name='EntLimit.Monitor.exe' OR Name='EntLimit.Desktop.exe' OR Name='EntLimit.AdminBroker.exe'" |
        Where-Object { $_.SessionId -eq $session -and $_.ExecutablePath -and $paths -contains $_.ExecutablePath -and
            ((-not $Instance) -or ($ProcessIds -contains [int]$_.ProcessId)) } |
        Sort-Object @{Expression={ if ($_.CommandLine -like '*--watchdog*') { 0 } elseif ($_.Name -eq 'EntLimit.Desktop.exe') { 1 } else { 2 } }})
    foreach ($candidate in $candidates) {
        $process = $null
        try {
            $process = [Diagnostics.Process]::GetProcessById([int]$candidate.ProcessId)
            # Pin the process handle before validating its path and session: a reused
            # PID must never redirect the termination to a different process.
            $null = $process.Handle
            if ($process.SessionId -ne $session -or $paths -notcontains $process.MainModule.FileName) { continue }
            $process.Kill()
            if (-not $process.WaitForExit(3000)) { throw 'Process did not exit within three seconds.' }
            $stopped.Add([int]$candidate.ProcessId) | Out-Null
        } catch [ArgumentException] { }
        catch [InvalidOperationException] { }
        catch { $problems.Add('Cannot stop EntLimit PID ' + $candidate.ProcessId + ': ' + $_.Exception.Message) }
        finally { if ($process) { $process.Dispose() } }
    }
    if ($round -lt 2) { Start-Sleep -Milliseconds 350 }
}
Write-Host ('Stopped EntLimit processes: ' + $stopped.Count)
Write-Host 'Config, learning records and drafts were not rewritten. Unsaved work may be lost during this forced stop.'
Write-Host 'Browser rules clear on the next extension sync (up to one minute). Reopen EntLimit and use Resume only when ready.'
if ($problems.Count) { foreach ($problem in $problems) { Write-Warning $problem }; exit 1 }
exit 0
