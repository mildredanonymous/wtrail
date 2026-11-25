param(
    [Parameter(Mandatory)]
    [string] $Executable
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
    $null = Invoke-Trail 1 @('search','kein Treffer',$temporary,'--format','jsonl')
    $null = Invoke-Trail 2 @('search','kein Treffer',(Join-Path $temporary 'fehlt'),'--format','jsonl')
    & eventcreate.exe /T ERROR /ID 901 /L APPLICATION /SO wtrail-smoke /D "$($service.Name) $marker"
    if ($LASTEXITCODE -ne 0) { throw 'Testereignis konnte nicht geschrieben werden.' }
    $event = Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'wtrail-smoke'; Id = 901 } -MaxEvents 1
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $actual = Invoke-Trail 0 @('incident',$service.Name,'--from',$from.ToString('o'),'--to',
        [DateTimeOffset]::UtcNow.AddSeconds(1).ToString('o'),'--channel','Application',
        '--provider','wtrail-smoke','--event-id','901','--logs',$temporary,'--format','jsonl')
    $watch.Stop()
    $results = @($actual | Where-Object type -eq 'result')
    if (-not ($results | Where-Object { $_.data.RecordId -eq $event.RecordId }) -or
        -not ($results | Where-Object { $_.data.Path -eq $log -and $_.data.Line -eq 1 })) {
        throw 'Der native Vorfall enthält nicht beide erwarteten Originalfundstellen.'
    }
    Write-Host "Windows-End-to-End bestanden, Suchlauf inklusive Prozessstart: $($watch.ElapsedMilliseconds) ms."
}
finally {
    Remove-Item -LiteralPath $temporary -Recurse -Force
}
