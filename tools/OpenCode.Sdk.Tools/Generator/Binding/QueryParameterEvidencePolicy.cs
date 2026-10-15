using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Refresh.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

/// <summary>
/// Holds every <c>queryParameterTypes</c> row to a file the source watch pins. The watch is what
/// stops a refresh when upstream's decode schema changes, so a row whose evidence it does not
/// watch would keep typing the parameter after the schema behind it moved (ADR-0037).
/// </summary>
internal static class QueryParameterEvidencePolicy
{
    public static void Check(GenerationCuration curation, SourceWatch watch, BindingErrorCollector errors)
    {
        ArgumentNullException.ThrowIfNull(curation);
        ArgumentNullException.ThrowIfNull(watch);
        ArgumentNullException.ThrowIfNull(errors);

        var watched = watch.Sources.Select(static source => source.Path).ToHashSet(StringComparer.Ordinal);
        foreach (var row in curation.QueryParameterTypes.Where(row => !watched.Contains(row.Evidence.Path)))
        {
            errors.Add(
                BindingErrorCategory.Curation,
                row.OperationId + ":" + row.Parameter,
                $"the evidence '{row.Evidence.Path}' is not in the source watch: pin it there, so a refresh that changes the decode stops for review");
        }
    }
}
