using System.ComponentModel;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Runtime.Versioning;
using System.ServiceProcess;
using System.Xml.Linq;

static class WindowsIncident
{
    public static int Run(string service, string[] arguments, IncidentOutput output)
    {
        if (!TryParse(arguments, DateTimeOffset.UtcNow, out var options))
        {
            output.Start(service, null, null, false);
            output.Error("Erwartet: --since (z. B. 30m) oder --from und --to mit ISO-Zeit. Siehe wtrail --help.");
            return 2;
        }

        output.Start(service, options!.From, options.To, false);
        if (!OperatingSystem.IsWindows())
        {
            output.Error("Die lokale Dienst- und Ereignissuche benötigt Windows. Für Testdaten steht --demo zur Verfügung.");
            return 2;
        }

        return ReadWindows(service, options, output);
    }

    [SupportedOSPlatform("windows")]
    private static int ReadWindows(string service, Options options, IncidentOutput output)
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
                    output.Error(matches.Length == 0 ? "Dienst nicht gefunden." : "Dienstname mehrdeutig:",
                        matches.OrderBy(candidate => candidate.ServiceName, StringComparer.OrdinalIgnoreCase)
                            .Select(candidate => $"{candidate.ServiceName} ({candidate.DisplayName})").ToArray());
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
            output.Error($"Dienstauflösung fehlgeschlagen: {error.Message}");
            return 2;
        }

        output.Source(new Source(serviceName, true, $"Dienst aufgelöst ({displayName})"));
        var count = 0;
        var sources = new List<Source>();

        void Emit(IncidentHit hit)
        {
            output.Result(hit);
            count++;
        }

        var timeQuery = FormattableString.Invariant(
            $"*[System[TimeCreated[@SystemTime >= '{options.From.UtcDateTime:O}' and @SystemTime < '{options.To.UtcDateTime:O}']]]");
        foreach (var channel in options.Channels)
        {
            output.Cancellation.ThrowIfCancellationRequested();
            var incomplete = false;
            try
            {
                using var reader = new EventLogReader(new EventLogQuery(channel, PathType.LogName, timeQuery));
                while (reader.ReadEvent() is { } entry)
                {
                    using (entry)
                    {
                        output.Cancellation.ThrowIfCancellationRequested();
                        if (options.Provider is { } provider && !Matches(entry.ProviderName, provider) ||
                            options.EventId is { } id && entry.Id != id)
                            continue;

                        var xml = XElement.Parse(entry.ToXml());
                        var ns = xml.Name.Namespace;
                        var timestamp = (string?)xml.Element(ns + "System")?.Element(ns + "TimeCreated")?.Attribute("SystemTime");
                        DateTimeOffset? time = TryTime(timestamp, out var parsedTime) ? parsedTime : null;
                        incomplete |= time is null;
                        if (time is { } reliable && (reliable < options.From || reliable >= options.To))
                            continue;

                        var fields = xml.Elements()
                            .Where(element => element.Name.LocalName is "EventData" or "UserData")
                            .SelectMany(element => element.Descendants().Where(field => !field.HasElements))
                            .Select(field => field.Value).ToArray();
                        var direct = fields.Any(field => Matches(field, serviceName) || Matches(field, displayName));
                        if (time is null && !direct)
                            continue;
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
                        Emit(new IncidentHit(time, direct, details, channel, entry.RecordId, entry.ProviderName, entry.Id));
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

        var logPath = options.Logs;
        if (logPath is null)
        {
            try
            {
                var configured = WinSwLogs.Resolve(serviceName);
                logPath = configured.Path;
                output.Source(new Source(configured.Configuration, true, $"Logpfad aus WinSW-Konfiguration: {logPath}"));
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
                options.From, options.To, sources, Emit, output.Cancellation);
        }
        foreach (var source in sources)
            output.Source(source);
        var found = count > 0;
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
}
