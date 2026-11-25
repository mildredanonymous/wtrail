using Microsoft.Win32;
using System.Runtime.Versioning;
using System.Xml;
using System.Xml.Linq;

static class WinSwLogs
{
    [SupportedOSPlatform("windows")]
    internal static (string Path, string Configuration) Resolve(string serviceName)
    {
        using var service = Registry.LocalMachine.OpenSubKey(
            $@"SYSTEM\CurrentControlSet\Services\{serviceName}", writable: false);
        var command = (service?.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames)
            as string)?.Trim() ?? "";
        var executable = command.Trim('"');
        if (!Path.IsPathFullyQualified(executable) || executable.Contains('"') || executable.Contains('%') ||
            !Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
            (!command.StartsWith('"') && executable.Any(char.IsWhiteSpace)))
            throw new IOException("Kein unterstützter lokaler WinSW-Dienststartpfad.");

        var (configuration, attributes) = LocalLogs.ResolveLocal(Path.ChangeExtension(executable, ".xml"));
        if (attributes.HasFlag(FileAttributes.Directory))
            throw new IOException("Die erwartete WinSW-Konfiguration ist keine Datei.");

        using var stream = File.OpenRead(configuration);
        return (ReadLogPath(stream, Path.GetDirectoryName(configuration)!, serviceName), configuration);
    }

    internal static string ReadLogPath(Stream stream, string directory, string serviceName)
    {
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 1_048_576
        });
        var root = XDocument.Load(reader).Root;
        var paths = root?.Elements("logpath").ToArray();
        if (root?.Name != "service" || !string.Equals((string?)root.Element("id"), serviceName,
                StringComparison.OrdinalIgnoreCase) || paths is not { Length: 1 } ||
            paths[0].HasElements || string.IsNullOrWhiteSpace(paths[0].Value))
            throw new IOException("Keine eindeutige WinSW-Konfiguration mit explizitem logpath für diesen Dienst.");

        var path = paths[0].Value.Trim().Replace("%BASE%", directory, StringComparison.OrdinalIgnoreCase);
        if (path.Contains('%') || (Path.IsPathRooted(path) && !Path.IsPathFullyQualified(path)))
            throw new IOException("Der WinSW-Logpfad enthält eine nicht unterstützte Pfadangabe.");

        return Path.GetFullPath(path, directory);
    }
}
