using OpenCode.Sdk.Tools.Generator.Binding.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

/// <summary>
/// The reasoned areas the Sessions family's models split into. A Sessions model whose name one of
/// an area's word-boundary prefixes matches lives in that area's folder; a model no pattern matches
/// stays in the family's model folder. An area name that is blank, reserved or repeated, a pattern
/// repeated across areas, a model two areas claim, and a pattern that places nothing are curation
/// errors.
/// </summary>
internal sealed class SessionAreaRows
{
    /// <summary>The one family whose models split into areas.</summary>
    public const string AreaFamily = "Sessions";

    private static readonly string[] ReservedAreaNames = ["Common", "Internal", "Hosting", "Properties", "Models"];

    private readonly IReadOnlyList<SessionAreaCuration> _rows;
    private readonly HashSet<string> _usedPatterns = new(StringComparer.Ordinal);

    private SessionAreaRows(IReadOnlyList<SessionAreaCuration> rows)
    {
        _rows = rows;
    }

    public static SessionAreaRows Create(IReadOnlyList<SessionAreaCuration> rows, IReadOnlySet<string> families,
        BindingErrorCollector errors)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(families);
        ArgumentNullException.ThrowIfNull(errors);

        if (rows.Count > 0 && !families.Contains(AreaFamily))
        {
            errors.Add(BindingErrorCategory.Curation, "sessionAreas", $"session areas need the '{AreaFamily}' family");
            return new SessionAreaRows([]);
        }

        var areas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var patterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var valid = new List<SessionAreaCuration>();
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.Area) || !CSharpNamePolicy.IsValidIdentifier(row.Area)
                                                    || ReservedAreaNames.Contains(row.Area, StringComparer.OrdinalIgnoreCase)
                                                    || !areas.Add(row.Area))
            {
                errors.Add(BindingErrorCategory.Curation, string.IsNullOrWhiteSpace(row.Area) ? "sessionAreas" : row.Area,
                    "session area name is empty, reserved, not an identifier, or duplicated");
                continue;
            }

            if (string.IsNullOrWhiteSpace(row.Reason))
            {
                errors.Add(BindingErrorCategory.Curation, row.Area, "session area row must declare a reason");
            }

            if (row.Matches.Count is 0 || row.Matches.Any(pattern => string.IsNullOrWhiteSpace(pattern) || !patterns.Add(pattern)))
            {
                errors.Add(BindingErrorCategory.Curation, row.Area, "session area patterns are empty or repeat another pattern");
                continue;
            }

            valid.Add(row);
        }

        return new SessionAreaRows(valid);
    }

    /// <summary>Gets the area the subject's name places it in, or <see langword="null"/> when no pattern matches.</summary>
    public string? AreaOf(OwnershipSubject subject, BindingErrorCollector errors)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(errors);

        var matches = _rows
            .SelectMany(static row => row.Matches.Select(pattern => new AreaPattern(row.Area, pattern)))
            .Where(entry => subject.Matches(entry.Pattern))
            .ToArray();
        if (matches.Select(static entry => entry.Area).Distinct(StringComparer.Ordinal).Count() > 1)
        {
            errors.Add(BindingErrorCategory.Curation, subject.Name,
                $"session area patterns '{string.Join("', '", matches.Select(static entry => entry.Pattern))}' place the model in more than one area");
        }

        _usedPatterns.UnionWith(matches.Select(static entry => entry.Pattern));
        return matches.FirstOrDefault()?.Area;
    }

    /// <summary>A pattern that places nothing is stale: the refresh that emptied it surfaces it instead of carrying it.</summary>
    public void ReportDeadPatterns(BindingErrorCollector errors)
    {
        ArgumentNullException.ThrowIfNull(errors);

        foreach (var entry in _rows
                     .SelectMany(static row => row.Matches.Select(pattern => new AreaPattern(row.Area, pattern)))
                     .Where(entry => !_usedPatterns.Contains(entry.Pattern)))
        {
            errors.Add(BindingErrorCategory.Curation, $"{entry.Area}/{entry.Pattern}", "session area pattern places no generated model");
        }
    }

    private sealed record AreaPattern(string Area, string Pattern);
}
