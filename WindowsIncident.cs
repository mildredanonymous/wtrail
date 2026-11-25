using System.ComponentModel;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Runtime.Versioning;
using System.ServiceProcess;
using System.Xml.Linq;

static class WindowsIncident
{
    public static int Run(string service, string[] arguments, Func<string, string> safeText)
    {
        if (!TryParse(arguments, DateTimeOffset.UtcNow, out var options))
        {
            Console.Error.WriteLine("Erwartet: --since (z. B. 30m) oder --from und --to mit ISO-Zeit. Siehe wtrail --help.");
            return 2;
        }

        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Die lokale Dienst- und Ereignissuche benötigt Windows. Für Testdaten steht --demo zur Verfügung.");
            return 2;
        }

        return ReadWindows(service, options!, safeText);
    }

    [SupportedOSPlatform("windows")]
    private static int ReadWindows(string service, Options options, Func<string, string> safeText)
    {
        string serviceName;
        string displayName;
        try
        {
            var services = ServiceController.GetServices();
            try
            {
                var matches = services.Where(candidate =>
                    Matches(candidate.ServiceName, service) || Matches(candidate.DisplayName, service)).ToArray();
                if (matches.Length != 1)
                {
                    Console.Error.WriteLine(matches.Length == 0 ? "Dienst nicht gefunden." : "Dienstname mehrdeutig:");
                    foreach (var candidate in matches.OrderBy(candidate => candidate.ServiceName, StringComparer.OrdinalIgnoreCase))
                        Console.Error.WriteLine(safeText($"{candidate.ServiceName} ({candidate.DisplayName})"));
                    return 2;
                }

                serviceName = matches[0].ServiceName;
                displayName = matches[0].DisplayName;
            }
            finally
            {
                foreach (var candidate in services)
                    candidate.Dispose();
            }
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(safeText($"Dienstauflösung fehlgeschlagen: {error.Message}"));
            return 2;
        }

        Console.WriteLine(safeText($"{serviceName} ({displayName}) · [{options.From.UtcDateTime:O}, {options.To.UtcDateTime:O}) UTC"));
        var hits = new List<WindowsHit>();
        var sources = new List<Source>();
        var timeQuery = FormattableString.Invariant(
            $"*[System[TimeCreated[@SystemTime >= '{options.From.UtcDateTime:O}' and @SystemTime < '{options.To.UtcDateTime:O}']]]");
        foreach (var channel in options.Channels)
        {
            var incomplete = false;
            try
            {
                using var reader = new EventLogReader(new EventLogQuery(channel, PathType.LogName, timeQuery));
                while (reader.ReadEvent() is { } entry)
                {
                    using (entry)
                    {
                        if (options.Provider is { } provider && !Matches(entry.ProviderName, provider) ||
                            options.EventId is { } id && entry.Id != id)
                            continue;

                        var xml = XElement.Parse(entry.ToXml());
                        var ns = xml.Name.Namespace;
                        var timestamp = (string?)xml.Element(ns + "System")?.Element(ns + "TimeCreated")?.Attribute("SystemTime");
                        if (!TryTime(timestamp, out var time))
                        {
                            incomplete = true;
                            continue;
                        }

                        if (time < options.From || time >= options.To)
                            continue;

                        var fields = xml.Elements()
                            .Where(element => element.Name.LocalName is "EventData" or "UserData")
                            .SelectMany(element => element.Descendants().Where(field => !field.HasElements))
                            .Select(field => field.Value).ToArray();
                        var direct = fields.Any(field => Matches(field, serviceName) || Matches(field, displayName));
                        string? message;
                        try
                        {
                            message = entry.FormatDescription();
                        }
                        catch (EventLogException)
                        {
                            message = null;
                        }

                        incomplete |= string.IsNullOrWhiteSpace(message);
                        var details = string.IsNullOrWhiteSpace(message)
                            ? $"Meldungstext fehlt. Ereignisdaten: {string.Join(" | ", fields)}"
                            : message;
                        hits.Add(new WindowsHit(time, channel, entry.RecordId, entry.ProviderName, entry.Id, direct, details));
                    }
                }

                sources.Add(new Source(channel, !incomplete,
                    incomplete ? "teilweise geprüft (Zeit oder Meldungstext fehlt)" : "geprüft"));
            }
            catch (Exception error) when (error is EventLogException or UnauthorizedAccessException
                or System.Xml.XmlException)
            {
                sources.Add(new Source(channel, false, error.Message));
            }
        }

        var fileHits = new List<WindowsHit>();
        var logPath = options.Logs;
        if (logPath is null)
        {
            try
            {
                var configured = WinSwLogs.Resolve(serviceName);
                logPath = configured.Path;
                Console.WriteLine(safeText($"Logpfad aus WinSW-Konfiguration {configured.Configuration}: {logPath}"));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                System.Security.SecurityException or System.Xml.XmlException or ArgumentException or NotSupportedException)
            {
                sources.Add(new Source("Dateisuche", false, $"ungeprüft ({error.Message})"));
            }
        }

        if (logPath is not null)
        {
            LocalLogs.Read(logPath, options.FileGlob ?? "*.log", serviceName, displayName,
                options.From, options.To, sources, (time, location, message) =>
                    fileHits.Add(new WindowsHit(time, location, null, null, 0, true, message, IsFile: true)));
        }
        foreach (var hit in hits.Concat(fileHits).OrderBy(hit => hit.Time).ThenBy(hit => hit.Channel, StringComparer.Ordinal).ThenBy(hit => hit.RecordId))
        {
            if (hit.IsFile)
            {
                Console.WriteLine(safeText($"{hit.Time:O}  {hit.Channel}  Diensttreffer  {hit.Message}"));
                continue;
            }

            var relation = hit.Direct ? "Diensttreffer" : "Zeitlicher Kontext";
            Console.WriteLine(safeText(
                $"{hit.Time:O}  {hit.Channel}, Datensatz-ID {hit.RecordId}  {relation}  {hit.Provider}, Ereignis-ID {hit.Id}  {hit.Message}"));
        }

        foreach (var source in sources)
            Console.WriteLine(safeText($"{source.Name} — {source.Status}"));
        var found = hits.Count + fileHits.Count > 0;
        if (sources.Any(source => !source.Checked))
            return found || sources.Any(source => source.Checked) ? 3 : 2;
        return found ? 0 : 1;
    }

    private static bool Matches(string? left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool TryParse(string[] arguments, DateTimeOffset now, out Options? options)
    {
        options = null;
        var channels = new List<string>();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < arguments.Length; index += 2)
        {
            var key = arguments[index];
            if (index + 1 == arguments.Length || arguments[index + 1].StartsWith("--", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(arguments[index + 1]))
                return false;

            var value = arguments[index + 1];
            if (key == "--channel")
                channels.Add(value);
            else if (key is not ("--since" or "--from" or "--to" or "--provider" or "--event-id" or "--logs" or "--file-glob") ||
                     !values.TryAdd(key, value))
                return false;
        }

        DateTimeOffset from;
        DateTimeOffset to;
        if (values.TryGetValue("--since", out var since))
        {
            if (values.ContainsKey("--from") || values.ContainsKey("--to") || since.Length < 2 ||
                !int.TryParse(since.AsSpan(0, since.Length - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var amount) ||
                amount <= 0)
                return false;
            var seconds = since[^1] switch { 's' => 1, 'm' => 60, 'h' => 3600, 'd' => 86400, _ => 0 };
            if (seconds == 0 || (double)amount * seconds > (now - DateTimeOffset.MinValue).TotalSeconds)
                return false;
            from = now.AddSeconds(-(double)amount * seconds);
            to = now;
        }
        else if (!TryTime(values.GetValueOrDefault("--from"), out from) ||
                 !TryTime(values.GetValueOrDefault("--to"), out to) || from >= to)
            return false;

        int? eventId = null;
        if (values.TryGetValue("--event-id", out var id))
        {
            if (!int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedId))
                return false;
            eventId = parsedId;
        }

        if (channels.Count == 0)
            channels.AddRange(["System", "Application"]);
        var glob = values.GetValueOrDefault("--file-glob");
        if (glob is not null && (glob.IndexOfAny(['/', '\\', ':']) >= 0))
            return false;
        options = new Options(from, to, channels.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            values.GetValueOrDefault("--provider"), eventId, values.GetValueOrDefault("--logs"), glob);
        return true;
    }

    internal static bool TryTime(string? value, out DateTimeOffset time)
    {
        string[] formats = ["yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", "yyyy-MM-dd'T'HH:mmK"];
        time = default;
        if (!DateTime.TryParseExact(value, formats, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            return false;

        if (parsed.Kind == DateTimeKind.Unspecified &&
            (TimeZoneInfo.Local.IsAmbiguousTime(parsed) || TimeZoneInfo.Local.IsInvalidTime(parsed)))
            return false;
        if (!DateTimeOffset.TryParseExact(value, formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var offset))
            return false;
        time = offset.ToUniversalTime();
        return true;
    }

    private sealed record Options(DateTimeOffset From, DateTimeOffset To, string[] Channels, string? Provider, int? EventId,
        string? Logs, string? FileGlob);
    private sealed record WindowsHit(DateTimeOffset Time, string Channel, long? RecordId, string? Provider,
        int Id, bool Direct, string Message, bool IsFile = false);
}
