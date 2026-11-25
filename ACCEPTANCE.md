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
