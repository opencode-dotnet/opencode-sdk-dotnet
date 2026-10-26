using System.Collections.ObjectModel;

namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

/// <summary>Documentation overrides already resolved against the generated type and its properties.</summary>
internal sealed record TypeDocumentationPlan
{
    public string? Summary { get; init; }

    public bool SummaryIsCurated { get; init; }

    public string? Remarks { get; init; }

    public string? CuratedRemarks { get; init; }

    public IReadOnlyDictionary<string, DocumentationPlan> Members
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = new ReadOnlyDictionary<string, DocumentationPlan>(new Dictionary<string, DocumentationPlan>(value, StringComparer.Ordinal));
        }
    } = new ReadOnlyDictionary<string, DocumentationPlan>(new Dictionary<string, DocumentationPlan>(StringComparer.Ordinal));
}
