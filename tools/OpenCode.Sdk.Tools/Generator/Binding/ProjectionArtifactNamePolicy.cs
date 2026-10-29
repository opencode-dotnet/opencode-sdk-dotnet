namespace OpenCode.Sdk.Tools.Generator.Binding;

/// <summary>
/// Strips upstream projection artifacts, Effect's encode-side <c>*Encoded</c> fallback rename (the
/// #44911 projection context), from component names before .NET name derivation.
/// </summary>
/// <remarks>
/// A suffix is stripped from the final dotted segment whenever a name remains, whatever other
/// components the document holds, so an unrelated key can never toggle a name. A schema whose
/// stripped name meets another schema's name refuses through the type-name collision wall until a
/// <c>schemaNames</c> row names one side.
/// </remarks>
internal static class ProjectionArtifactNamePolicy
{
    private static readonly string[] ArtifactSuffixes = ["Encoded"];

    /// <summary>Returns the component name with any declared projection-artifact suffix removed.</summary>
    public static string Normalize(string componentName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(componentName);

        foreach (var suffix in ArtifactSuffixes)
        {
            if (!componentName.EndsWith(suffix, StringComparison.Ordinal))
            {
                continue;
            }

            var stripped = componentName[..^suffix.Length];
            if (stripped.Length == stripped.LastIndexOf('.', StringComparison.Ordinal) + 1)
            {
                continue;
            }

            return stripped;
        }

        return componentName;
    }
}
