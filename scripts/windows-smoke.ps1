#Requires -Version 5.1

[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$ReportPath = 'TestResults\windows-smoke.json',
    [string]$InstallerPath = '',
    [switch]$LaunchApplication
)

$ErrorActionPreference = 'Stop'

if ($env:OS -ne 'Windows_NT') {
    throw 'windows-smoke.ps1 must run on Windows 10 or 11.'
}

$repository = Split-Path -Parent $PSScriptRoot
Set-Location $repository
$results = [System.Collections.Generic.List[object]]::new()

function Invoke-SmokeStep {
    param(
        [string]$Name,
        [scriptblock]$Action
    )

    $started = Get-Date
    try {
        & $Action
        $results.Add([ordered]@{
            name = $Name
            passed = $true
            durationSeconds = [math]::Round(((Get-Date) - $started).TotalSeconds, 2)
            error = $null
        })
    }
    catch {
        $results.Add([ordered]@{
            name = $Name
            passed = $false
            durationSeconds = [math]::Round(((Get-Date) - $started).TotalSeconds, 2)
            error = $_.Exception.Message
        })
        throw
    }
}

function Invoke-DotNet {
    & dotnet $args
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet failed with exit code ${LASTEXITCODE}: dotnet $($args -join ' ')"
    }
}

$publishPath = Join-Path $repository 'artifacts\smoke-publish'

try {
    Invoke-SmokeStep 'dotnet info' { Invoke-DotNet --info }
    Invoke-SmokeStep 'SDK available' {
        $sdks = & dotnet --list-sdks
        if ($LASTEXITCODE -ne 0 -or -not $sdks) {
            throw '.NET SDK is not installed or is not available on PATH.'
        }
    }
    Invoke-SmokeStep 'restore' { Invoke-DotNet restore VoiceFlowWin.sln }
    Invoke-SmokeStep 'format' { Invoke-DotNet format VoiceFlowWin.sln --verify-no-changes --severity error --no-restore }
    Invoke-SmokeStep 'build' { Invoke-DotNet build VoiceFlowWin.sln -c $Configuration --no-restore }
    Invoke-SmokeStep 'core tests' { Invoke-DotNet test tests\VoiceFlowWin.Tests\VoiceFlowWin.Tests.csproj -c $Configuration --no-build }
    Invoke-SmokeStep 'windows tests' { Invoke-DotNet test tests\VoiceFlowWin.Windows.Tests\VoiceFlowWin.Windows.Tests.csproj -c $Configuration --no-build }
    Invoke-SmokeStep 'win-x64 publish' {
        Invoke-DotNet publish src\VoiceFlowWin.App\VoiceFlowWin.App.csproj -c $Configuration -r win-x64 --self-contained false -o $publishPath
        Invoke-DotNet publish src\VoiceFlowWin.UpdaterHost\VoiceFlowWin.UpdaterHost.csproj -c $Configuration -r win-x64 --self-contained false -o $publishPath
    }
    Invoke-SmokeStep 'native runtime' {
        if (Get-ChildItem $publishPath -Filter '*.so' -File) {
            throw 'A Linux native library (*.so) was found in the Windows publish output.'
        }

        if (-not (Test-Path (Join-Path $publishPath 'VoiceFlowWin.exe'))) {
            throw 'VoiceFlowWin.exe is missing from the publish output.'
        }

        if (-not (Get-ChildItem $publishPath -Filter '*sherpa*onnx*.dll' -File)) {
            throw 'The sherpa-onnx Windows runtime is missing from the publish output.'
        }
    }
    Invoke-SmokeStep 'signatures' {
        $targets = @(
            (Join-Path $publishPath 'VoiceFlowWin.exe'),
            (Join-Path $publishPath 'VoiceFlowWin.UpdaterHost.exe')
        )
        if ($InstallerPath) { $targets += (Resolve-Path $InstallerPath).Path }

        foreach ($target in $targets) {
            $signature = Get-AuthenticodeSignature $target
            if ($signature.Status -notin @('Valid', 'NotSigned')) {
                throw "Invalid signature for ${target}: $($signature.Status)."
            }
            Write-Host "${target} - $($signature.Status)"
        }
    }

    if ($LaunchApplication) {
        Invoke-SmokeStep 'application startup' {
            $process = Start-Process (Join-Path $publishPath 'VoiceFlowWin.exe') -PassThru
            try {
                Start-Sleep -Seconds 8
                if ($process.HasExited) {
                    throw "The application exited with code $($process.ExitCode)."
                }
            }
            finally {
                if (-not $process.HasExited) { Stop-Process -Id $process.Id }
            }
        }
    }
}
finally {
    $report = [ordered]@{
        generatedAt = (Get-Date).ToUniversalTime().ToString('o')
        computer = $env:COMPUTERNAME
        windows = [System.Environment]::OSVersion.VersionString
        configuration = $Configuration
        results = $results
        passed = -not ($results | Where-Object { -not $_.passed })
    }

    $absoluteReport = Join-Path $repository $ReportPath
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $absoluteReport) | Out-Null
    $report | ConvertTo-Json -Depth 6 | Set-Content $absoluteReport -Encoding utf8
    Write-Host "Report: $absoluteReport"
}
