using OpenCode.Sdk.Tools.Generator.Refresh;
using OpenCode.Sdk.Tools.Tests.Support;

namespace OpenCode.Sdk.Tools.Tests.Generator.Refresh;

public sealed class SnapshotMarkdownRendererTests
{
    [Test]
    public async Task Render_Should_Update_Every_Pin_Identity_Field()
    {
        var text = Render(RefreshScenarioData.SnapshotMarkdown);

        await Assert.That(text).Contains("| Upstream release tag | `v2.0.30` |");
        await Assert.That(text).Contains("this tag published as `2.0.30`,");
        await Assert.That(text).Contains("This pin is the release tag `v2.0.30`, so");
        await Assert.That(text).Contains("`npm install -g @opencode/cli@2.0.30`");
        await Assert.That(text).Contains("| Commit | `0123456789abcdef0123456789abcdef01234567` |");
        await Assert.That(text).Contains("Date: 2031-02-03");
        await Assert.That(text).Contains("semver release tags (`v2.0.0`…)");
    }

    [Test]
    [Arguments("table", false)]
    [Arguments("table", true)]
    [Arguments("date", false)]
    [Arguments("date", true)]
    [Arguments("prose", false)]
    [Arguments("prose", true)]
    [Arguments("Upstream release tag", false)]
    [Arguments("Upstream release tag", true)]
    public async Task Render_Should_Refuse_A_Missing_Or_Duplicate_Managed_Region(string region, bool duplicate)
    {
        var text = RefreshScenarioData.SnapshotMarkdown;
        var start = region switch { "table" => "| Fact |", "date" => "Date:", _ => "This pin is" };
        var end = region switch { "table" => "\n\n", "date" => "\n", _ => "; later" };
        var offset = text.IndexOf(start, StringComparison.Ordinal);
        var length = text.IndexOf(end, offset, StringComparison.Ordinal) - offset + (region == "prose" ? end.Length : 0);
        var managed = text.Substring(offset, length);
        var invalid = duplicate ? text + "\n" + managed : text.Remove(offset, length);
        if (region == "Upstream release tag")
        {
            const string stray = "| Upstream release tag | `v2.0.28` |\n";
            invalid = duplicate ? text + "\n" + stray : stray + text;
        }

        var exception = await Assert.That(() => Render(invalid)).Throws<SnapshotRefreshException>();

        await Assert.That(exception!.Message).Contains(region == "Upstream release tag"
            ? "Upstream release tag" : "exactly one managed identity table, date line and pin/install prose span");
    }

    [Test]
    [Arguments("v2.0.29")]
    [Arguments("v$1.0.0")]
    public async Task Render_Should_Preserve_Surroundings_And_Treat_Tag_Data_Literally(string tag)
    {
        var current = RefreshScenarioData.SnapshotMarkdown;
        const string surrounding = "Unrelated `backticks` | pipes | $1\n";
        var rendered = Render(surrounding + current + surrounding, tag);
        var expected = current.Replace("v2.0.29", tag, StringComparison.Ordinal)
            .Replace("@2.0.29", "@" + tag[1..], StringComparison.Ordinal)
            .Replace("`2.0.29`", "`" + tag[1..] + "`", StringComparison.Ordinal)
            .Replace(new string('a', 40), "`" + RefreshScenarioData.Commit + "`", StringComparison.Ordinal)
            .Replace("2026-08-13", "2031-02-03", StringComparison.Ordinal);

        await Assert.That(rendered).IsEqualTo(surrounding + expected + surrounding);
    }

    private static string Render(string text, string tag = "v2.0.30") => SnapshotMarkdownRenderer.Render(text,
        RefreshScenarioData.Receipt(RefreshScenarioData.DocumentBytes(static spec => spec.WithOperation("test.get"))) with { ReleaseTag = tag },
        new DateOnly(2031, 2, 3));
}
