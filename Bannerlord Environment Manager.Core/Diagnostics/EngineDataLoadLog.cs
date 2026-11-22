using System.Text;
using System.Text.RegularExpressions;

namespace BannerlordEnvironmentManager.Core.Diagnostics;

// What an engine run log says about module data that failed to load: each ModuleData xml the engine opened
// and the schema validation errors it logged under that file, and every "Null object reference found with
// ID: x" line, which is the engine saying an object the game expects was never registered. Read line by
// line while the log is streamed, so a multi-megabyte log is kept as a few synthetic lines:
//   [XmlSchemaErrors] Module|file.xml|Schema.xsd|count|first message
// followed by the Null object reference lines as written (EngineDataLoadFailures reads both).
public sealed partial class EngineDataLoadLog
{
    public const string SchemaMarker = "[XmlSchemaErrors] ";

    public const string NullMarker = "Null object reference found with ID:";

    [GeneratedRegex(@"opening \.\.[\\/]\.\.[\\/]Modules[\\/](?<module>[^\\/]+)[\\/]ModuleData[\\/](?<file>.+?)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex ModuleFile { get; }

    [GeneratedRegex(@"opening \.\.[\\/]\.\.[\\/]XmlSchemas[\\/](?<xsd>[^\\/\s]+\.xsd)", RegexOptions.CultureInvariant)]
    private static partial Regex Schema { get; }

    [GeneratedRegex(@"\] Error: (?<message>.+?)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex ErrorLine { get; }

    private string _module = string.Empty;
    private string _file = string.Empty;
    private string _xsd = string.Empty;
    private int _errors;
    private string _first = string.Empty;

    // Appends what this line contributes to text and returns whether the line was of this kind.
    public bool Feed(string line, StringBuilder text)
    {
        if (line.Contains(NullMarker, StringComparison.Ordinal))
        {
            Flush(text);
            text.AppendLine(line.Length > 300 ? line[..300] : line);
            return true;
        }

        if (ModuleFile.Match(line) is { Success: true } file)
        {
            Flush(text);
            _module = file.Groups["module"].Value;
            _file = file.Groups["file"].Value;
            return true;
        }

        if (_module.Length > 0 && Schema.Match(line) is { Success: true } xsd)
        {
            _xsd = xsd.Groups["xsd"].Value;
            return true;
        }

        if (line.Contains("] opening ", StringComparison.Ordinal))
        {
            Flush(text);
            return false;
        }

        if (_module.Length > 0 && ErrorLine.Match(line) is { Success: true } error)
        {
            if (_errors++ == 0)
                _first = error.Groups["message"].Value.Replace('|', '/');

            return true;
        }

        return false;
    }

    public void Flush(StringBuilder text)
    {
        if (_errors > 0 && _module.Length > 0)
        {
            var first = _first.Length > 200 ? _first[..200] : _first;
            text.AppendLine($"{SchemaMarker}{_module}|{_file}|{_xsd}|{_errors}|{first}");
        }

        _module = _file = _xsd = _first = string.Empty;
        _errors = 0;
    }
}
