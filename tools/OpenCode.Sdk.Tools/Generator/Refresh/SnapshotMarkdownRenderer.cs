using System.Globalization;
using System.Text.RegularExpressions;
using OpenCode.Sdk.Tools.Generator.Refresh.Models;

namespace OpenCode.Sdk.Tools.Generator.Refresh;

/// <summary>Validates managed identity text before returning replacements, leaving other prose intact.</summary>
internal static partial class SnapshotMarkdownRenderer
{
    private static readonly string[] ManagedRows = ["Upstream release tag", "Commit", "Upstream product channel"];

    public static string Render(string text, SnapshotReceipt receipt, DateOnly date)
    {
        var lines = text.Split('\n');
        var invalidRow = ManagedRows
            .FirstOrDefault(row => lines.Count(line => line.StartsWith($"| {row} |", StringComparison.Ordinal)) is not 1);
        if (invalidRow is not null || IdentityTableRegex().Count(text) is not 1 || DateLineRegex().Count(text) is not 1 || PinProseRegex().Count(text) is not 1)
        {
            var detail = invalidRow is null ? string.Empty : $" (managed '{invalidRow}' row must occur exactly once)";
            throw new SnapshotRefreshException(
                $"'{SnapshotPaths.SnapshotMarkdown}' no longer carries exactly one managed identity table, date line and pin/install prose span{detail}; update it by hand");
        }

        var version = receipt.ReleaseTag is ['v', ..] tag ? tag[1..] : receipt.ReleaseTag;
        var table = "| Fact | Value |\n|---|---|\n"
            + $"| Upstream file | `{SnapshotPaths.UpstreamArtifact}` |\n"
            + $"| Upstream release tag | `{receipt.ReleaseTag}` |\n"
            + $"| Commit | `{receipt.UpstreamCommit}` |\n"
            + "| Upstream product channel | npm `@opencode/cli@latest` (channel `latest`); "
            + $"this tag published as `{version}`, installing the `opencode` command (plus the transitional `opencode2` alias) |";
        text = IdentityTableRegex().Replace(text, _ => table, 1);
        text = PinProseRegex().Replace(text, _ => $"This pin is the release tag `{receipt.ReleaseTag}`, so\n"
            + $"`npm install -g @opencode/cli@{version}` installs a server built from exactly this commit; later", 1);
        return DateLineRegex().Replace(text, _ => $"Date: {date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}", 1);
    }

    [GeneratedRegex(@"^\| Fact \| Value \|\n\|---\|---\|\n\| Upstream file \| `packages/protocol/openapi.json` \|\n"
        + @"\| Upstream release tag \| `[^`\r\n]+` \|\n\| Commit \| \S+ \|\n"
        + @"\| Upstream product channel \| npm `@opencode/cli@latest` \(channel `latest`\); this tag published as `[^`\r\n]+`, "
        + @"installing the `opencode` command \(plus the transitional `opencode2` alias\) \|$",
        RegexOptions.Multiline, matchTimeoutMilliseconds: Timeout.Infinite)]
    private static partial Regex IdentityTableRegex();

    [GeneratedRegex(@"This pin is the release tag `[^`\r\n]+`, so\n`npm install -g @opencode/cli@[^`\r\n]+` installs a server built from exactly this commit; later",
        RegexOptions.None, matchTimeoutMilliseconds: Timeout.Infinite)]
    private static partial Regex PinProseRegex();

    [GeneratedRegex(@"^Date: \d{4}-\d{2}-\d{2}$", RegexOptions.Multiline, matchTimeoutMilliseconds: Timeout.Infinite)]
    private static partial Regex DateLineRegex();
}
