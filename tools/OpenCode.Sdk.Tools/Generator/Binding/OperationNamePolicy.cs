using System.Collections.Frozen;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

/// <summary>
/// Derives C# member and type names for one bound operation from its identifier segments.
/// Verb detection is structural — only the final segment can be a verb, and only a segment of the
/// closed grammar is one — so mid-position segments that spell a verb stay in the subject. A
/// <c>GET</c> without a grammar verb is a read and names <c>Get…</c>; every other operation
/// without one has no mechanical name, because the HTTP method is never a name source
/// (ADR-0008): the binder refuses it until a reason-bearing <c>operationNames</c> row names it.
/// Lists pluralize the last subject word, with a closed table for irregular forms.
/// </summary>
internal static class OperationNamePolicy
{
    private const string ResponseSuffix = "Response";
    private const string ReadVerb = "Get";

    /// <summary>
    /// The closed grammar: identifier segments recognized as operation verbs when they close the
    /// identifier. A change here is an ADR-0008 revision, not curation.
    /// </summary>
    private static readonly string[] KnownVerbSegments = ["create", "get", "list", "remove", "rename", "timeout", "update"];

    private static readonly FrozenDictionary<string, string> IrregularPlurals =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["Pty"] = "Ptys", ["Inbox"] = "Inbox", ["Saved"] = "Saved" }
            .ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// Gets the operation verb: the closing grammar segment, <c>Get</c> for a read without one,
    /// or <see langword="null"/> when no mechanical rule may name the operation.
    /// </summary>
    public static string? Verb(SpecOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (HasVerbSegment(operation))
        {
            return CSharpNamePolicy.ToPascalCase(operation.Segments[^1]);
        }

        return IsRead(operation) ? ReadVerb : null;
    }

    /// <summary>
    /// The method name: the curated one when a row exists, else verb plus subject. On a handle
    /// client the handle is the subject, so an empty subject stays empty (<c>session.GetAsync()</c>);
    /// on a collection client it falls back to the family (<c>sessions.ListSessionsAsync()</c>).
    /// </summary>
    public static string? MethodName(SpecOperation operation, OperationNameCuration? curation = null, bool handle = false)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (curation is not null)
        {
            return curation.MethodName;
        }

        var verb = Verb(operation);
        if (verb is null)
        {
            return null;
        }

        var subject = handle ? Subject(operation, pluralize: true) : SubjectOrGroupFallback(operation, verb);
        return $"{verb}{subject}Async";
    }

    /// <summary>
    /// The refusal for an operation no mechanical rule may name: the closing segment, the
    /// missing row, and the fallback the HTTP method would have produced. Null when a mechanical
    /// name exists.
    /// </summary>
    public static string? RowRequiredProblem(SpecOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (Verb(operation) is not null)
        {
            return null;
        }

        return $"operation '{operation.OperationId}' closes with '{operation.Segments[^1]}', which is not a naming verb, "
               + "and no operationNames row names it; the HTTP method is never a name source (ADR-0008)";
    }

    /// <summary>Replaces a list operation's verb with the reviewed automatic-traversal verb.</summary>
    public static string? EnumerationMethodName(string methodName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(methodName);

        return methodName.StartsWith("List", StringComparison.Ordinal)
               && methodName.EndsWith("Async", StringComparison.Ordinal)
               && methodName.Length > "ListAsync".Length
            ? $"Enumerate{methodName[4..]}"
            : null;
    }

    /// <summary>Group, subject, and the grammar verb (a read's <c>Get</c> and an absent verb fold to nothing).</summary>
    public static string ResponseTypeName(SpecOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        return $"{CSharpNamePolicy.ToPascalCase(operation.Segments[0])}{Subject(operation)}{TypeVerbSuffix(operation)}{ResponseSuffix}";
    }

    /// <summary>
    /// Names the model a promoted inline envelope payload becomes: the response spine's stem
    /// plus <c>Data</c>. Such a payload has no schema identity of its own — the wrapper it was
    /// promoted out of is response spine the dialect never names — so it takes the operation's,
    /// and no upstream wrapper spelling reaches the public surface.
    /// </summary>
    public static string PayloadTypeName(SpecOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var responseTypeName = ResponseTypeName(operation);
        return $"{responseTypeName[..^ResponseSuffix.Length]}Data";
    }

    /// <summary>
    /// Route members never restate a root container; client-placed members mirror their
    /// method names so merged client families stay collision-free.
    /// </summary>
    public static string? RouteMemberName(SpecOperation operation, GroupPlacement placement,
        OperationNameCuration? curation = null, bool handle = false)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (curation is not null)
        {
            return curation.MethodName.EndsWith("Async", StringComparison.Ordinal)
                   && curation.MethodName.Length > 5
                ? curation.MethodName[..^5]
                : null;
        }

        var verb = Verb(operation);
        if (verb is null)
        {
            return null;
        }

        if (placement is GroupPlacement.Root || handle)
        {
            return $"{verb}{Subject(operation, pluralize: true)}";
        }

        var subject = SubjectOrGroupFallback(operation, verb);
        return $"{verb}{subject}";
    }

    public static string PayloadName(SpecOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var subject = Subject(operation, pluralize: true);
        return subject.Length is 0 ? GroupFallback(operation, Verb(operation)) : subject;
    }

    /// <summary>Folds the verb exactly like the response name, so the pair stays uniform.</summary>
    public static string RequestTypeName(SpecOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        return $"{CSharpNamePolicy.ToPascalCase(operation.Segments[0])}{Subject(operation)}{TypeVerbSuffix(operation)}Request";
    }

    private static string TypeVerbSuffix(SpecOperation operation)
    {
        var verb = Verb(operation);
        return verb is null || string.Equals(verb, ReadVerb, StringComparison.Ordinal) ? string.Empty : verb;
    }

    /// <summary>
    /// The operation's subject: the segments between the group and the closing verb.
    /// Only a recognized grammar verb drops; the HTTP method never removes a segment.
    /// </summary>
    private static string Subject(SpecOperation operation, bool pluralize = false)
    {
        var end = operation.Segments.Count;
        if (HasVerbSegment(operation))
        {
            end--;
        }

        var subject = string.Concat(operation.Segments.Skip(1).Take(end - 1).Select(CSharpNamePolicy.ToPascalCase));
        return pluralize && subject.Length > 0 && string.Equals(Verb(operation), "List", StringComparison.Ordinal)
            ? Pluralize(subject)
            : subject;
    }

    private static string SubjectOrGroupFallback(SpecOperation operation, string verb)
    {
        var subject = Subject(operation, pluralize: true);
        return subject.Length is 0 ? GroupFallback(operation, verb) : subject;
    }

    /// <summary>An empty subject falls back to the group — pluralized for list operations.</summary>
    private static string GroupFallback(SpecOperation operation, string? verb)
    {
        var group = CSharpNamePolicy.ToPascalCase(operation.Segments[0]);
        return string.Equals(verb, "List", StringComparison.Ordinal) ? Pluralize(group) : group;
    }

    /// <summary>Pluralizes only the last word, preserving the preceding subject words.</summary>
    private static string Pluralize(string subject)
    {
        var word = CSharpNamePolicy.SplitWords(subject)[^1];
        var prefix = subject[..^word.Length];
        if (IrregularPlurals.TryGetValue(word, out var irregular))
        {
            return $"{prefix}{irregular}";
        }

        if (word[^1] is 's' or 'x' or 'z'
            || word.EndsWith("ch", StringComparison.Ordinal) || word.EndsWith("sh", StringComparison.Ordinal))
        {
            return $"{subject}es";
        }

        if (word.Length > 1 && word[^1] is 'y' && !IsVowel(word[^2]))
        {
            return $"{subject[..^1]}ies";
        }

        return $"{subject}s";
    }

    private static bool IsVowel(char letter) => letter is 'a' or 'e' or 'i' or 'o' or 'u' or 'A' or 'E' or 'I' or 'O' or 'U';

    private static bool IsRead(SpecOperation operation) =>
        string.Equals(operation.Method, "get", StringComparison.OrdinalIgnoreCase);

    private static bool HasVerbSegment(SpecOperation operation) =>
        operation.Segments.Count > 1 && KnownVerbSegments.Contains(operation.Segments[^1], StringComparer.Ordinal);
}
