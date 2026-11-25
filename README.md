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

## Dateisuche

```powershell
dotnet run -- search '[ERROR]' 'C:\Anwendung\Logs' --file-glob '*.log' --context 2
dotnet run -- search '^ERROR\b' 'C:\Anwendung\Logs' --regex --format jsonl
```

Die Suche ist standardmässig wörtlich, unterscheidet Gross- und Kleinschreibung
und beginnt ohne Pfadangabe im aktuellen Verzeichnis. `--` beendet die
Optionsverarbeitung, etwa für ein Suchmuster, das mit `--` beginnt.
`--context` akzeptiert 0 bis 1000 Zeilen je Seite. Quelldatensätze weisen
Dateifilter, übersprungene Verknüpfungen und Zugriffsfehler aus. Ein Regex, der
das Zeitlimit überschreitet, erzeugt eine Suchlücke für die betroffene Datei.

JSONL verwendet die Felder `version`, `type` und `data`. Der letzte Datensatz
enthält den Exitcode und die Anzahl Treffer. Bei einem verarbeiteten
`Ctrl+C` endet die Dateisuche mit Code 130.

## Vorfallausgabe

```powershell
dotnet run -- incident Auftragsimport --demo partial --order found --format jsonl
```

`incident` sortiert standardmässig nach Zeit. Dienstbezogene Fundstellen ohne
verlässliche Zeit erscheinen danach mit dem Hinweis «Zeit ungeklärt».
`--order found` gibt Fundstellen direkt in Fundreihenfolge aus.
`--format jsonl` verwendet die Felder `version`, `type` und `data` für Start,
Treffer, Quellenstatus und Abschluss. Bei verarbeitetem Abbruch meldet der
Abschluss Exitcode 130.
