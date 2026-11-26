using System.Threading;
using System.IO.Enumeration;
using System.Text;

static class LocalLogs
{
    private const FileAttributes UnsafeAttributes =
        FileAttributes.ReparsePoint | FileAttributes.Offline | FileAttributes.Device |
        (FileAttributes)0x40000 | (FileAttributes)0x400000; // Recall on open / data access.

    internal static void Read(string input, string glob, string serviceName, string displayName,
        DateTimeOffset from, DateTimeOffset to, List<Source> sources,
        Action<IncidentHit> emit, CancellationToken cancellation, Action<string>? onDirectory = null)
    {
        Walk(input, glob, sources.Add, (file, reader) =>
        {
            long lineNumber = 0;
            while (reader.ReadLine() is { } line)
            {
                cancellation.ThrowIfCancellationRequested();
                lineNumber++;
                var separator = line.IndexOfAny([' ', '\t']);
                if (!line.Contains(serviceName, StringComparison.OrdinalIgnoreCase) &&
                    !line.Contains(displayName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (separator <= 0 || !WindowsIncident.TryTime(line[..separator], out var time))
                    emit(new IncidentHit(null, true, line, Path: file, Line: lineNumber));
                else if (time >= from && time < to)
                    emit(new IncidentHit(time, true, line[(separator + 1)..], Path: file, Line: lineNumber));
            }
        }, cancellation, onDirectory);
    }

    internal static void Walk(string input, string glob, Action<Source> report,
        Action<string, StreamReader> scan, CancellationToken cancellation = default, Action<string>? onDirectory = null)
    {
        try
        {
            cancellation.ThrowIfCancellationRequested();
            var (path, attributes) = ResolveLocal(input);

            onDirectory?.Invoke(attributes.HasFlag(FileAttributes.Directory) ? path : Path.GetDirectoryName(path)!);
            if (attributes.HasFlag(FileAttributes.Directory))
                ReadDirectory(path);
            else
                ReadFile(path);
        }
        catch (Exception error) when (IsPathError(error) || error is ArgumentException or NotSupportedException)
        {
            report(new Source(input, false, error.Message));
        }

        void ReadDirectory(string start)
        {
            var pending = new Stack<string>();
            pending.Push(start);
            while (pending.TryPop(out var directory))
            {
                try
                {
                    CheckAttributes(directory);
                    foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                    {
                        cancellation.ThrowIfCancellationRequested();
                        try
                        {
                            var attributes = CheckAttributes(entry);
                            if (attributes.HasFlag(FileAttributes.Directory))
                                pending.Push(entry);
                            else
                                ReadFile(entry);
                        }
                        catch (Exception error) when (IsPathError(error))
                        {
                            report(new Source(entry, false, error.Message));
                        }
                    }

                    report(new Source(directory, true, "Verzeichnis geprüft"));
                }
                catch (Exception error) when (IsPathError(error))
                {
                    report(new Source(directory, false, error.Message));
                }
            }
        }

        void ReadFile(string file)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!FileSystemName.MatchesSimpleExpression(glob, Path.GetFileName(file), ignoreCase: true))
            {
                report(new Source(file, true, $"durch Dateifilter {glob} ausgeschlossen"));
                return;
            }

            CheckAttributes(file);
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            scan(file, reader);
            report(new Source(file, true, "Datei geprüft"));
        }
    }

    internal static (string Path, FileAttributes Attributes) ResolveLocal(string input)
    {
        if (OperatingSystem.IsWindows() &&
            (input.StartsWith(@"\\") || input.StartsWith("//")))
            throw new IOException("Netzwerk- und Gerätepfade werden übersprungen.");

        var path = Path.GetFullPath(input);
        var root = Path.GetPathRoot(path)!;
        if (OperatingSystem.IsWindows() &&
            (root.Length != 3 || root[1] != ':' || path.AsSpan(2).Contains(':') ||
             new DriveInfo(root).DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Ram)))
            throw new IOException("Erwartet wird ein Pfad auf einem lokalen Dateisystem.");

        var segmentPath = root;
        var attributes = CheckAttributes(segmentPath);
        foreach (var segment in path[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            segmentPath = Path.Combine(segmentPath, segment);
            attributes = CheckAttributes(segmentPath);
        }

        return (path, attributes);
    }

    private static FileAttributes CheckAttributes(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & UnsafeAttributes) != 0)
            throw new IOException("Verknüpfung, Gerät oder Offline-Platzhalter übersprungen.");
        return attributes;
    }

    private static bool IsPathError(Exception error) =>
        error is IOException or UnauthorizedAccessException;
}
