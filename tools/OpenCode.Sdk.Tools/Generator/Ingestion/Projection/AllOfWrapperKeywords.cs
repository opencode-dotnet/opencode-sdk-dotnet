using Microsoft.OpenApi;

namespace OpenCode.Sdk.Tools.Generator.Ingestion.Projection;

/// <summary>
/// The keywords no single-element <c>allOf</c> wrapper may carry, whichever wrapper it is: a
/// consumed keyword (format), a referencing or applicator keyword, an annotation that would claim
/// a meaning of its own, or anything unrecognized. The validation-only wrapper and Effect's
/// struct-with-rest wrapper each check their own structural shape and both check this one list,
/// so a keyword added here closes both paths at once.
/// </summary>
internal static class AllOfWrapperKeywords
{
    /// <summary>Whether the wrapper element carries none of the keywords no wrapper admits.</summary>
    /// <param name="element">The single <c>allOf</c> element.</param>
    /// <returns>True when none is present; title and description, which the projection ignores everywhere, never count.</returns>
    public static bool AreAbsent(OpenApiSchema element)
    {
        ArgumentNullException.ThrowIfNull(element);

        return element is
        {
            Format: null,
            Not: null,
            If: null,
            Then: null,
            Else: null,
            Contains: null,
            PropertyNames: null,
            Discriminator: null,
            Xml: null,
            ExternalDocs: null,
            Default: null,
            Deprecated: false,
            ReadOnly: false,
            WriteOnly: false,
        }
               // CS0618 marks Example obsolete for authoring, but the reader still stores a singular
               // `example` keyword there, and refusing it is this list's contract.
#pragma warning disable CS0618
               && element.Example is null
#pragma warning restore CS0618
               && element.DependentSchemas is not { Count: > 0 }
               && element.DependentRequired is not { Count: > 0 }
               && element.Definitions is not { Count: > 0 }
               && element.Examples is not { Count: > 0 }
               && element.Vocabulary is not { Count: > 0 }
               && element.Extensions is not { Count: > 0 }
               && element.UnrecognizedKeywords is not { Count: > 0 };
    }
}
