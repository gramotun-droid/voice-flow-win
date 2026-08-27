#Requires -Version 5.1

[CmdletBinding()]
param(
    [string]$ProcessName = 'VoiceFlowWin',
    [ValidateRange(10, 86400)]
    [int]$DurationSeconds = 300,
    [ValidateRange(1, 60)]
    [int]$SampleIntervalSeconds = 1,
    [string]$OutputPath = 'TestResults\performance.csv'
)

$ErrorActionPreference = 'Stop'

if ($env:OS -ne 'Windows_NT') {
    throw 'collect-performance.ps1 must run on Windows.'
}

$repository = Split-Path -Parent $PSScriptRoot
$process = Get-Process -Name $ProcessName -ErrorAction Stop | Select-Object -First 1
$samples = [System.Collections.Generic.List[object]]::new()
$previousCpu = $process.CPU
$previousAt = Get-Date
$deadline = $previousAt.AddSeconds($DurationSeconds)

Write-Host "Collecting metrics for process $($process.Id) for $DurationSeconds seconds. Start the test dictation."

while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds $SampleIntervalSeconds
    $process.Refresh()
    $now = Get-Date
    $cpu = $process.CPU
    $elapsed = ($now - $previousAt).TotalSeconds
    $cpuPercent = if ($elapsed -gt 0) {
        (($cpu - $previousCpu) / $elapsed / [Environment]::ProcessorCount) * 100
    } else { 0 }

    $samples.Add([pscustomobject][ordered]@{
        timestamp = $now.ToUniversalTime().ToString('o')
        cpuPercent = [math]::Round($cpuPercent, 2)
        workingSetMiB = [math]::Round($process.WorkingSet64 / 1MB, 2)
        privateMemoryMiB = [math]::Round($process.PrivateMemorySize64 / 1MB, 2)
        threads = $process.Threads.Count
        handles = $process.HandleCount
    })

    $previousCpu = $cpu
    $previousAt = $now
}

$absoluteOutput = Join-Path $repository $OutputPath
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $absoluteOutput) | Out-Null
$samples | Export-Csv $absoluteOutput -NoTypeInformation -Encoding utf8

$summary = [ordered]@{
    samples = $samples.Count
    averageCpuPercent = [math]::Round(($samples | Measure-Object cpuPercent -Average).Average, 2)
    peakCpuPercent = [math]::Round(($samples | Measure-Object cpuPercent -Maximum).Maximum, 2)
    averageWorkingSetMiB = [math]::Round(($samples | Measure-Object workingSetMiB -Average).Average, 2)
    peakWorkingSetMiB = [math]::Round(($samples | Measure-Object workingSetMiB -Maximum).Maximum, 2)
}

$summaryPath = [IO.Path]::ChangeExtension($absoluteOutput, '.summary.json')
$summary | ConvertTo-Json | Set-Content $summaryPath -Encoding utf8
Write-Host "CSV: $absoluteOutput"
Write-Host "Summary: $summaryPath"
$summary | Format-List
