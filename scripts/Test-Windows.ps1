param(
    [Parameter(Mandatory)]
    [string] $Executable,
    [switch] $Benchmark
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

function Invoke-Trail {
    param([int] $Expected, [string[]] $Arguments)
    $lines = & $Executable @Arguments
    if ($LASTEXITCODE -ne $Expected) {
        throw "Exitcode $LASTEXITCODE, erwartet $Expected. $lines"
    }
    $rows = @($lines | ConvertFrom-Json)
    if ($rows[0].type -ne 'start' -or $rows[-1].type -ne 'finish' -or
        $rows[-1].data.exitCode -ne $Expected) {
        throw 'JSONL-Rahmen oder Abschlussstatus fehlt.'
    }
    $rows
}

$version = Get-CimInstance Win32_OperatingSystem
Write-Host "$($version.Caption), Build $($version.BuildNumber), PowerShell $($PSVersionTable.PSVersion)"
$demo = Invoke-Trail 0 @('incident','Auftragsimport','--demo','complete','--format','jsonl')
if (@($demo | Where-Object type -eq 'result').Count -ne 2) { throw 'Demo-Fundstellen fehlen.' }
$null = Invoke-Trail 3 @('incident','Auftragsimport','--demo','partial','--order','found','--format','jsonl')

$timeline = @(& $Executable incident Auftragsimport --demo partial)
if ($LASTEXITCODE -ne 3 -or
    -not ($timeline -match '^09:15:01  LOG') -or
    -not ($timeline -match '^    auftragsimport\.log:2') -or
    -not ($timeline -match '^    System .* 4201') -or
    -not ($timeline -match '^  Suchlücke .*Application')) {
    throw 'Compact timeline or source coverage changed unexpectedly.'
}
$details = @(& $Executable incident Auftragsimport --demo complete --verbose)
if ($LASTEXITCODE -ne 0 -or -not ($details -match '^2026-01-15T09:15:01\.') -or
    -not ($details -match '^    [A-Z]:.*auftragsimport\.log:2')) {
    throw 'Verbose timeline did not show the full timestamp and path.'
}

$temporary = Join-Path ([IO.Path]::GetTempPath()) ("wtrail Prüfung " + [guid]::NewGuid())
$null = New-Item -ItemType Directory -Path $temporary
try {
    $log = Join-Path $temporary 'träge.log'
    $service = Get-Service -Name EventLog
    $marker = "wtrail smoke " + [guid]::NewGuid()
    $from = [DateTimeOffset]::UtcNow.AddSeconds(-1)
    $timestamp = [DateTime]::UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ")
    Set-Content -LiteralPath $log -Encoding Unicode -Value "$timestamp ERROR $($service.Name) [Probe]"
    $search = Invoke-Trail 0 @('search','[Probe]',$temporary,'--file-glob','*.log','--format','jsonl')
    if (-not ($search | Where-Object { $_.type -eq 'result' -and $_.data.line -eq 1 })) {
        throw 'UTF-16-Fundstelle fehlt.'
    }
    $grouped = @(& $Executable search '[Probe]' $temporary --file-glob '*.log')
    if ($LASTEXITCODE -ne 0 -or @($grouped -match '^träge\.log$').Count -ne 1 -or
        -not ($grouped -match '^  >\s+1\s+')) {
        throw 'Grouped file search did not show the file heading and marked line.'
    }
    $null = Invoke-Trail 1 @('search','kein Treffer',$temporary,'--format','jsonl')
    $null = Invoke-Trail 2 @('search','kein Treffer',(Join-Path $temporary 'fehlt'),'--format','jsonl')
    $lock = [IO.File]::Open($log, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
    try {
        $locked = Invoke-Trail 3 @('search','kein Treffer',$temporary,'--format','jsonl')
        if (-not ($locked | Where-Object { $_.type -eq 'source' -and $_.data.path -eq $log -and -not $_.data.checked })) {
            throw 'Gesperrte Datei wurde nicht als Suchlücke gemeldet.'
        }
    }
    finally { $lock.Dispose() }
    & eventcreate.exe /T ERROR /ID 901 /L APPLICATION /SO wtrail-smoke /D "$($service.Name) $marker"
    if ($LASTEXITCODE -ne 0) { throw 'Testereignis konnte nicht geschrieben werden.' }
    $event = Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'wtrail-smoke'; Id = 901 } -MaxEvents 1
    $to = [DateTimeOffset]::UtcNow.AddSeconds(1)
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $actual = Invoke-Trail 0 @('incident',$service.Name,'--from',$from.ToString('o'),'--to',
        $to.ToString('o'),'--channel','Application',
        '--provider','wtrail-smoke','--event-id','901','--logs',$temporary,'--format','jsonl')
    $watch.Stop()
    $results = @($actual | Where-Object type -eq 'result')
    if (-not ($results | Where-Object { $_.data.RecordId -eq $event.RecordId }) -or
        -not ($results | Where-Object { $_.data.Path -eq $log -and $_.data.Line -eq 1 })) {
        throw 'Der native Vorfall enthält nicht beide erwarteten Originalfundstellen.'
    }
    Write-Host "Windows-End-to-End bestanden, Suchlauf inklusive Prozessstart: $($watch.ElapsedMilliseconds) ms."
    $missing = Invoke-Trail 3 @('incident',$service.Name,'--from',$from.ToString('o'),'--to',
        $to.ToString('o'),'--channel','Application','--provider','wtrail-smoke','--event-id','901',
        '--logs',(Join-Path $temporary 'fehlt'),'--format','jsonl')
    if (-not ($missing | Where-Object { $_.type -eq 'result' -and $_.data.RecordId -eq $event.RecordId })) {
        throw 'Ein fehlender Dateipfad hat den Ereignistreffer unterdrückt.'
    }

    if ($Benchmark) {
        $rg = (Get-Command rg -ErrorAction Stop).Source
        & $rg --version | Select-Object -First 1
        $trialArgs = @('incident',$service.Name,'--from',$from.ToString('o'),'--to',$to.ToString('o'),
            '--channel','Application','--provider','wtrail-smoke','--event-id','901','--logs',$temporary,'--order','found','--format','jsonl')
        $filter = @{ LogName = 'Application'; ProviderName = 'wtrail-smoke'; Id = 901;
            StartTime = $from.LocalDateTime; EndTime = $to.LocalDateTime }
        for ($trial = 1; $trial -le 5; $trial++) {
            $watch.Restart()
            $baselineEvent = Get-WinEvent -FilterHashtable $filter |
                Where-Object { $_.TimeCreated.ToUniversalTime() -lt $to.UtcDateTime -and $_.Message.Contains($service.Name) } |
                Select-Object -First 1
            $baselineLine = & $rg --fixed-strings --line-number --with-filename --glob '*.log' $service.Name $temporary
            $watch.Stop()
            $baselineMs = $watch.Elapsed.TotalMilliseconds
            if ($LASTEXITCODE -ne 0 -or $baselineEvent.RecordId -ne $event.RecordId -or
                -not ($baselineLine -like "*$([IO.Path]::GetFileName($log)):1:*")) {
                throw 'Vergleichsmessung hat nicht beide Originalfundstellen geliefert.'
            }

            $watch.Restart()
            $native = Invoke-Trail 0 $trialArgs
            $watch.Stop()
            $nativeMs = $watch.Elapsed.TotalMilliseconds
            if (-not ($native | Where-Object { $_.type -eq 'result' -and $_.data.RecordId -eq $event.RecordId }) -or
                -not ($native | Where-Object { $_.type -eq 'result' -and $_.data.Path -eq $log -and $_.data.Line -eq 1 })) {
                throw 'wtrail-Vergleichsmessung hat nicht beide Originalfundstellen geliefert.'
            }
            Write-Host ("Benchmark {0}: rg + Get-WinEvent {1:F1} ms; wtrail {2:F1} ms" -f $trial,$baselineMs,$nativeMs)
        }
    }
    $longDirectory = $temporary
    while ($longDirectory.Length -lt 320) {
        $longDirectory = Join-Path $longDirectory ('unterordner-' + ('a' * 72))
    }
    $null = [IO.Directory]::CreateDirectory($longDirectory)
    $longLog = Join-Path $longDirectory 'langer-pfad.log'
    [IO.File]::WriteAllText($longLog, 'lange Fundstelle')
    $long = Invoke-Trail 0 @('search','lange Fundstelle',$temporary,'--format','jsonl')
    if (-not ($long | Where-Object { $_.type -eq 'result' -and $_.data.path -eq $longLog })) {
        throw 'Fundstelle hinter der 260-Zeichen-Grenze fehlt.'
    }

    $offlineFile = Join-Path $temporary 'offline.log'
    [IO.File]::WriteAllText($offlineFile, 'Offline-Prüfmarke')
    $attributes = [IO.File]::GetAttributes($offlineFile)
    try {
        [IO.File]::SetAttributes($offlineFile, ($attributes -bor [IO.FileAttributes]::Offline))
        $offline = Invoke-Trail 3 @('search','Offline-Prüfmarke',$temporary,'--format','jsonl')
        if (($offline | Where-Object type -eq 'result') -or
            -not ($offline | Where-Object { $_.type -eq 'source' -and $_.data.path -eq $offlineFile -and -not $_.data.checked })) {
            throw 'Datei mit Offline-Attribut wurde nicht als Suchlücke übersprungen.'
        }
    }
    finally { [IO.File]::SetAttributes($offlineFile, $attributes) }

    $serviceName = 'wtrail-test-' + [guid]::NewGuid().ToString('N')
    $wrapper = Join-Path $temporary 'WinSW Test.exe'
    $config = [IO.Path]::ChangeExtension($wrapper, '.xml')
    $logDirectory = [IO.Directory]::CreateDirectory((Join-Path $temporary 'WinSW Logs'))
    $configuredLog = Join-Path $logDirectory.FullName 'wrapper.log'
    [IO.File]::WriteAllText($configuredLog, "$timestamp ERROR $serviceName")
    [IO.File]::WriteAllText($config, "<service><id>$serviceName</id><logpath>%BASE%/WinSW Logs</logpath></service>")
    $null = New-Service -Name $serviceName -BinaryPathName ('"' + $wrapper + '"') -StartupType Disabled
    try {
        $serviceArgs = @('incident',$serviceName,'--from',$from.ToString('o'),'--to',$to.ToString('o'),
            '--channel','Application','--provider','wtrail-smoke','--event-id','901','--format','jsonl')
        $automatic = Invoke-Trail 0 $serviceArgs
        if (-not ($automatic | Where-Object { $_.type -eq 'source' -and $_.data.name -eq $config -and $_.data.Checked }) -or
            -not ($automatic | Where-Object { $_.type -eq 'result' -and $_.data.Path -eq $configuredLog })) {
            throw 'Automatische WinSW-Auflösung lieferte Konfigurationsquelle oder Logtreffer nicht.'
        }

        $overrideDirectory = [IO.Directory]::CreateDirectory((Join-Path $temporary 'Explizite Logs'))
        $overrideLog = Join-Path $overrideDirectory.FullName 'override.log'
        [IO.File]::WriteAllText($overrideLog, "$timestamp ERROR $serviceName")
        $explicit = Invoke-Trail 0 ($serviceArgs + @('--logs',$overrideDirectory.FullName))
        $fileResults = @($explicit | Where-Object { $_.type -eq 'result' -and $_.data.Path })
        if ($fileResults.Count -ne 1 -or $fileResults[0].data.Path -ne $overrideLog) {
            throw 'Der explizite Pfad hatte keinen Vorrang.'
        }
    }
    finally { Remove-Service -Name $serviceName }

    Write-Host 'Lange Pfade, Offline-Dateiattribut, automatische WinSW-Auflösung und Vorrang von --logs bestanden.'

}
finally {
    Remove-Item -LiteralPath $temporary -Recurse -Force
}
