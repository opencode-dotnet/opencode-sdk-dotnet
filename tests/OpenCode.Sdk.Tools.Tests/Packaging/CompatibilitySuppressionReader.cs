using System.Xml.Linq;

namespace OpenCode.Sdk.Tools.Tests.Packaging;

internal sealed class CompatibilitySuppressionReader
{
    private readonly XElement _root;

    public CompatibilitySuppressionReader(string xml)
    {
        var root = XDocument.Parse(xml).Root;
        if (root is null || root.Name != "Suppressions" || root.Elements().Any(static element => element.Name != "Suppression"))
        {
            throw new InvalidOperationException("CompatibilitySuppressions.xml must contain a Suppressions root and Suppression entries.");
        }

        _root = root;
    }

    public IReadOnlyList<CompatibilitySuppression> Read() => [.. _root.Elements("Suppression").Select(ReadEntry)];

    private static CompatibilitySuppression ReadEntry(XElement entry)
    {
        var flag = RequiredValue(entry, "IsBaselineSuppression");
        if (!bool.TryParse(flag, out var baseline))
        {
            throw new InvalidOperationException($"Suppression IsBaselineSuppression '{flag}' must be true or false.");
        }

        return new CompatibilitySuppression(
            RequiredValue(entry, "DiagnosticId"), RequiredValue(entry, "Target"),
            RequiredValue(entry, "Left"), RequiredValue(entry, "Right"), baseline);
    }

    private static string RequiredValue(XElement entry, string name)
    {
        var values = entry.Elements(name).ToArray();
        return values.Length == 1 && !string.IsNullOrWhiteSpace(values[0].Value)
            ? values[0].Value.Trim()
            : throw new InvalidOperationException($"Every Suppression must carry exactly one nonempty {name}.");
    }
}
