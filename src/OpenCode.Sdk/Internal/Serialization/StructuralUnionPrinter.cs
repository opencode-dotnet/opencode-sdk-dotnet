using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// Formats a structural union for its record's <c>ToString()</c>. A union's inactive arm
/// properties throw by design, and the compiler-synthesized <c>ToString()</c> would read every
/// one of them; the generated override hands this printer the type name, the kind, and the boxed
/// active value instead, so a union renders in the record shape,
/// <c>ProviderSettingsTimeout { Kind = Number, Number = 30000 }</c>. Numbers print with the
/// invariant culture (a log line must not depend on the current culture), a raw JSON payload as
/// its text, a list as <c>[item, item]</c>.
/// </summary>
internal static class StructuralUnionPrinter
{
    /// <summary>
    /// Renders <c>&lt;typeName&gt; { Kind = &lt;arm&gt;, &lt;arm&gt; = &lt;value&gt; }</c>.
    /// </summary>
    public static string Format(string typeName, string armName, object value)
    {
        ArgumentNullException.ThrowIfNull(typeName);
        ArgumentNullException.ThrowIfNull(armName);
        ArgumentNullException.ThrowIfNull(value);

        var builder = new StringBuilder();
        _ = builder.Append(typeName).Append(" { Kind = ").Append(armName).Append(", ").Append(armName).Append(" = ");
        Append(builder, value);
        return builder.Append(" }").ToString();
    }

    private static void Append(StringBuilder builder, object? value)
    {
        switch (value)
        {
            case null:
                return;
            case string text:
                _ = builder.Append(text);
                return;
            case JsonElement element:
                _ = builder.Append(element.GetRawText());
                return;
            case IFormattable formattable:
                _ = builder.Append(formattable.ToString(format: null, CultureInfo.InvariantCulture));
                return;
            case IEnumerable items:
                AppendList(builder, items);
                return;
            default:
                _ = builder.Append(value);
                return;
        }
    }

    private static void AppendList(StringBuilder builder, IEnumerable items)
    {
        _ = builder.Append('[');
        var first = true;
        foreach (var item in items)
        {
            if (!first)
            {
                _ = builder.Append(", ");
            }

            first = false;
            Append(builder, item);
        }

        _ = builder.Append(']');
    }
}
