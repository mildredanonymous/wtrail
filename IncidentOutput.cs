using System.Threading;
using System.Text.Json;

sealed class IncidentOutput(bool json, bool streaming, bool verbose, Func<string, string> safeText, CancellationToken cancellation)
{
    private readonly List<IncidentHit> pending = [];
    private readonly List<Source> sources = [];
    private string? logDirectory;
    private bool includeDate;
    private long matches;

    internal CancellationToken Cancellation => cancellation;

    internal void Start(string service, DateTimeOffset? from, DateTimeOffset? to, bool demo)
    {
        if (json)
        {
            Write("start", new { command = "incident", service, from, to, demo }, "");
            return;
        }

        if (from is not { } start || to is not { } end)
            return;

        start = start.ToUniversalTime();
        end = end.ToUniversalTime();
        includeDate = start.Date != end.Date;
        var title = demo ? $"Synthetischer Vorfall · {service}" : service;
        WriteText(title);
        WriteText(verbose
            ? $"Zeitraum (UTC) · [{start:O}, {end:O})"
            : $"Zeitraum (UTC) · [{start:yyyy-MM-dd HH:mm:ss.FFFFFFF}, {end:yyyy-MM-dd HH:mm:ss.FFFFFFF})");
    }

    internal void LogDirectory(string directory)
    {
        if (json)
            return;

        logDirectory = directory;
        WriteText($"Logordner · {directory}");
    }

    internal void Source(Source source)
    {
        if (json)
            Write("source", new { name = source.Name, source.Checked, source.Status }, "");
        else
            sources.Add(source);
    }

    internal void Error(string message, string[]? candidates = null)
    {
        if (json)
            Write("source", new { status = "error", message, candidates }, "");
        else
        {
            Console.Error.WriteLine(safeText(message));
            foreach (var candidate in candidates ?? [])
                Console.Error.WriteLine(safeText(candidate));
        }
    }

    internal void Result(IncidentHit hit)
    {
        cancellation.ThrowIfCancellationRequested();
        matches++;
        if (streaming)
            Print(hit);
        else
            pending.Add(hit);
    }

    internal int Finish(int code)
    {
        if (code != 130)
        {
            foreach (var hit in pending.OrderBy(hit => hit.Time is null).ThenBy(hit => hit.Time)
                         .ThenBy(hit => hit.Path ?? hit.Channel, StringComparer.Ordinal)
                         .ThenBy(hit => hit.Line ?? hit.RecordId))
            {
                if (cancellation.IsCancellationRequested)
                    break;
                Print(hit);
            }
            if (cancellation.IsCancellationRequested)
                code = 130;
        }

        if (!json && sources.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Quellen");
            foreach (var source in sources.OrderBy(source => source.Checked))
            {
                var name = DisplayPath(source.Name);
                var state = source.Checked ? "  Geprüft   " : "  Suchlücke ";
                if (source.Status.StartsWith("durch Dateifilter ", StringComparison.Ordinal))
                    state = "  Filter    ";
                WriteText($"{state} {name} · {source.Status}");
            }
        }

        var status = code switch
        {
            0 or 1 => "Suche abgeschlossen",
            3 => "Suche teilweise abgeschlossen",
            130 => "Suche abgebrochen",
            _ => "Suche fehlgeschlagen"
        };
        if (!json)
            Console.WriteLine();
        Write("finish", new { exitCode = code, matches }, $"{matches} Treffer · {status} · Exitcode {code}");
        return code;
    }

    private void Print(IncidentHit hit)
    {
        if (json)
        {
            Write("result", hit, "");
            return;
        }

        var location = hit.Path is null
            ? $"{hit.Channel} · Datensatz-ID {hit.RecordId}"
            : $"{DisplayPath(hit.Path)}:{hit.Line}";
        if (verbose && hit.EventId is { } id)
            location += $" · {hit.Provider} · Ereignis-ID {id}";

        var time = hit.Time is { } timestamp
            ? timestamp.ToUniversalTime().ToString(verbose ? "O" : includeDate ? "yyyy-MM-dd HH:mm:ss" : "HH:mm:ss")
            : "Zeit ungeklärt";
        var relation = hit.Direct ? "Diensttreffer" : "Zeitlicher Kontext";
        var kind = hit.Path is null ? "EVENT" : "LOG";
        Console.WriteLine();
        WriteText($"{time}  {kind,-5}  {hit.Message}");
        WriteText($"    {location} · {relation}");
    }

    private string DisplayPath(string path)
    {
        if (verbose || logDirectory is null || !Path.IsPathFullyQualified(path))
            return path;

        var relative = Path.GetRelativePath(logDirectory, path);
        return relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            ? path : relative;
    }

    private void WriteText(string text)
    {
        Console.WriteLine(safeText(text));
        Console.Out.Flush();
    }

    private void Write(string type, object data, string text)
    {
        if (json)
            Console.WriteLine(JsonSerializer.Serialize(new { version = 1, type, data }));
        else if (text.Length > 0)
            WriteText(text);
        Console.Out.Flush();
    }
}

sealed record IncidentHit(DateTimeOffset? Time, bool Direct, string Message, string? Channel = null,
    long? RecordId = null, string? Provider = null, int? EventId = null, string? Path = null, long? Line = null);
