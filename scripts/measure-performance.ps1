# Измерение ресурсов запущенного GameProfileManager без изменения его настроек.
# Скрипт предназначен для одинаковых замеров до и после рефакторинга.
[CmdletBinding()]
param(
    [ValidatePattern('^[A-Za-z0-9_-]+$')]
    [string]$Scenario = 'tray',

    [ValidateRange(1, 120)]
    [int]$DurationMinutes = 10,

    [ValidateRange(1, 60)]
    [int]$IntervalSeconds = 5,

    [ValidateRange(0, 2147483647)]
    [int]$TargetPid = 0,

    [string]$OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\metrics')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($TargetPid -gt 0) {
    $target = Get-Process -Id $TargetPid -ErrorAction Stop
}
else {
    $candidates = @(Get-Process -Name 'GameProfileManager' -ErrorAction SilentlyContinue)
    if ($candidates.Count -ne 1) {
        throw "Найдено процессов GameProfileManager: $($candidates.Count). Запустите один экземпляр или передайте -TargetPid."
    }
    $target = $candidates[0]
}

# Идентификатор процесса фиксируется на весь сеанс: если приложение перезапустится,
# нельзя незаметно переключиться на другую копию и исказить результаты.
$measuredPid = $target.Id
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$destination = Join-Path $OutputDirectory "$stamp-$Scenario-pid$measuredPid.csv"

$samples = New-Object 'System.Collections.Generic.List[object]'
$watch = [System.Diagnostics.Stopwatch]::StartNew()
$previousTime = $null
$previousCpu = $null
$sampleCount = [int][Math]::Ceiling(($DurationMinutes * 60.0) / $IntervalSeconds)

Write-Host "GameProfileManager: PID=$measuredPid; сценарий=$Scenario; замер=$DurationMinutes мин."
Write-Host 'Оставьте приложение в выбранном состоянии до окончания измерения.'

try {
    for ($index = 0; $index -le $sampleCount; $index++) {
        $target.Refresh()
        if ($target.HasExited) {
            throw "GameProfileManager (PID=$measuredPid) завершился во время замера."
        }

        $elapsed = $watch.Elapsed.TotalSeconds
        $cpuSeconds = $target.TotalProcessorTime.TotalSeconds

        $cpuPercent = $null
        if ($null -ne $previousTime -and $elapsed -gt $previousTime) {
            # Процент нормирован на все логические процессоры, как общий CPU в Диспетчере задач.
            $cpuPercent = [Math]::Round(
                (($cpuSeconds - $previousCpu) / ($elapsed - $previousTime)) *
                100.0 / [Environment]::ProcessorCount, 3
            )
        }

        $samples.Add([pscustomobject]@{
            TimestampUtc       = [DateTimeOffset]::UtcNow.ToString('o')
            ElapsedSeconds     = [Math]::Round($elapsed, 2)
            WorkingSetMB       = [Math]::Round($target.WorkingSet64 / 1MB, 2)
            PrivateBytesMB     = [Math]::Round($target.PrivateMemorySize64 / 1MB, 2)
            CpuPercentTotal    = $cpuPercent
            HandleCount        = $target.HandleCount
            ThreadCount        = $target.Threads.Count
        })

        $previousTime = $elapsed
        $previousCpu = $cpuSeconds

        if ($index -lt $sampleCount) {
            Start-Sleep -Seconds $IntervalSeconds
        }
    }
}
finally {
    $watch.Stop()

    if ($samples.Count -gt 0) {
        $samples | Export-Csv -Path $destination -NoTypeInformation -Encoding UTF8
        $working = $samples | Measure-Object -Property WorkingSetMB -Average -Minimum -Maximum
        $private = $samples | Measure-Object -Property PrivateBytesMB -Average -Minimum -Maximum
        $cpuSamples = @($samples | Where-Object { $null -ne $_.CpuPercentTotal })
        $cpuAverage = if ($cpuSamples.Count -gt 0) {
            ($cpuSamples | Measure-Object -Property CpuPercentTotal -Average).Average
        }
        else { $null }

        $summary = [pscustomobject]@{
            Scenario              = $Scenario
            ProcessId             = $measuredPid
            SampleCount           = $samples.Count
            ActualDurationSeconds = [Math]::Round($watch.Elapsed.TotalSeconds, 1)
            LogicalProcessors     = [Environment]::ProcessorCount
            WorkingSetAverageMB   = [Math]::Round($working.Average, 2)
            WorkingSetMaximumMB   = [Math]::Round($working.Maximum, 2)
            PrivateBytesAverageMB = [Math]::Round($private.Average, 2)
            PrivateBytesMaximumMB = [Math]::Round($private.Maximum, 2)
            CpuAveragePercent     = if ($null -ne $cpuAverage) { [Math]::Round($cpuAverage, 3) } else { $null }
            SamplesCsv            = $destination
        }

        $summaryPath = [IO.Path]::ChangeExtension($destination, '.json')
        $summary | ConvertTo-Json -Depth 3 | Set-Content -Path $summaryPath -Encoding UTF8
        $summary | Format-List
        Write-Host "CSV: $destination"
        Write-Host "Сводка: $summaryPath"
    }

    $target.Dispose()
}

# Примечание: WorkingSetMB включает совместно используемые страницы и не обязан совпадать
# с колонкой «Память» в Диспетчере задач. Для анализа GC нужен отдельный .NET-профайлер.
