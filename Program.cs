using System.Threading;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

return Run(args);

static int Run(string[] args)
{
    const string help = """
        wtrail search PATTERN [PFAD] [--regex] [--file-glob MUSTER] [--context N] [--verbose] [--format jsonl]
        wtrail incident DIENST --since 30m [--channel KANAL] [--provider NAME] [--event-id ID] [--logs PFAD] [--file-glob MUSTER]
        wtrail incident DIENST --from ZEIT --to ZEIT [--channel KANAL] [--provider NAME] [--event-id ID] [--logs PFAD] [--file-glob MUSTER]
        wtrail incident Auftragsimport --demo complete|partial [--from UTC --to UTC]
        incident: [--order time|found] [--verbose] [--format jsonl]
        """;
    if (args is ["--help"] or ["incident", "--help"])
    {
        Console.WriteLine(help);
        return 0;
    }

    if (args.Length > 0 && args[0] == "search")
        return FileSearch.Run(args[1..], SafeText, help);

    if (args.Length > 0 && args[0] == "incident")
        return RunIncident(args[1..], help);
    Console.Error.WriteLine(help);
    return 2;
}

static int RunIncident(string[] args, string help)
{
    var remaining = new List<string>();
    var json = false;
    var streaming = false;
    var verbose = false;
    var valid = true;
    var seen = new HashSet<string>();
    if (args.Length > 0)
        remaining.Add(args[0]);
    for (var index = 1; index < args.Length;)
    {
        var key = args[index++];
        if (key == "--verbose")
        {
            valid &= seen.Add(key);
            verbose = true;
            continue;
        }
        if (index >= args.Length)
        {
            valid = false;
            break;
        }
        var value = args[index++];
        if (key is "--format" or "--order")
        {
            valid &= seen.Add(key);
            if (key == "--format")
            {
                json |= value == "jsonl";
                valid &= value == "jsonl";
            }
            else
            {
                streaming = value == "found";
                valid &= streaming || value == "time";
            }
            continue;
        }
        remaining.Add(key);
        remaining.Add(value);
    }

    var service = args.FirstOrDefault() ?? "";
    var demo = remaining.Count >= 2 && remaining[1] == "--demo";
    using var cancellation = new CancellationTokenSource();
    var output = new IncidentOutput(json, streaming, verbose, SafeText, cancellation.Token);
    ConsoleCancelEventHandler cancel = (_, signal) =>
    {
        signal.Cancel = true;
        cancellation.Cancel();
    };
    Console.CancelKeyPress += cancel;
    try
    {
        if (!valid || string.IsNullOrWhiteSpace(service) || service.StartsWith("--", StringComparison.Ordinal))
        {
            output.Start(service, null, null, demo);
            output.Error(help);
            return output.Finish(2);
        }
        var code = demo ? RunDemo(remaining.ToArray(), output, help)
            : WindowsIncident.Run(service, remaining.Skip(1).ToArray(), output);
        return output.Finish(code);
    }
    catch (OperationCanceledException)
    {
        return output.Finish(130);
    }
    finally
    {
        Console.CancelKeyPress -= cancel;
    }
}

static int RunDemo(string[] args, IncidentOutput output, string help)
{
    if (args.Length is not (3 or 7) ||
        !args[0].Equals("Auftragsimport", StringComparison.OrdinalIgnoreCase) ||
        args[1] != "--demo" || args[2] is not ("complete" or "partial"))
    {
        output.Start(args[0], null, null, true);
        output.Error(help);
        return 2;
    }

    var from = ParseTime("2026-01-15T09:14:00Z");
    var to = ParseTime("2026-01-15T09:16:00Z");
    if (args.Length == 7)
    {
        if (args[3] != "--from" || args[5] != "--to" ||
            ParseTime(args[4]) is not { } start || ParseTime(args[6]) is not { } end || start >= end)
        {
            output.Start(args[0], null, null, true);
            output.Error("Erwartet wird ein gültiges Zeitfenster in UTC, z. B. 2026-01-15T09:15:00Z.");
            return 2;
        }

        from = start;
        to = end;
    }

    output.Start(args[0], from, to, true);

    var root = Path.Combine(AppContext.BaseDirectory, "fixtures", "auftragsimport");
    output.LogDirectory(Path.Combine(root, "logs"));
    var count = 0;
    var sources = new List<Source>();
    try
    {
        using var data = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "events.json")));
        var scenario = data.RootElement.GetProperty("scenarios").GetProperty(args[2]);
        foreach (var channel in scenario.EnumerateObject())
        {
            sources.Add(channel.Value.GetString() == "checked"
                ? new Source(channel.Name, true, "geprüft (simuliert)")
                : new Source(channel.Name, false, "Zugriff verweigert (simuliert)"));
        }

        foreach (var entry in data.RootElement.GetProperty("events").EnumerateArray())
        {
            output.Cancellation.ThrowIfCancellationRequested();
            var channel = entry.GetProperty("channel").GetString();
            if (scenario.GetProperty(channel!).GetString() != "checked" ||
                !string.Equals(entry.GetProperty("serviceName").GetString(), args[0], StringComparison.OrdinalIgnoreCase))
                continue;

            var time = ParseTime(entry.GetProperty("timestamp").GetString()!);
            if (time is null || (time >= from && time < to))
            {
                var id = entry.GetProperty("recordId").GetInt64();
                output.Result(new IncidentHit(time, true, entry.GetProperty("message").GetString()!, channel, id));
                count++;
            }
        }
    }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
        or KeyNotFoundException or InvalidOperationException or FormatException)
    {
        sources.Clear();

        sources.Add(new Source("Ereignisdaten", false, error.Message));
    }

    var path = Path.Combine(root, "logs", "auftragsimport.log");
    try
    {
        var lineNumber = 0;
        foreach (var line in File.ReadLines(path))
        {
            output.Cancellation.ThrowIfCancellationRequested();
            lineNumber++;
            var separator = line.IndexOf(' ');
            if (separator > 0 && ParseTime(line[..separator]) is { } timestamp &&
                timestamp >= from && timestamp < to &&
                line.Contains(" ERROR ", StringComparison.Ordinal) &&
                line.Contains(args[0], StringComparison.OrdinalIgnoreCase))
            {
                output.Result(new IncidentHit(timestamp, true, line[(separator + 1)..], Path: path, Line: lineNumber));
                count++;
            }
        }

        sources.Add(new Source(path, true, "geprüft"));
    }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
    {
        sources.Add(new Source(path, false, error.Message));
    }

    foreach (var source in sources)
        output.Source(source);

    if (!sources.Any(source => source.Checked) && count == 0)
        return 2;
    if (sources.Any(source => !source.Checked))
        return 3;
    return count == 0 ? 1 : 0;
}

static DateTimeOffset? ParseTime(string value) =>
    DateTimeOffset.TryParseExact(value, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var result) ? result : null;

static string SafeText(string value) =>
    Regex.Replace(value, @"[\p{Cc}\p{Cf}\p{Zl}\p{Zp}]", match => $"\\u{(int)match.Value[0]:X4}");

record Source(string Name, bool Checked, string Status);
