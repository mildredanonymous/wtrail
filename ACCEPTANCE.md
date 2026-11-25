# Windows-Abnahme

## Windows 11 x64, lokal gebauter RC 1

Der Anwender hat den Build aus dem geklonten Repository erstellt und auf
Windows 11 ausgeführt. Die genaue Windows-Buildnummer und PowerShell-Version
dieses Laufs sind nicht erfasst.

| Fall | Beobachtung |
|---|---|
| Vollständige Demo | Logzeile 2 und simuliertes System-Ereignis 4201 in Zeitreihenfolge, Exitcode 0 |
| Zugriffsfehler in der Demo | Beide Fundstellen bleiben sichtbar, Application wird als unzugänglich gemeldet, Exitcode 3 |
| Echter Kanal System | Dienst EventLog aufgelöst, System geprüft, keine Treffer im 15-Minuten-Fenster, Dateisuche ungeprüft, Exitcode 3 |

[Windows-Server-Abnahme des RC 1](https://github.com/mildredanonymous/wtrail/actions/runs/45692891283)
lief unter Windows Server 2025 Build 26100 und PowerShell 7.6.10. Sie fand ein
echtes Ereignis und die zugehörige Zeile in einer UTF-16-Datei.

## Vergleichsmessung

Die CI führt fünf Wiederholungen für denselben Testvorfall auf demselben
Runner aus. Beide Varianten erhalten Dienstname, Zeitfenster, Ereigniskanal,
Provider, Ereignis-ID und Logverzeichnis. Die Messung umfasst den jeweiligen
integrierten Aufruf bis zur Auswertung der Resultate und prüft die beiden
Originalfundstellen. Das ist eine Maschinenmessung für den vorbereiteten
Kernfall. Angaben zur menschlichen Bedienzeit und zum Spitzenverbrauch des
Arbeitsspeichers stehen aus.

### Ergebnisse

[Windows-CI mit ripgrep 16.0.0](https://github.com/mildredanonymous/wtrail/actions/runs/45696059978)
bestanden. Eine exklusiv gesperrte Datei wurde als Suchlücke gemeldet. Bei
fehlendem Logpfad blieb der echte Ereignistreffer sichtbar.

| Messreihe | Median | Spannweite |
|---|---:|---:|
| rg und Get-WinEvent | 55,2 ms | 46,3–70,4 ms |
| wtrail inklusive Start und JSONL-Auswertung | 211,1 ms | 201,8–224,2 ms |

Für diesen kleinen CI-Fall ist 300 ms ein vorläufiger Richtwert zur
Beobachtung von Regressionen. Die Messung sagt nichts über grössere Logs,
unbekannte Logpfade oder die Zeit aus, die eine Person zum Auffinden der
beiden Quellen benötigt.

## Weitere Windows-Prüfungen

[Windows-CI für Pfade und WinSW](https://github.com/mildredanonymous/wtrail/actions/runs/45696561360)
bestanden. Die rekursive Suche fand eine Datei hinter der 260-Zeichen-Grenze.
Eine lokale Datei mit Offline-Attribut wurde vor dem Lesen übersprungen.
Für einen temporär registrierten, deaktivierten Testdienst fand wtrail die
WinSW-kompatible XML neben dem konfigurierten EXE-Pfad, löste `%BASE%` auf und
meldete die Konfigurationsquelle. `--logs` hatte Vorrang. Der Dienst wurde
für die Prüfung der Konfiguration nicht gestartet.

[Windows-CI für Ctrl+C](https://github.com/mildredanonymous/wtrail/actions/runs/45697357471)
bestanden. Ein natives Windows-Konsolensignal wurde während der laufenden
JSONL-Ausgabe an `search` und an `incident --demo` gesendet. In beiden Fällen
blieben alle Zeilen gültiges JSON und der Abschluss sowie der Prozess
meldeten Exitcode 130. Die interaktive Tastatureingabe in einem
Windows-11-PowerShell-Fenster wurde dabei nicht geprüft.

Vor der stabilen V1 stehen die Messung von Speicherbedarf und Bedienzeit
sowie die Abnahme mit einem echten Cloud-Platzhalter aus.
