using System.Threading;
using System.Text.Json;

sealed class IncidentOutput(bool json, bool streaming, Func<string, string> safeText, CancellationToken cancellation)
{
    private readonly List<IncidentHit> pending = [];
    private long matches;

    internal CancellationToken Cancellation => cancellation;

    internal void Start(string service, DateTimeOffset? from, DateTimeOffset? to, bool demo) =>
        Write("start", new { command = "incident", service, from, to, demo },
            from is null ? "" : $"{(demo ? "Synthetischer Vorfall " : "")}{service} · [{from:O}, {to:O})");

    internal void Source(Source source) =>
        Write("source", new { name = source.Name, source.Checked, source.Status }, $"{source.Name} — {source.Status}");

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

        Write("finish", new { exitCode = code, matches }, $"Treffer: {matches} · Exitcode {code}");
        return code;
    }

    private void Print(IncidentHit hit)
    {
        var location = hit.Path is null ? $"{hit.Channel}, Datensatz-ID {hit.RecordId}" : $"{hit.Path}:{hit.Line}";
        var time = hit.Time is { } timestamp ? timestamp.ToString("O") : "Zeit ungeklärt";
        var relation = hit.Direct ? "Diensttreffer" : "Zeitlicher Kontext";
        var provider = hit.EventId is { } id ? $"{hit.Provider}, Ereignis-ID {id}  " : "";
        Write("result", hit, $"{time}  {location}  {relation}  {provider}{hit.Message}");
    }

    private void Write(string type, object data, string text)
    {
        if (json)
            Console.WriteLine(JsonSerializer.Serialize(new { version = 1, type, data }));
        else if (text.Length > 0)
            Console.WriteLine(safeText(text));
        Console.Out.Flush();
    }
}

sealed record IncidentHit(DateTimeOffset? Time, bool Direct, string Message, string? Channel = null,
    long? RecordId = null, string? Provider = null, int? EventId = null, string? Path = null, long? Line = null);
