using System.Globalization;
using System.Text;

namespace OpenCode.Sdk.Internal;

/// <summary>The pure JSON.stringify byte policy for decoded service configuration.</summary>
internal static class ServiceConfigJsonWriter
{
    /// <summary>Writes schema/merge order with two-space indentation and a final newline.</summary>
    /// <param name="document">The decoded and merged configuration.</param>
    /// <returns>The UTF-8 file content, without a byte-order mark.</returns>
    public static byte[] Write(ServiceConfigDocument document) =>
        Encoding.UTF8.GetBytes(Container("{", "}", document.MemberNames.Select(name =>
            "  " + WriteString(name) + ": " + Value(document, name)), 0) + "\n");

    /// <summary>MemberNames proves each value present; a repaired remote has a route rather than a legacy boolean.</summary>
    private static string Value(ServiceConfigDocument document, string name) => name switch
    {
        "disabled" => document.Disabled == true ? "true" : "false",
        "remote" => "{\n    \"route\": " + WriteString(document.RemoteRoute!) + "\n  }",
        "remoteAccepted" => "true",
        "hostname" => WriteString(document.Hostname!),
        "port" => document.Port!.Value.ToString(CultureInfo.InvariantCulture),
        "password" => WriteString(document.Password!),
        "cors" => Container("[", "]", document.CorsOrigins!.Select(static value => "    " + WriteString(value)), 2),
        "env" => WriteEnvironment(document.Environment!),
        _ => throw new InvalidOperationException("Unknown service-config member."),
    };

    private static string Container(string open, string close, IEnumerable<string> entries, int indentation)
    {
        var values = entries.ToArray();
        return values.Length == 0 ? open + close : open + "\n" + string.Join(",\n", values) + "\n" + new string(' ', indentation) + close;
    }

    private static string WriteEnvironment(IReadOnlyDictionary<string, string> environment) =>
        Container("{", "}", environment.OrderBy(static entry => ArrayIndex(entry.Key)).Select(static entry =>
            "    " + WriteString(entry.Key) + ": " + WriteString(entry.Value)), 2);

    private static uint ArrayIndex(string name) =>
        uint.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
        && index != uint.MaxValue && string.Equals(index.ToString(CultureInfo.InvariantCulture), name, StringComparison.Ordinal)
            ? index : uint.MaxValue;

    private static string WriteString(string value)
    {
        var text = new StringBuilder("\"");
        var index = 0;
        while (index < value.Length)
        {
            var character = value[index++];
            var escape = character switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\b' => "\\b",
                '\f' => "\\f",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ => null,
            };
            if (escape is not null)
            {
                _ = text.Append(escape);
            }
            else if (char.IsHighSurrogate(character) && index < value.Length && char.IsLowSurrogate(value[index]))
            {
                _ = text.Append(character).Append(value[index++]);
            }
            else if (character < ' ' || char.IsSurrogate(character))
            {
                _ = text.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
            }
            else
            {
                _ = text.Append(character);
            }
        }

        return text.Append('"').ToString();
    }
}
