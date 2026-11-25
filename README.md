# wtrail
A Windows CLI for incident investigation. Search application logs and Windows Event Logs together, trace matches to their original sources, and see which sources couldn’t be checked.

## Entwicklungsstand

Die synthetische Demo läuft mit dem .NET 10 SDK und braucht keinen installierten Testdienst.

```powershell
dotnet run -- incident Auftragsimport --demo complete
dotnet run -- incident Auftragsimport --demo partial
```

Unter Windows lassen sich Ereignisse und ein expliziter lokaler Logpfad gemeinsam prüfen.

```powershell
dotnet run -- incident 'Dienstname' --since 30m --logs 'C:\Anwendung\Logs' --file-glob '*.log'
```

`--logs` akzeptiert eine Datei oder ein rekursiv durchsuchtes Verzeichnis.
Der Standardfilter ist `*.log`. Logzeilen werden berücksichtigt, wenn sie mit
einem ISO-Zeitstempel beginnen und den internen Dienstnamen oder Anzeigenamen
enthalten. UTF-8 sowie UTF-16 mit BOM werden unterstützt. Erkennbare
Verknüpfungen und Offline-Platzhalter werden übersprungen und gemeldet.
Ohne `--logs` prüft wtrail im Windows-Modus eine gleichnamige XML-Datei neben der
EXE eines direkt konfigurierten Dienstes. Die XML muss zum internen Dienstnamen
passen und genau einen expliziten `<logpath>` enthalten. Relative Pfade und
`%BASE%` beziehen sich auf das Verzeichnis der XML-Datei. Wenn die Auflösung
scheitert, bleiben Ereignistreffer sichtbar und die Dateisuche erscheint als
ungeprüft. Ein expliziter `--logs`-Pfad hat Vorrang.

Die native Windows-Dienstauflösung, Ereigniskanäle und Windows-spezifischen
Pfadprüfungen stehen zur Abnahme aus.
