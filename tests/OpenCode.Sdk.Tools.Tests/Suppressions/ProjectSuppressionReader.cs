using System.Xml.Linq;

namespace OpenCode.Sdk.Tools.Tests.Suppressions;

/// <summary>
/// Reads the suppressions an MSBuild file declares: every diagnostic ID in a <c>NoWarn</c> or
/// <c>WarningsNotAsErrors</c> property (an inherited <c>$(…)</c> reference is not an ID), every
/// assignment of a property that switches the analyzer wall as a whole, and every slopwatch-ignore
/// comment.
/// </summary>
internal static class ProjectSuppressionReader
{
    private static readonly string[] BuildSwitches =
    [
        "TreatWarningsAsErrors", "CodeAnalysisTreatWarningsAsErrors", "RunAnalyzers", "RunAnalyzersDuringBuild",
        "EnableNETAnalyzers", "EnforceCodeStyleInBuild", "WarningLevel",
    ];

    private static readonly string[] BuildSwitchPrefixes = ["AnalysisLevel", "AnalysisMode"];

    public static IReadOnlyList<Suppression> Read(string origin, string text)
    {
        var document = XDocument.Parse(text);
        var result = new List<Suppression>();
        foreach (var element in document.Descendants().Where(static element => !element.HasElements))
        {
            result.AddRange(Property(origin, element.Name.LocalName, element.Value));
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
            .Where(static element => element.Name.LocalName == "Compile")
            .Select(static element => (string?)element.Attribute("Include"))
            .OfType<string>(),
    ];

    /// <summary>The suppressions one property assignment makes, in a project file or a file-based app directive.</summary>
    internal static IEnumerable<Suppression> Property(string origin, string name, string value)
    {
        if (name is "NoWarn" or "WarningsNotAsErrors")
        {
            var channel = name == "NoWarn" ? SuppressionChannel.NoWarn : SuppressionChannel.WarningsNotAsErrors;
            return value.Split([';', ','], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Where(static id => !id.StartsWith("$(", StringComparison.Ordinal))
                .Select(id => new Suppression(channel, origin, string.Empty, id, string.Empty));
        }

        return BuildSwitches.Contains(name, StringComparer.Ordinal)
            || BuildSwitchPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal))
            ? [new Suppression(SuppressionChannel.BuildSwitch, origin, string.Empty, name, value.Trim())]
            : [];
    }
}
