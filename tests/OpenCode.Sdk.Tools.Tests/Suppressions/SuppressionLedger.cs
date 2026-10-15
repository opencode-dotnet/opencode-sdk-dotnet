using System.Text;

namespace OpenCode.Sdk.Tools.Tests.Suppressions;

/// <summary>
/// Renders the suppression ledger: a total per channel, then one line per distinct suppression
/// (channel, declaring file, scope, rule, value), with a count when the same line repeats. The line
/// carries no line number, so editing around a suppression does not move the ledger; adding,
/// removing or changing one does.
/// </summary>
internal static class SuppressionLedger
{
    public static string Render(IEnumerable<Suppression> suppressions)
    {
        ArgumentNullException.ThrowIfNull(suppressions);

        var all = suppressions.ToList();
        var text = new StringBuilder()
            .Append("Suppression ledger: every channel that lowers, silences or switches off a diagnostic, counted.\n")
            .Append("A change that adds, removes or alters a suppression updates this file in the same commit.\n\n");
        foreach (var total in all.GroupBy(static suppression => Name(suppression.Channel), StringComparer.Ordinal).OrderBy(static group => group.Key, StringComparer.Ordinal))
        {
            text.Append(total.Key).Append(": ").Append(total.Count()).Append('\n');
        }

        text.Append('\n');
        foreach (var line in all.Select(Line).GroupBy(static line => line, StringComparer.Ordinal).OrderBy(static group => group.Key, StringComparer.Ordinal))
        {
            text.Append(line.Key);
            if (line.Count() > 1)
            {
                text.Append(" x").Append(line.Count());
            }

            text.Append('\n');
        }

        return text.ToString();
    }

    private static string Line(Suppression suppression)
    {
        var line = new StringBuilder(Name(suppression.Channel)).Append(' ').Append(suppression.Origin);
        if (suppression.Scope.Length > 0)
        {
            line.Append(" [").Append(suppression.Scope).Append(']');
        }

        line.Append(' ').Append(suppression.Rule);
        if (suppression.Value.Length > 0)
        {
            line.Append(" = ").Append(suppression.Value);
        }

        return line.ToString();
    }

    private static string Name(SuppressionChannel channel) => channel switch
    {
        SuppressionChannel.Pragma => "pragma",
        SuppressionChannel.NullableDirective => "nullable",
        SuppressionChannel.SuppressMessage => "suppress-message",
        SuppressionChannel.SlopwatchSuppress => "slopwatch-suppress",
        SuppressionChannel.SlopwatchIgnore => "slopwatch-ignore",
        SuppressionChannel.SlopwatchBaseline => "slopwatch-baseline",
        SuppressionChannel.EditorConfig => "editorconfig",
        SuppressionChannel.GlobalConfig => "globalconfig",
        SuppressionChannel.NoWarn => "no-warn",
        SuppressionChannel.WarningsNotAsErrors => "warnings-not-as-errors",
        SuppressionChannel.BuildSwitch => "build-switch",
        _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, "The ledger has no name for this channel."),
    };
}
