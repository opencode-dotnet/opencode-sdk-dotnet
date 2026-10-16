using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace OpenCode.Sdk.Tools.Tests.Suppressions;

/// <summary>
/// Reads the suppressions an MSBuild file declares: every diagnostic ID in a <c>NoWarn</c> or
/// <c>WarningsNotAsErrors</c> or <c>MSBuildWarningsAsMessages</c> property, every assignment of a property that switches the analyzer
/// wall, its severity or nullable analysis as a whole, and every slopwatch-ignore comment. A
/// property's scope is the condition it is set under, so widening a condition moves the ledger.
/// Property names compare without case, as MSBuild compares them. Forms the ledger does not model
/// are refused rather than skipped: a warning list on an item or a task, and a warning list that
/// takes IDs from another property.
/// </summary>
internal static partial class ProjectSuppressionReader
{
    private static readonly string[] WarningLists = ["NoWarn", "WarningsNotAsErrors", "MSBuildWarningsAsMessages"];

    private static readonly string[] BuildSwitches =
    [
        "TreatWarningsAsErrors", "CodeAnalysisTreatWarningsAsErrors", "WarningsAsErrors", "EnableNETAnalyzers",
        "EnforceCodeStyleInBuild", "WarningLevel", "Nullable", "CodeAnalysisRuleSet",
        "IsAotCompatible", "IsTrimmable", "EnableTrimAnalyzer", "EnableAotAnalyzer", "EnableSingleFileAnalyzer",
    ];

    private static readonly string[] BuildSwitchPrefixes = ["RunAnalyzers", "AnalysisLevel", "AnalysisMode"];

    private static readonly string[] TaskWarningParameters = [.. WarningLists, "DisabledWarnings"];

    public static IReadOnlyList<Suppression> Read(string origin, string text)
    {
        var document = XDocument.Parse(text);
        var result = new List<Suppression>();
        foreach (var element in document.Descendants())
        {
            var name = element.Name.LocalName;
            if (element.Parent is { } parent && Is(parent.Name.LocalName, "PropertyGroup"))
            {
                result.AddRange(Property(origin, name, element.Value, Condition(element)));
            }
            else if (WarningLists.Any(list => Is(list, name)))
            {
                throw new InvalidOperationException(
                    $"{origin}: <{name}> outside a PropertyGroup (item metadata) is a warning list the suppression ledger does not read; set it as a property.");
            }

            if (element.Attributes().FirstOrDefault(attribute => TaskWarningParameters.Any(parameter => Is(parameter, attribute.Name.LocalName))) is { } warnings)
            {
                throw new InvalidOperationException(
                    $"{origin}: <{name} {warnings.Name.LocalName}=\"…\"> is a warning list on an item or a task, which the suppression ledger does not read; set NoWarn as a property.");
            }
        }

        foreach (var comment in document.DescendantNodes().OfType<XComment>())
        {
            result.AddRange(CSharpSuppressionReader.SlopwatchIgnores(origin, comment.Value));
        }

        return result;
    }

    /// <summary>The <c>Include</c> of every <c>&lt;Compile&gt;</c> item in a project file.</summary>
    public static IReadOnlyList<string> CompileIncludes(string text) =>
    [
        .. XDocument.Parse(text).Descendants()
            .Where(static element => Is(element.Name.LocalName, "Compile"))
            .Select(static element => (string?)element.Attribute("Include"))
            .OfType<string>(),
    ];

    /// <summary>
    /// The build-property assignments on a workflow or script's command lines (<c>-p:</c>,
    /// <c>/p:</c> and <c>--property:</c>) that set a warning list or switch the analyzer wall.
    /// </summary>
    public static IReadOnlyList<Suppression> CommandLine(string origin, string text) =>
    [
        .. CommandLineProperty().Matches(text)
            .SelectMany(match => Property(origin, match.Groups["name"].Value, match.Groups["value"].Value.Trim('"', '\''), "command line")),
    ];

    /// <summary>The suppressions one property assignment makes, under the scope it is set in.</summary>
    internal static IEnumerable<Suppression> Property(string origin, string name, string value, string scope)
    {
        if (WarningLists.FirstOrDefault(list => Is(list, name)) is { } list)
        {
            var channel = list switch
            {
                "NoWarn" => SuppressionChannel.NoWarn,
                "MSBuildWarningsAsMessages" => SuppressionChannel.MSBuildWarningsAsMessages,
                _ => SuppressionChannel.WarningsNotAsErrors,
            };
            var ids = new List<Suppression>();
            foreach (var id in value.Split([';', ','], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (id.StartsWith("$(", StringComparison.Ordinal))
                {
                    if (!Is(id, $"$({list})"))
                    {
                        throw new InvalidOperationException(
                            $"{origin}: {name} takes IDs from {id}, which the suppression ledger cannot see; list the IDs in {name} itself.");
                    }

                    continue;
                }

                ids.Add(new Suppression(channel, origin, scope, DiagnosticId.Normalize(id), string.Empty));
            }

            return ids;
        }

        var buildSwitch = BuildSwitches.FirstOrDefault(candidate => Is(candidate, name))
            ?? BuildSwitchPrefixes.Where(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Select(_ => name).FirstOrDefault();
        return buildSwitch is null ? [] : [new Suppression(SuppressionChannel.BuildSwitch, origin, scope, buildSwitch, value.Trim())];
    }

    private static string Condition(XElement element)
    {
        var conditions = element.AncestorsAndSelf()
            .Select(static ancestor => (string?)ancestor.Attribute("Condition"))
            .OfType<string>()
            .Select(static condition => string.Join(' ', condition.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))
            .Reverse();
        return string.Join(" and ", conditions);
    }

    private static bool Is(string actual, string expected) => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"(?:^|\s)(?:-p:|/p:|--property:|-property:)(?<name>[A-Za-z]+)=(?<value>""[^""]*""|'[^']*'|\S+)", RegexOptions.Multiline, 1000)]
    private static partial Regex CommandLineProperty();
}
