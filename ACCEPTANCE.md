# Windows acceptance report

## Windows 11 x64

A user built RC 1 from the repository and exercised it on Windows 11. The exact OS build and PowerShell version were not captured.

| Check | Observed result |
| --- | --- |
| Complete demo | Application log line 2 and simulated System record 4201 in chronological order, exit code 0 |
| Demo with a coverage gap | Both results remained visible, simulated access denial for Application, exit code 3 |
| Real System channel | Resolved the EventLog service, checked System, found no events in the 15-minute window, and reported file coverage as unchecked, exit code 3 |
| Interactive Ctrl+C in PowerShell | File search stopped during JSONL output and emitted a complete finish record with exit code 130. Windows CI separately verified the process exit code. |

## Windows CI

- A native test on Windows Server 2025 Build 26100 and PowerShell 7.6.10 found a real event and the corresponding line in a UTF-16 log. [End-to-end run](https://github.com/mildredanonymous/wtrail/actions/runs/45692891283)
- An exclusively locked file was reported as a coverage gap. An event result remained visible when the log path was missing. [Access and coverage run](https://github.com/mildredanonymous/wtrail/actions/runs/45696059978)
- Recursive search found a file beyond the 260-character path boundary. The tool skipped a local file marked offline before reading it. It read a WinSW-compatible configuration for a disabled test service, expanded `%BASE%` and gave an explicit `--logs` path precedence. [Path and WinSW run](https://github.com/mildredanonymous/wtrail/actions/runs/45696561360)
- A Windows console signal interrupted live JSONL output for both `search` and `incident --demo`. Emitted lines remained valid JSON and both the finish record and the process returned 130. [Cancellation run](https://github.com/mildredanonymous/wtrail/actions/runs/45697357471)

## Runtime comparison

The CI benchmark runs both approaches five times on the same runner and checks that each returns the same event record and log line. Both receive the service name, time window, channel, provider, event ID and log directory. Timing covers the command sequence through result processing.

| Approach | Median | Range |
| --- | ---: | ---: |
| ripgrep 16.0.0 and Get-WinEvent | 55.2 ms | 46.3–70.4 ms |
| wtrail including startup and JSONL parsing | 211.1 ms | 201.8–224.2 ms |

For this small CI case, 300 ms is a provisional reference for tracking regressions. The benchmark uses a known log path and a small fixture. [Benchmark run](https://github.com/mildredanonymous/wtrail/actions/runs/45696059978)

## Open for stable release

- Test a real cloud placeholder and check for unwanted hydration.
- Measure peak memory and the time a person needs to locate both original results.
- Publish a stable Windows x64 package. The release candidate is currently distributed as an Actions artifact.
