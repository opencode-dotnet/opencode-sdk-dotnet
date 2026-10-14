using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace OpenCode.Sdk.Tools.Generator.Emission;

/// <summary>Emits the experimental mark with its id and the guide section that explains the opt-in.</summary>
internal static class ExperimentalAttributeSyntax
{
    // The published guide's address is the value the attribute carries, not a resource this code
    // reaches; the attribute needs it as a constant. The contract wins over S1075.
#pragma warning disable S1075
    private const string GuideUrlBase = "https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/experimental.md#";
#pragma warning restore S1075

    public static AttributeListSyntax[] Emit(string? diagnostic) => diagnostic is null ? [] :
    [
        EmissionSyntax.Attribute("System.Diagnostics.CodeAnalysis.Experimental",
            EmissionSyntax.StringArgument(diagnostic),
            EmissionSyntax.StringArgument(GuideUrlBase + diagnostic.ToLowerInvariant())
                .WithNameEquals(SyntaxFactory.NameEquals("UrlFormat"))),
    ];
}
