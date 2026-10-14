using OpenCode.Sdk.Tools.Generator.Binding.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

/// <summary>
/// The reasoned rows that place the models no family prefix places. A row names a word-boundary
/// name prefix and an owner: an admitted family, or <see cref="TypeHome.CommonSlice"/> for the
/// shared error spine. A row without a reason, with an unknown owner, matching a model another row
/// also matches, or placing nothing is a curation error.
/// </summary>
internal sealed class ModelOwnerRows
{
    private readonly IReadOnlyList<ModelOwnerCuration> _rows;
    private readonly HashSet<ModelOwnerCuration> _used = [];

    private ModelOwnerRows(IReadOnlyList<ModelOwnerCuration> rows)
    {
        _rows = rows;
    }

    public static ModelOwnerRows Create(IReadOnlyList<ModelOwnerCuration> rows, IReadOnlySet<string> families, BindingErrorCollector errors)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(families);
        ArgumentNullException.ThrowIfNull(errors);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var valid = new List<ModelOwnerCuration>();
        foreach (var row in rows)
        {
            var subject = string.IsNullOrWhiteSpace(row.Match) ? "modelOwners" : row.Match;
            var usable = true;
            if (string.IsNullOrWhiteSpace(row.Match) || !seen.Add(row.Match))
            {
                errors.Add(BindingErrorCategory.Curation, subject, "model owner row match is empty or duplicated");
                usable = false;
            }

            if (string.IsNullOrWhiteSpace(row.Reason))
            {
                errors.Add(BindingErrorCategory.Curation, subject, "model owner row must declare a reason");
            }

            if (!families.Contains(row.Owner) && !string.Equals(row.Owner, TypeHome.CommonSlice, StringComparison.Ordinal))
            {
                errors.Add(BindingErrorCategory.Curation, subject,
                    $"model owner '{row.Owner}' is neither an admitted family nor '{TypeHome.CommonSlice}'");
                usable = false;
            }

            if (usable)
            {
                valid.Add(row);
            }
        }

        return new ModelOwnerRows(valid);
    }

    /// <summary>Gets the owner the one matching row names, or <see langword="null"/> when no row matches.</summary>
    public string? OwnerOf(OwnershipSubject subject, BindingErrorCollector errors)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(errors);

        var matches = _rows.Where(row => subject.Matches(row.Match)).ToArray();
        if (matches.Length > 1)
        {
            errors.Add(BindingErrorCategory.Curation, subject.Name,
                $"model owner rows '{string.Join("', '", matches.Select(static row => row.Match))}' all match the model");
        }

        _used.UnionWith(matches);
        return matches.FirstOrDefault()?.Owner;
    }

    /// <summary>A row that places nothing is stale: the refresh that emptied it surfaces it instead of carrying it.</summary>
    public void ReportDeadRows(BindingErrorCollector errors)
    {
        ArgumentNullException.ThrowIfNull(errors);

        foreach (var row in _rows.Where(row => !_used.Contains(row)))
        {
            errors.Add(BindingErrorCategory.Curation, row.Match, "model owner row places no generated model");
        }
    }
}
