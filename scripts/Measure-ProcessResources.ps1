<#
.SYNOPSIS
Samples an existing process without changing its configuration or GC policy.
.EXAMPLE
.\scripts\Measure-ProcessResources.ps1 -TargetProcessId 12345 -DurationSeconds 60 -OutputPath .\.tmp\idle.json
.NOTES
CpuPercentOneCore is 100% for one fully busy logical CPU; CpuPercentMachine
is normalized across logical CPUs. PrivateBytes is committed private memory,
not managed heap size. WorkingSet includes shared/resident pages, not GPU VRAM.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateRange(1, [int]::MaxValue)]
    [int]$TargetProcessId,
    [ValidateRange(1, 3600)]
    [int]$DurationSeconds = 60,
    [ValidateRange(100, 5000)]
    [int]$IntervalMilliseconds = 1000,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$targetProcess = Get-Process -Id $TargetProcessId
$processStarted = $targetProcess.StartTime.ToUniversalTime()
$processName = $targetProcess.ProcessName
$logicalCpuCount = [Environment]::ProcessorCount
$rows = [Collections.Generic.List[object]]::new()
$watch = [Diagnostics.Stopwatch]::StartNew()
$previousTime = 0.0
$previousCpu = $targetProcess.TotalProcessorTime.TotalSeconds
$initialCpu = $previousCpu
$endedEarly = $false

try {
    while ($watch.Elapsed.TotalSeconds -lt $DurationSeconds) {
        $remainingMs = ($DurationSeconds - $watch.Elapsed.TotalSeconds) * 1000
        Start-Sleep -Milliseconds ([int][Math]::Max(1, [Math]::Min($IntervalMilliseconds, $remainingMs)))
        $targetProcess.Refresh()
        if ($targetProcess.HasExited) { $endedEarly = $true; break }
        $elapsed = $watch.Elapsed.TotalSeconds
        $cpu = $targetProcess.TotalProcessorTime.TotalSeconds
        $oneCorePercent = 100 * ($cpu - $previousCpu) / ($elapsed - $previousTime)
        $rows.Add([pscustomobject]@{
            ElapsedSeconds = $elapsed
            CpuPercentOneCore = $oneCorePercent
            CpuPercentMachine = $oneCorePercent / $logicalCpuCount
            WorkingSetBytes = $targetProcess.WorkingSet64
            PrivateBytes = $targetProcess.PrivateMemorySize64
            Handles = $targetProcess.HandleCount
            Threads = $targetProcess.Threads.Count
        })
        $previousTime = $elapsed
        $previousCpu = $cpu
    }
}
finally {
    $watch.Stop()
    $targetProcess.Dispose()
}

if ($rows.Count -eq 0) { throw 'The process exited before any sample could be collected.' }
$cpuStats = $rows | Measure-Object CpuPercentMachine -Average -Maximum
$workingSetStats = $rows | Measure-Object WorkingSetBytes -Average -Maximum
$privateStats = $rows | Measure-Object PrivateBytes -Average -Maximum
$result = [pscustomobject]@{
    ProcessId = $TargetProcessId
    ProcessName = $processName
    ProcessStartedUtc = $processStarted.ToString('O')
    LogicalCpuCount = $logicalCpuCount
    ElapsedSeconds = $watch.Elapsed.TotalSeconds
    EndedEarly = $endedEarly
    Summary = [pscustomobject]@{
        SampleCount = $rows.Count
        AverageCpuPercentMachine = 100 * ($previousCpu - $initialCpu) / $previousTime / $logicalCpuCount
        PeakCpuPercentMachine = $cpuStats.Maximum
        AverageWorkingSetMiB = $workingSetStats.Average / 1MB
        PeakWorkingSetMiB = $workingSetStats.Maximum / 1MB
        AveragePrivateMiB = $privateStats.Average / 1MB
        PeakPrivateMiB = $privateStats.Maximum / 1MB
        PrivateDeltaMiB = ($rows[$rows.Count - 1].PrivateBytes - $rows[0].PrivateBytes) / 1MB
    }
    Samples = $rows.ToArray()
}
if ($OutputPath) {
    $resolvedOutput = [IO.Path]::GetFullPath($OutputPath)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($resolvedOutput)) | Out-Null
    $result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $resolvedOutput -Encoding utf8
}
$result
