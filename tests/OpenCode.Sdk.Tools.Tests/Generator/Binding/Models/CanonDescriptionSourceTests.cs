using System.Globalization;
using System.IO.Abstractions;
using System.Text;
using System.Text.RegularExpressions;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Tests.Support;
using Testably.Abstractions;
using Testably.Abstractions.Testing;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tools.Tests.Generator.Binding.Models;

public sealed partial class CanonDescriptionSourceTests
{
    [Test]
    public async Task Sources_Should_Quote_The_Owning_Canon_Section()
    {
        var fileSystem = new RealFileSystem();
        var (_, _, curation) = await BindingTestHost.LoadPinnedInputsAsync();
        // No committed row uses canon yet; this proof currently passes vacuously.
        await Assert.That(await CheckAsync(fileSystem, new RepositoryRoot(fileSystem).Locate(), curation.Descriptions)).IsEmpty();
    }

    [Test]
    [Arguments("missing", "file does not exist")]
    [Arguments("anchor", "heading slug does not exist")]
    [Arguments("outside", "excerpt not found in heading section")]
    [Arguments("valid", null)]
    public async Task Sources_Should_Refuse_Unproven_Canon(string fault, string? problem)
    {
        var real = new RealFileSystem();
        var fixture = await real.File.ReadAllTextAsync(real.Path.Combine(new RepositoryRoot(real).Locate(),
            "tests/OpenCode.Sdk.Tools.Tests/Generator/Binding/Fixtures/canon-source.md"), CancellationToken.None);
        var fileSystem = new MockFileSystem();
        fileSystem.Initialize().WithFile("docs/architecture/example.md").Which(file => file.HasStringContent(fixture));
        var canon = "docs/architecture/" + (fault == "missing" ? "missing" : "example") + ".md#" + (fault == "anchor" ? "absent" : "reconnect-contract");
        DescriptionCuration[] rows = [new()
        {
            Type = "EventsClient", Member = "SubscribeAsync", Summary = "The event stream.", Reason = "Prove canon evidence.",
            Sources = [new() { Canon = canon, Excerpt = fault == "outside" ? "Only in the next section." : "Keep the stream alive." }],
        }];
        string[] expected = problem is null ? [] : ["EventsClient.SubscribeAsync: " + canon + ": " + problem];
        await Assert.That(await CheckAsync(fileSystem, fileSystem.Directory.GetCurrentDirectory(), rows))
            .IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("```\n## Contract\nEvidence belongs here.\n```", "contract", "heading slug does not exist")]
    [Arguments("~~~\n## Contract\nEvidence belongs here.\n~~~", "contract", "heading slug does not exist")]
    [Arguments("## Contract\n  ## Next\nEvidence belongs here.", "contract", "excerpt not found in heading section")]
    [Arguments("## Contract\n### Detail\nEvidence belongs here.", "contract", null)]
    [Arguments("## Contract ##\nEvidence belongs here.", "contract", null)]
    [Arguments("## [Contract](x)\nEvidence belongs here.", "contractx", "heading '[Contract](x)' is outside the supported plain-text subset")]
    [Arguments("## Contract\n### *Detail*\nEvidence belongs here.", "contract", "heading '*Detail*' is outside the supported plain-text subset")]
    [Arguments("## Contract\nEvidence belongs here.\n## Contract", "contract", "headings in docs/architecture/example.md share the GitHub slug 'contract', outside the supported subset")]
    [Arguments("## Contract\n## Contract-1\nEvidence belongs here.", "contract-1", "headings in docs/architecture/example.md share the GitHub slug 'contract-1', outside the supported subset")]
    [Arguments("## [Contract](x)\n## Contract\nEvidence belongs here.", "contract", "heading '[Contract](x)' is outside the supported plain-text subset")]
    [Arguments("## Contract\nDetail\n===\nEvidence belongs here.", "contract", "docs/architecture/example.md line 3 is outside the supported subset")]
    [Arguments("## Contract\nDetail\n---\nEvidence belongs here.", "contract", "docs/architecture/example.md line 3 is outside the supported subset")]
    [Arguments("## Contract\nElsewhere\n=== \nEvidence belongs here.", "contract", "docs/architecture/example.md line 3 is outside the supported subset")]
    [Arguments("## Other\nElsewhere\n-\n## Contract\nEvidence belongs here.", "contract", "docs/architecture/example.md line 3 is outside the supported subset")]
    [Arguments("## Contract\n---\nEvidence belongs here.", "contract", "docs/architecture/example.md line 2 is outside the supported subset")]
    [Arguments("## Contract\n\n* * *\nEvidence belongs here.", "contract", "docs/architecture/example.md line 3 is outside the supported subset")]
    [Arguments("<!--\n## Contract\nEvidence belongs here.\n-->", "contract", "docs/architecture/example.md line 1 is outside the supported subset")]
    [Arguments("## Contract\n> ## Contract\nEvidence belongs here.", "contract", "docs/architecture/example.md line 2 is outside the supported subset")]
    [Arguments("## Contract\n[x]: https://example.com\nEvidence belongs here.", "contract", "docs/architecture/example.md line 2 is outside the supported subset")]
    [Arguments("## Contract\n\tEvidence belongs here.", "contract", "docs/architecture/example.md line 2 is outside the supported subset")]
    [Arguments("    ## Contract\nEvidence belongs here.", "contract", "docs/architecture/example.md line 1 is outside the supported subset")]
    [Arguments("##Contract\nEvidence belongs here.", "contract", "docs/architecture/example.md line 1 is outside the supported subset")]
    [Arguments("## Contract\n\n    code\nEvidence belongs here.", "contract", "docs/architecture/example.md line 3 is outside the supported subset")]
    [Arguments("## Contract\n   ```\nEvidence belongs here.\n   ```", "contract", "docs/architecture/example.md line 2 is outside the supported subset")]
    [Arguments("```a`b\n## Contract\nEvidence belongs here.", "contract", "docs/architecture/example.md line 1 is outside the supported subset")]
    [Arguments("## Contract\n```\nEvidence belongs here.", "contract", "docs/architecture/example.md line 2 opens a fence that never closes, outside the supported subset")]
    [Arguments("## Contract\n- item\n    # x\nEvidence belongs here.", "contract", "docs/architecture/example.md line 3 is outside the supported subset")]
    [Arguments("## Contract\n- item\n\nText\n\n  Evidence belongs here.", "contract", "docs/architecture/example.md line 6 is outside the supported subset")]
    [Arguments("## Contract\n- item\n  Evidence belongs here.", "contract", null)]
    [Arguments("## Contract\n1. item\n   - Evidence belongs here.", "contract", null)]
    [Arguments("## Contract\n| a | b |\n| - | - |\nEvidence belongs here.", "contract", null)]
    [Arguments("## Contract\nEvidence\n  belongs here.", "contract", null)]
    [Arguments("## Contract\nEvidence  belongs    here.", "contract", null)]
    [Arguments("   ## Contract   ###  \nEvidence belongs here.", "contract", null)]
    [Arguments("## Contràct:  2-3?\nEvidence belongs here.", "contràct--2-3", null)]
    [Arguments("## \U00010400 Contract\nEvidence belongs here.", "\U00010428-contract", null)]
    [Arguments("## Contract\n````\n## Next\n~~~\n```\n```` trailing\nEvidence belongs here.\n`````  ", "contract", null)]
    [Arguments("## Contract\n~~~\nDetail\n---\nEvidence belongs here.\n~~~", "contract", null)]
    [Arguments("## Contract\n~~~\n## Next\n~~~\n## Next\nEvidence belongs here.", "contract", "excerpt not found in heading section")]
    [Arguments("- ## Contract\n## Contract\nEvidence belongs here.", "contract", "docs/architecture/example.md line 1 is outside the supported subset")]
    [Arguments("- <!--\n  ## Contract\n  Evidence belongs here.", "contract", "docs/architecture/example.md line 1 is outside the supported subset")]
    [Arguments("## Contract\n- item\n  ~~~\n  code\n## Next\nEvidence belongs here.", "contract", "docs/architecture/example.md line 3 is outside the supported subset")]
    [Arguments("## Contract\n##\tNext\nEvidence belongs here.", "contract", "docs/architecture/example.md line 2 is outside the supported subset")]
    [Arguments("## Contract\n``` bad`info\n## Next\nEvidence belongs here.", "contract", "docs/architecture/example.md line 2 is outside the supported subset")]
    [Arguments("~~~\n~~~\u00A0\n## Contract\nEvidence belongs here.", "contract", "docs/architecture/example.md line 1 opens a fence that never closes, outside the supported subset")]
    [Arguments("## Contract\n### ~~Detail~~\nEvidence belongs here.", "contract", "heading '~~Detail~~' is outside the supported plain-text subset")]
    [Arguments("## Contract\n-\t## Next\nEvidence belongs here.", "contract", "docs/architecture/example.md line 2 is outside the supported subset")]
    [Arguments("## Contract\n*\n  ## Next\nEvidence belongs here.", "contract", "docs/architecture/example.md line 2 is outside the supported subset")]
    [Arguments("## Contract\n- - item\nEvidence belongs here.", "contract", "docs/architecture/example.md line 2 is outside the supported subset")]
    [Arguments("## Contract\n\u00A0\n    Evidence belongs here.", "contract", "docs/architecture/example.md line 2 is outside the supported subset")]
    [Arguments("## Contract\n| <a name='contract'></a> | x |\nEvidence belongs here.", "contract", "docs/architecture/example.md line 2 is outside the supported subset")]
    [Arguments("## Contract\nSee <a name='contract'></a> here.\nEvidence belongs here.", "contract", "docs/architecture/example.md line 2 is outside the supported subset")]
    [Arguments("## Contract\n- item <a name='contract'></a>\nEvidence belongs here.", "contract", "docs/architecture/example.md line 2 is outside the supported subset")]
    [Arguments("## Contract\nUse `Task<T>` and `IAsyncEnumerable<T>` here.\nEvidence belongs here.", "contract", null)]
    [Arguments("## Contract\nUse ``a ` <b>`` here.\nEvidence belongs here.", "contract", "docs/architecture/example.md line 2 is outside the supported subset")]
    [Arguments("## Contract\nPipes under `\\\\.\\pipe\\` are <local>.\nEvidence belongs here.", "contract", "docs/architecture/example.md line 2 is outside the supported subset")]
    [Arguments("## Contract\nPipes under `\\\\.\\pipe\\<name>\\` are local.\nEvidence belongs here.", "contract", null)]
    [Arguments("## Contract\nUse \\`<b>\\` here.\nEvidence belongs here.", "contract", "docs/architecture/example.md line 2 is outside the supported subset")]
    [Arguments("## Contract\nUse `Task<T> here.\nEvidence belongs here.", "contract", "docs/architecture/example.md line 2 is outside the supported subset")]
    [Arguments("## Contract\nUse ``Task<T>` here.\nEvidence belongs here.", "contract", "docs/architecture/example.md line 2 is outside the supported subset")]
    [Arguments("- \t## Contract\n## Contract\nEvidence belongs here.", "contract", "docs/architecture/example.md line 1 is outside the supported subset")]
    [Arguments("## Contract #\u00A0\nEvidence belongs here.", "contract", "docs/architecture/example.md line 1 is outside the supported subset")]
    [Arguments("## Contract \t##\nEvidence belongs here.", "contract-", "docs/architecture/example.md line 1 is outside the supported subset")]
    [Arguments("## Contract\nEvidence belongs\u2003here.", "contract", "docs/architecture/example.md line 2 is outside the supported subset")]
    [Arguments("## Contract\nEvidence belongs here.\u200B", "contract", "docs/architecture/example.md line 2 is outside the supported subset")]
    [Arguments("## Contract\n~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~\n~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~\n## Next\nEvidence belongs here.\n~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~", "contract", "docs/architecture/example.md line 2 is outside the supported subset")]
    [Arguments("## Contract\n~~~\n## Next\n~~~\t\n## Other\nEvidence belongs here.", "contract", "excerpt not found in heading section")]
    [Arguments("## Contract\nIntro `\n` <a name='contract'></a> `\nEvidence belongs here.", "contract", "docs/architecture/example.md line 2 is outside the supported subset")]
    [Arguments("## Contract\n- Intro `\n  ` <a name='contract'></a> `\nEvidence belongs here.", "contract", "docs/architecture/example.md line 2 is outside the supported subset")]
    [Arguments("## Contract\n| a | b |\n| --- | --- |\n| `<a name='contract'></a> | ` |\nEvidence belongs here.", "contract", "docs/architecture/example.md line 4 is outside the supported subset")]
    [Arguments("## Contract\n[x](u`)<a name='contract'></a>`\nEvidence belongs here.", "contract", "docs/architecture/example.md line 2 is outside the supported subset")]
    [Arguments("## Contract\nhttps://example.com/`<a name='contract'></a>`\nEvidence belongs here.", "contract", "docs/architecture/example.md line 2 is outside the supported subset")]
    [Arguments("## Contract\nUse `````````````````````````````````````````````````````````````````````````````````<a name='contract'></a>`````````````````````````````````````````````````````````````````````````````````\nEvidence belongs here.", "contract", "docs/architecture/example.md line 2 is outside the supported subset")]
    [Arguments("## Contract\nFlags `DETACHED |\nCREATE_NEW` apply.\nEvidence belongs here.", "contract", null)]
    [Arguments("## Contract\nSee `Task<T>` and\n`IAsyncEnumerable<T>`.\n\nRead https://example.com first.\nEvidence belongs here.", "contract", null)]
    public async Task Sources_Should_Prove_Only_The_Stated_Heading_Subset(string markdown, string anchor, string? problem)
    {
        var fileSystem = new MockFileSystem();
        fileSystem.Initialize().WithFile("docs/architecture/example.md").Which(file => file.HasStringContent(markdown));
        DescriptionCuration[] rows = [new()
        {
            Type = "EventsClient", Member = "SubscribeAsync", Summary = "The stream.", Reason = "Prove section boundaries.",
            Sources = [new() { Canon = "docs/architecture/example.md#" + anchor, Excerpt = "Evidence belongs here." }],
        }];
        string[] expected = problem is null ? [] : ["EventsClient.SubscribeAsync: docs/architecture/example.md#" + anchor + ": " + problem];
        await Assert.That(await CheckAsync(fileSystem, fileSystem.Directory.GetCurrentDirectory(), rows))
            .IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    private static async Task<IReadOnlyList<string>> CheckAsync(IFileSystem fileSystem, string root, IReadOnlyList<DescriptionCuration> rows)
    {
        var problems = new List<string>();
        foreach (var (row, source) in rows.SelectMany(row => (row.Sources ?? []).OfType<DescriptionSource>()
            .Where(static source => source.Canon is not null).Select(source => (row, source))))
        {
            var parts = source.Canon!.Split('#'); // The binder proves the canon shape and excerpt before this oracle.
            var path = fileSystem.Path.Combine(root, parts[0]);
            var problem = "file does not exist";
            if (fileSystem.File.Exists(path))
            {
                var lines = await fileSystem.File.ReadAllLinesAsync(path, CancellationToken.None);
                problem = SectionProblem(lines, parts[1], parts[0], source.Excerpt!);
            }

            if (problem is not null) { problems.Add(row.Type + (row.Member is null ? "" : "." + row.Member) + ": " + source.Canon + ": " + problem); }
        }

        return problems;
    }

    private static string Normalize(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string? SectionProblem(string[] lines, string anchor, string file, string excerpt)
    {
        // Allowlist: every line of the cited file is one of these, or the source is refused. Outside a fence the only
        // whitespace character is the space (U+0020): no tab, no other Unicode space, no control or format character.
        // A blank line; a plain-text ATX heading (1-6 hashes after 0-3 spaces, then a space; optional closing hashes);
        // a list item ("-", "*", "+" or "1." / "1)", spaces, and a body that is itself a text line); a list continuation
        // (an indented text line while a list is open); a paragraph line; a table row (starts with "|", holds no "<");
        // a closed backtick or tilde fence of 3-255 characters at column 0 and the lines inside it. A text line never
        // starts with "#", ">", "<", a fence, a link reference definition, an empty list marker, or a run of "=", "-",
        // "*" or "_" alone (a setext underline or a thematic break), and holds no run of two or more backticks, so code
        // spans pair strictly in order. A paragraph (a list item or text line and the text lines that follow it) holds no
        // "<" outside its code spans, and none at all beside link or autolink syntax: inline HTML such as <a name> makes
        // anchors the slug rules cannot see. Anchors: no two headings share a GitHub slug and none equals another's slug
        // plus "-<digits>", so GitHub adds no suffix and every anchor is its heading's own slug.
        // This is not a Markdown renderer: it accepts only what it models and refuses everything else. It is measured
        // against the files under docs/architecture/, which all lie inside it; a canon edit that leaves the subset turns
        // a citing row red until the line is reworded.
        var (headings, problem) = Headings(lines, file);
        if (problem is not null) { return problem; }
        if (SharedSlug([.. headings.Select(static heading => heading.Slug)]) is { } shared)
        {
            return $"headings in {file} share the GitHub slug '{shared}', outside the supported subset";
        }
        var matches = headings.Where(heading => heading.Slug == anchor).ToArray();
        if (matches.Length == 0) { return "heading slug does not exist"; }
        var target = matches[0];
        var end = headings.Where(heading => heading.Index > target.Index && heading.Level <= target.Level)
            .Select(static heading => heading.Index).DefaultIfEmpty(lines.Length).First();
        var section = string.Join('\n', lines.Skip(target.Index + 1).Take(end - target.Index - 1));
        return Normalize(section).Contains(Normalize(excerpt), StringComparison.Ordinal) ? null : "excerpt not found in heading section";
    }

    private static (List<(string Slug, int Level, int Index)> Headings, string? Problem) Headings(string[] lines, string file)
    {
        var headings = new List<(string Slug, int Level, int Index)>();
        var fence = "";
        var fenceLine = 0;
        var list = false;
        var blankAbove = true;
        var paragraph = new StringBuilder();
        var paragraphLine = 0;
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (fence.Length > 0)
            {
                fence = Closes(line, fence) ? "" : fence;
                blankAbove = false;
                continue;
            }
            var content = line.TrimStart(' ');
            var indent = line.Length - content.Length;
            var kind = content.Length == 0 ? LineKind.Blank : Classify(line, content, indent, list, blankAbove);
            if (kind is not LineKind.Text && ParagraphProblem(paragraph, paragraphLine, file) is { } inline)
            {
                return (headings, inline);
            }
            switch (kind)
            {
                case LineKind.Blank:
                    blankAbove = true;
                    continue;
                case LineKind.Fence:
                    fence = Fence().Match(line).Groups["fence"].Value;
                    fenceLine = index;
                    break;
                case LineKind.Heading:
                    {
                        var heading = Heading().Match(line);
                        var text = ClosingHashes().Replace(heading.Groups["text"].Value, "").Trim();
                        if (text.IndexOfAny(['`', '[', ']', '*', '_', '~', '<', '>', '!', '\\', '&', '|']) >= 0)
                        {
                            return (headings, $"heading '{text}' is outside the supported plain-text subset");
                        }
                        headings.Add((Slug(text), heading.Groups["level"].Length, index));
                        list = false;
                        break;
                    }
                case LineKind.ListItem:
                    list = true;
                    paragraphLine = index;
                    _ = paragraph.Append(ListItem().Match(content).Groups["body"].Value);
                    break;
                case LineKind.Table:
                    list = false;
                    break;
                case LineKind.Text:
                    list &= !(indent == 0 && blankAbove);
                    paragraphLine = paragraph.Length == 0 ? index : paragraphLine;
                    _ = paragraph.Append('\n').Append(content);
                    break;
                default:
                    return (headings, string.Create(CultureInfo.InvariantCulture, $"{file} line {index + 1} is outside the supported subset"));
            }
            blankAbove = false;
        }
        if (ParagraphProblem(paragraph, paragraphLine, file) is { } last)
        {
            return (headings, last);
        }
        return fence.Length > 0
            ? (headings, string.Create(CultureInfo.InvariantCulture, $"{file} line {fenceLine + 1} opens a fence that never closes, outside the supported subset"))
            : (headings, null);
    }

    /// <summary>Checks a finished paragraph for inline HTML and clears it. Code spans pair across the paragraph's lines, so
    /// "&lt;" is judged on the whole paragraph; a paragraph with "&lt;" and link or autolink syntax refuses, because a link
    /// destination or an autolink can consume a backtick that would otherwise open a span.</summary>
    private static string? ParagraphProblem(StringBuilder paragraph, int line, string file)
    {
        var text = paragraph.ToString();
        _ = paragraph.Clear();
        return text.Contains('<', StringComparison.Ordinal) && (OutsideCodeSpans(text).Contains('<', StringComparison.Ordinal) || LinkSyntax().IsMatch(text))
            ? string.Create(CultureInfo.InvariantCulture, $"{file} line {line + 1} is outside the supported subset")
            : null;
    }

    private enum LineKind
    {
        Unsupported,
        Blank,
        Fence,
        Heading,
        ListItem,
        Table,
        Text,
    }

    private static LineKind Classify(string line, string content, int indent, bool list, bool blankAbove) => content switch
    {
        _ when line.Any(static character => character != ' ' && (char.IsWhiteSpace(character) || char.IsControl(character)
            || char.GetUnicodeCategory(character) is UnicodeCategory.Format)) => LineKind.Unsupported,
        _ when indent == 0 && Fence().Match(line) is { Success: true } open => open.Groups["fence"].Length > 255 || (open.Groups["fence"].Value[0] == '`'
            && open.Groups["tail"].Value.Contains('`', StringComparison.Ordinal)) ? LineKind.Unsupported : LineKind.Fence,
        _ when indent <= 3 && Heading().IsMatch(line) => LineKind.Heading,
        _ when content.Contains("``", StringComparison.Ordinal) => LineKind.Unsupported,
        _ when Unsupported().IsMatch(content) => LineKind.Unsupported,
        _ when ListItem().Match(content) is { Success: true } item && (indent <= 3 || list) => Unsupported().IsMatch(item.Groups["body"].Value)
            || ListItem().IsMatch(item.Groups["body"].Value) ? LineKind.Unsupported : LineKind.ListItem,
        ['|', ..] when indent == 0 => content.Contains('<', StringComparison.Ordinal) ? LineKind.Unsupported : LineKind.Table,
        _ when indent == 0 || list || !blankAbove => LineKind.Text,
        _ => LineKind.Unsupported,
    };

    /// <summary>The line without its code spans, scanned as CommonMark does: outside a span a backslash escapes the next
    /// character; a backtick run opens a span that only a run of the same length on the same line closes.</summary>
    private static string OutsideCodeSpans(string text)
    {
        var kept = new StringBuilder();
        var index = 0;
        while (index < text.Length)
        {
            if (text[index] == '\\' && index + 1 < text.Length)
            {
                _ = kept.Append(text, index, 2);
                index += 2;
                continue;
            }
            if (text[index] != '`')
            {
                _ = kept.Append(text[index]);
                index++;
                continue;
            }
            var run = Run(text, index);
            var close = run;
            while (close < text.Length && (text[close] != '`' || Run(text, close) - close != run - index))
            {
                close = text[close] == '`' ? Run(text, close) : close + 1;
            }
            if (close < text.Length)
            {
                index = Run(text, close);
                continue;
            }
            _ = kept.Append(text, index, run - index);
            index = run;
        }
        return kept.ToString();
    }

    private static int Run(string text, int start)
    {
        var end = start;
        while (end < text.Length && text[end] == '`')
        {
            end++;
        }
        return end;
    }

    private static bool Closes(string line, string fence) => Fence().Match(line) is { Success: true } close
        && close.Groups["fence"].Value[0] == fence[0] && close.Groups["fence"].Length >= fence.Length
        && close.Groups["tail"].Value.Trim(' ', '\t').Length == 0;

    private static string Slug(string text) => string.Concat(text.ToLowerInvariant().EnumerateRunes().Where(static rune =>
        Rune.IsLetter(rune) || Rune.IsNumber(rune) || rune.Value is ' ' or '-' or '_'
        || Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)).Replace(' ', '-');

    /// <summary>GitHub suffixes a repeated slug with "-1", "-2", ...: a repeat, or a slug such a suffix could produce, is refused.</summary>
    private static string? SharedSlug(string[] slugs) => slugs.Where((slug, position) => slugs.Where((_, other) => other != position)
        .Any(other => slug == other || (slug.StartsWith(other + "-", StringComparison.Ordinal) && slug.Length > other.Length + 1
            && slug[(other.Length + 1)..].All(char.IsAsciiDigit)))).FirstOrDefault();

    [GeneratedRegex(@"^ {0,3}(?<level>#{1,6})(?<text> +.*)?$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: Timeout.Infinite)]
    private static partial Regex Heading();
    [GeneratedRegex(@" +#+\s*$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: Timeout.Infinite)]
    private static partial Regex ClosingHashes();
    [GeneratedRegex(@"^ {0,3}(?<fence>`{3,}|~{3,})(?<tail>.*)$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: Timeout.Infinite)]
    private static partial Regex Fence();
    [GeneratedRegex(@"^(?:[-*+]|[0-9]{1,9}[.)]) +(?<body>\S.*)$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: Timeout.Infinite)]
    private static partial Regex ListItem();
    [GeneratedRegex(@"^(?:[#><]|```|~~~|\[[^\]]*\]:|(?:[-*+]|[0-9]{1,9}[.)]) *$|(?:=+|-+|(?:[-*_] *){3,}) *$)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: Timeout.Infinite)]
    private static partial Regex Unsupported();
    [GeneratedRegex(@"\]\(|\]:|://|www\.", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: Timeout.Infinite)]
    private static partial Regex LinkSyntax();
}
