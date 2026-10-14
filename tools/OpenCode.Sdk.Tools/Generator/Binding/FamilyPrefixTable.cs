using OpenCode.Sdk.Tools.Generator.Binding.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

/// <summary>
/// The name prefixes that place a model in a family: the family's name, its singular (one trailing
/// <c>s</c> dropped unless the family is its own group key, as <c>Vcs</c> is), its Pascal-cased group
/// keys, and the group's reasoned prefix aliases. The longest matching prefix wins, so
/// <c>PersistentPty</c> beats <c>Pty</c>. A prefix two families claim, a blank or non-identifier
/// alias, and an alias that places no model are curation errors.
/// </summary>
internal sealed class FamilyPrefixTable
{
    private readonly IReadOnlyList<PrefixEntry> _entries;
    private readonly HashSet<PrefixEntry> _usedAliases = [];

    private FamilyPrefixTable(IReadOnlyList<PrefixEntry> entries)
    {
        _entries = entries;
    }

    public static FamilyPrefixTable Create(IReadOnlySet<string> families, GenerationCuration curation, BindingErrorCollector errors)
    {
        ArgumentNullException.ThrowIfNull(families);
        ArgumentNullException.ThrowIfNull(curation);
        ArgumentNullException.ThrowIfNull(errors);

        var claims = new Dictionary<string, PrefixEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in families.Order(StringComparer.Ordinal).SelectMany(family => Entries(family, curation, errors)))
        {
            if (claims.TryAdd(entry.Prefix, entry))
            {
                continue;
            }

            var first = claims[entry.Prefix];
            if (!string.Equals(first.Family, entry.Family, StringComparison.Ordinal))
            {
                errors.Add(BindingErrorCategory.Curation, entry.Prefix,
                    $"the name prefix places models in both '{first.Family}' and '{entry.Family}'");
            }
            else if (entry.IsAlias)
            {
                errors.Add(BindingErrorCategory.Curation, entry.Prefix, $"the prefix alias repeats a prefix family '{entry.Family}' already has");
            }
        }

        return new FamilyPrefixTable(
        [
            .. claims.Values
                .OrderByDescending(static entry => entry.Prefix.Length)
                .ThenBy(static entry => entry.Prefix, StringComparer.Ordinal),
        ]);
    }

    /// <summary>Gets the family whose longest prefix starts the concept at a word boundary, or <see langword="null"/>.</summary>
    public string? FamilyOf(string concept)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(concept);

        var match = _entries.FirstOrDefault(entry => NamePrefix.Matches(concept, entry.Prefix));
        if (match is { IsAlias: true })
        {
            _ = _usedAliases.Add(match);
        }

        return match?.Family;
    }

    /// <summary>An alias that places nothing is stale: the refresh that emptied it surfaces it instead of carrying it.</summary>
    public void ReportDeadAliases(BindingErrorCollector errors)
    {
        ArgumentNullException.ThrowIfNull(errors);

        foreach (var alias in _entries.Where(entry => entry.IsAlias && !_usedAliases.Contains(entry)))
        {
            errors.Add(BindingErrorCategory.Curation, alias.Prefix, $"the prefix alias of '{alias.Family}' places no generated model");
        }
    }

    private static IEnumerable<PrefixEntry> Entries(string family, GenerationCuration curation, BindingErrorCollector errors)
    {
        yield return new PrefixEntry(family, family, IsAlias: false);
        foreach (var (key, group) in curation.Groups.OrderBy(static group => group.Key, StringComparer.Ordinal))
        {
            if (!string.Equals(group.ClientName ?? CSharpNamePolicy.ToPascalCase(key), family, StringComparison.Ordinal))
            {
                continue;
            }

            yield return new PrefixEntry(CSharpNamePolicy.ToPascalCase(key), family, IsAlias: false);
            if (family[^1] is 's' && !string.Equals(family, key, StringComparison.OrdinalIgnoreCase))
            {
                yield return new PrefixEntry(family[..^1], family, IsAlias: false);
            }

            foreach (var alias in group.PrefixAliases ?? [])
            {
                if (string.IsNullOrWhiteSpace(alias) || !CSharpNamePolicy.IsValidIdentifier(alias))
                {
                    errors.Add(BindingErrorCategory.Curation, key, $"the prefix alias '{alias}' is blank or not an identifier");
                    continue;
                }

                yield return new PrefixEntry(alias, family, IsAlias: true);
            }
        }
    }

    private sealed record PrefixEntry(string Prefix, string Family, bool IsAlias);
}
