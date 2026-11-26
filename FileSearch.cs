using System.Threading;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

static class FileSearch
{
    internal static int Run(string[] args, Func<string, string> safeText, string help)
    {
        if (args is ["--help"])
        {
            Console.WriteLine(help);
            return 0;
        }

        var json = args.Zip(args.Skip(1)).Any(pair => pair.First == "--format" && pair.Second == "jsonl");
        long matches = 0;
        var checkedSources = false;
        var gaps = false;

        void Write(string type, object data, string text)
        {
            Console.WriteLine(json ? JsonSerializer.Serialize(new { version = 1, type, data }) : safeText(text));
            Console.Out.Flush();
        }

        int Finish(int code)
        {
            Write("finish", new { exitCode = code, matches }, $"Treffer: {matches} · Exitcode {code}");
            return code;
        }

        if (!TryParse(args, out var options))
        {
            if (!json)
            {
                Console.Error.WriteLine(help);
                return 2;
            }

            Write("start", new { command = "search" }, "");
            Write("source", new { status = "invalid_arguments" }, "");
            return Finish(2);
        }

        var (pattern, path, glob, context, regex, formatJson, verbose) = options;
        string? directory = null;
        json = formatJson;
        Write("start", new { command = "search", path, glob }, $"Dateisuche · Filter {glob}");
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, signal) =>
        {
            signal.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancel;
        try
        {
            LocalLogs.Walk(path, glob, source =>
            {
                checkedSources |= source.Checked;
                gaps |= !source.Checked;
                if (json || verbose || !source.Checked || source.Status.StartsWith("durch Dateifilter ", StringComparison.Ordinal))
                    Write("source", new { path = source.Name, source.Checked, source.Status },
                        $"{(source.Checked ? "  " : "Suchlücke · ")}{source.Name} — {source.Status}");
            }, (file, reader) =>
            {
                var before = new Queue<(long Number, string Text)>();
                long number = 0;
                long lastPrinted = 0;
                var after = 0;

                void Print(long lineNumber, string text, bool match)
                {
                    if (!json && !verbose && lastPrinted == 0)
                    {
                        Console.WriteLine();
                        Console.WriteLine(safeText(directory is null ? file : Path.GetRelativePath(directory, file)));
                    }

                    var display = verbose ? $"{file}:{lineNumber}{(match ? ":" : "-")} {text}"
                        : $"  {(match ? ">" : " ")} {lineNumber,6}  {text}";
                    Write("result", new { path = file, line = lineNumber, match, text }, display);
                    lastPrinted = lineNumber;
                }

                try
                {
                    while (reader.ReadLine() is { } line)
                    {
                        cancellation.Token.ThrowIfCancellationRequested();
                        number++;
                        var match = regex?.IsMatch(line) ?? line.Contains(pattern, StringComparison.Ordinal);
                        if (match)
                        {
                            matches++;
                            foreach (var previous in before)
                                if (previous.Number > lastPrinted)
                                    Print(previous.Number, previous.Text, false);
                            Print(number, line, true);
                            after = context;
                        }
                        else if (after > 0)
                        {
                            Print(number, line, false);
                            after--;
                        }

                        if (context > 0)
                        {
                            if (before.Count == context)
                                before.Dequeue();
                            before.Enqueue((number, line));
                        }
                    }
                }
                catch (RegexMatchTimeoutException error)
                {
                    throw new IOException("Zeitlimit für regulären Ausdruck überschritten.", error);
                }
            }, cancellation.Token, root =>
            {
                directory = root;
                if (!json)
                    Console.WriteLine(safeText($"Suchordner · {root}"));
            });

            cancellation.Token.ThrowIfCancellationRequested();
            return Finish(gaps ? checkedSources || matches > 0 ? 3 : 2 : matches > 0 ? 0 : 1);
        }
        catch (OperationCanceledException)
        {
            return Finish(130);
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
        }
    }

    private static bool TryParse(string[] args, out Options options)
    {
        options = new Options("", ".", "*", 0, null, false, false);
        var positional = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var regex = false;
        var literal = false;
        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            if (!literal && arg == "--")
            {
                literal = true;
                continue;
            }

            if (literal || !arg.StartsWith("--", StringComparison.Ordinal))
            {
                positional.Add(arg);
                continue;
            }

            if (!seen.Add(arg))
                return false;
            if (arg == "--regex")
            {
                regex = true;
                continue;
            }

            if (arg == "--verbose")
            {
                options = options with { Verbose = true };
                continue;
            }

            if (++index == args.Length)
                return false;
            var value = args[index];
            switch (arg)
            {
                case "--file-glob" when value.Length > 0 && value.IndexOfAny(['/', '\\', ':']) < 0:
                    options = options with { Glob = value };
                    break;
                case "--context" when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture,
                    out var count) && count is >= 0 and <= 1000:
                    options = options with { Context = count };
                    break;
                case "--format" when value == "jsonl":
                    options = options with { Json = true };
                    break;
                default:
                    return false;
            }
        }

        if (positional.Count is < 1 or > 2 || positional[0].Length == 0)
            return false;
        options = options with { Pattern = positional[0], Path = positional.Count == 2 ? positional[1] : "." };
        if (!regex)
            return true;
        try
        {
            options = options with { Regex = new Regex(options.Pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250)) };
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private sealed record Options(string Pattern, string Path, string Glob, int Context, Regex? Regex, bool Json, bool Verbose);
}
