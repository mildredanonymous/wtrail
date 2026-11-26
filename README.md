# wtrail · Incident Trail

Investigate a Windows service incident across Windows Event Logs and local application logs. `wtrail` shows both sources on one timeline, points to the original event record or file line, and reports gaps in search coverage.

Built for developers and IT operations working on Windows 11 x64 with PowerShell 7. The current build is a release candidate. See the [Windows acceptance report](ACCEPTANCE.md) for verified scenarios and remaining release checks. The CLI's human-readable output is currently in German.

## Install from a portable ZIP

1. Sign in to GitHub and open the [latest successful Windows build](https://github.com/mildredanonymous/wtrail/actions/workflows/windows.yml?query=branch%3Amain+is%3Asuccess). Open a successful run and download **wtrail-1.0.0-rc.1-win-x64** under **Artifacts**. If the artifact has expired, [build from source](#build-from-source).
2. Extract the outer archive. In PowerShell, change to the directory containing the inner ZIP and its `.sha256` file. Verify and extract it:

   ```powershell
   $zip = '.\wtrail-1.0.0-rc.1-win-x64.zip'
   $expected = ((Get-Content -LiteralPath "${zip}.sha256" -Raw).Trim() -split '\s+')[0]
   if ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ne $expected) { throw 'SHA-256 mismatch' }
   Expand-Archive -LiteralPath $zip -DestinationPath .\wtrail-rc1
   Set-Location .\wtrail-rc1
   .\wtrail.exe --help
   ```

The package contains the .NET runtime. Keep the extracted files together. To update, verify a newer package and extract it to a new directory. To remove `wtrail`, delete the extracted directory.

## Try the included demo

Run these commands from the directory containing `wtrail.exe`. The fixture runs without an installed test service or administrator privileges.

```powershell
.\wtrail.exe incident Auftragsimport --demo complete
.\wtrail.exe incident Auftragsimport --demo partial
```

The complete demo returns two timestamped results: line 2 of `auftragsimport.log` and simulated `System` record 4201. The partial demo keeps both results and reports simulated access denial for `Application`. The expected exit codes are `0` and `3`, respectively. In PowerShell, read the last command's exit code with `$LASTEXITCODE`.

## Investigate a service

```powershell
.\wtrail.exe incident EventLog --since 15m --channel System
.\wtrail.exe incident 'My Service' --since 30m --logs 'C:\Apps\My Service\Logs'
```

The first command reads a real Windows event channel using the built-in Event Log service. The number of results depends on the machine and time window. For the second command, substitute your service name and local log path.

- Supply a service's internal name or display name. If more than one service matches, use an internal name from the candidate list.
- Use `--since` with seconds, minutes, hours, or days (for example, `30m`), or supply both `--from` and `--to`. Absolute bounds accept ISO 8601 timestamps such as `2026-01-15T09:14:00Z`. The start is inclusive and the end is exclusive. An input without an offset uses local time. Supply an offset for ambiguous local times.
- The default event channels are `System` and `Application`. Repeat `--channel` to select multiple channels. Use `--provider` and `--event-id` to narrow the results.
- `--logs` takes a local file or a directory searched recursively. The default file-name filter is `*.log`. Set `--file-glob` to change it. A service-related line with no reliable leading timestamp is marked as undated.
- Without `--logs`, `wtrail` looks for a same-named WinSW XML file next to the configured service executable. The XML must contain the service's internal ID and one explicit `<logpath>`. Relative paths and `%BASE%` resolve against the XML directory. When discovery fails, the event results remain visible and file coverage is reported as unchecked.

The default order is chronological. `--order found` streams incident results as the sources are scanned. The text view puts the message on the first line and the original location on the next. It shows file paths relative to the displayed log directory and ends with a source coverage summary. Add `--verbose` for full paths, precise timestamps, and available provider and event IDs.

## Search files directly

```powershell
.\wtrail.exe search '[ERROR]' 'C:\Apps\My Service\Logs' --file-glob '*.log' --context 2
.\wtrail.exe search '^ERROR\b' 'C:\Apps\My Service\Logs' --regex --format jsonl
```

File search streams results under one heading per file. The `>` marker identifies matches within context lines. Add `--verbose` for full paths on each result and detailed source status. Patterns are literal and case-sensitive by default. Omit the path to search the current directory recursively. Use `--` before a pattern that starts with `--`. The reader supports UTF-8 and UTF-16 with a byte order mark. The tool reports skipped links, files marked offline, access errors and regex timeouts in the source status.

## JSON Lines and exit codes

Both commands accept `--format jsonl`. Each line is a JSON object with `version`, `type` and `data` fields. A run emits a `start` record, `result` and `source` records as available, and a `finish` record with the exit code and match count. Already emitted records remain usable after a handled interruption.

| Exit code | Meaning |
| --- | --- |
| `0` | Search complete, with results |
| `1` | Search complete, with no results |
| `2` | Invalid invocation or search could not complete any source or return a result |
| `3` | Partial search, with or without results |
| `130` | Search interrupted by `Ctrl+C` |

`wtrail` reads local files and event channels using the permissions of the current session. It skips network paths in Windows file searches and does not create an index or keep a search history. Review the output for sensitive log content before sharing it.

## Build from source

Install Git and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). In PowerShell 7, run:

```powershell
git clone https://github.com/mildredanonymous/wtrail.git
Set-Location wtrail
dotnet publish .\wtrail.csproj -c Release -r win-x64 --self-contained -o .\publish\win-x64
.\publish\win-x64\wtrail.exe incident Auftragsimport --demo complete
```

The portable build is in `publish\win-x64`. The [Windows CI workflow](.github/workflows/windows.yml) builds and tests the package. To report a problem, include the command with any sensitive values removed, the exit code, and your Windows and PowerShell versions. [Open an issue](https://github.com/mildredanonymous/wtrail/issues/new)
