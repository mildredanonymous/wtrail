using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

return Run(args);

static int Run(string[] args)
{
    const string help = """
        wtrail incident DIENST --since 30m [--channel KANAL] [--provider NAME] [--event-id ID] [--logs PFAD] [--file-glob MUSTER]
        wtrail incident DIENST --from ZEIT --to ZEIT [--channel KANAL] [--provider NAME] [--event-id ID] [--logs PFAD] [--file-glob MUSTER]
        wtrail incident Auftragsimport --demo complete|partial [--from UTC --to UTC]
        """;
    if (args is ["--help"] or ["incident", "--help"])
    {
        Console.WriteLine(help);
        return 0;
    }

    if (args.Length >= 2 && args[0] == "incident" && !args[1].StartsWith("--", StringComparison.Ordinal)
        && (args.Length < 3 || args[2] != "--demo"))
        return WindowsIncident.Run(args[1], args[2..], SafeText);

    if (args.Length is not (4 or 8) || args[0] != "incident" ||
        !args[1].Equals("Auftragsimport", StringComparison.OrdinalIgnoreCase) ||
        args[2] != "--demo" || args[3] is not ("complete" or "partial"))
    {
        Console.Error.WriteLine(help);
        return 2;
    }

    var from = ParseTime("2026-01-15T09:14:00Z");
    var to = ParseTime("2026-01-15T09:16:00Z");
    if (args.Length == 8)
    {
        if (args[4] != "--from" || args[6] != "--to" ||
            ParseTime(args[5]) is not { } start || ParseTime(args[7]) is not { } end || start >= end)
        {
            Console.Error.WriteLine("Erwartet wird ein gültiges Zeitfenster in UTC, z. B. 2026-01-15T09:15:00Z.");
            return 2;
        }

        from = start;
        to = end;
    }

    Console.WriteLine($"Synthetischer Vorfall Auftragsimport · [{from:O}, {to:O})");

    var root = Path.Combine(AppContext.BaseDirectory, "fixtures", "auftragsimport");
    var hits = new List<Hit>();
    var sources = new List<Source>();
    try
    {
        using var data = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "events.json")));
        var scenario = data.RootElement.GetProperty("scenarios").GetProperty(args[3]);
        foreach (var channel in scenario.EnumerateObject())
        {
            sources.Add(channel.Value.GetString() == "checked"
                ? new Source(channel.Name, true, "geprüft (simuliert)")
                : new Source(channel.Name, false, "Zugriff verweigert (simuliert)"));
        }

        foreach (var entry in data.RootElement.GetProperty("events").EnumerateArray())
        {
            var channel = entry.GetProperty("channel").GetString();
            if (scenario.GetProperty(channel!).GetString() != "checked" ||
                !string.Equals(entry.GetProperty("serviceName").GetString(), args[1], StringComparison.OrdinalIgnoreCase))
                continue;

            var time = ParseTime(entry.GetProperty("timestamp").GetString()!);
            if (time is { } timestamp && timestamp >= from && timestamp < to)
            {
                var id = entry.GetProperty("recordId").GetInt64();
                hits.Add(new Hit(timestamp, $"{channel}, Datensatz-ID {id}", entry.GetProperty("message").GetString()!));
            }
        }
    }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
        or KeyNotFoundException or InvalidOperationException or FormatException)
    {
        sources.Clear();
        hits.Clear();
        sources.Add(new Source("Ereignisdaten", false, error.Message));
    }

    var path = Path.Combine(root, "logs", "auftragsimport.log");
    try
    {
        var lineNumber = 0;
        foreach (var line in File.ReadLines(path))
        {
            lineNumber++;
            var separator = line.IndexOf(' ');
            if (separator > 0 && ParseTime(line[..separator]) is { } timestamp &&
                timestamp >= from && timestamp < to &&
                line.Contains(" ERROR ", StringComparison.Ordinal) &&
                line.Contains(args[1], StringComparison.OrdinalIgnoreCase))
            {
                hits.Add(new Hit(timestamp, $"{path}:{lineNumber}", line[(separator + 1)..]));
            }
        }

        sources.Add(new Source(path, true, "geprüft"));
    }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
    {
        sources.Add(new Source(path, false, error.Message));
    }

    foreach (var hit in hits.OrderBy(hit => hit.Time))
        Console.WriteLine(SafeText($"{hit.Time:O}  {hit.Location}  {hit.Message}"));

    foreach (var source in sources)
        Console.WriteLine(SafeText($"{source.Name} — {source.Status}"));

    Console.WriteLine($"Treffer: {hits.Count}");
    if (!sources.Any(source => source.Checked) && hits.Count == 0)
        return 2;
    if (sources.Any(source => !source.Checked))
        return 3;
    return hits.Count == 0 ? 1 : 0;
}

static DateTimeOffset? ParseTime(string value) =>
    DateTimeOffset.TryParseExact(value, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var result) ? result : null;

static string SafeText(string value) =>
    Regex.Replace(value, @"[\p{Cc}\p{Cf}\p{Zl}\p{Zp}]", match => $"\\u{(int)match.Value[0]:X4}");

record Hit(DateTimeOffset Time, string Location, string Message);
record Source(string Name, bool Checked, string Status);
