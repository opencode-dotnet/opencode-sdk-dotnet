using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace OpenCode.Sdk.Tools.Tests.Generator.Emission.Support;

internal static class SessionScopedEventConsumerSyntax
{
    public static SyntaxTree CreateTree() => CSharpSyntaxTree.ParseText(Source, new CSharpParseOptions(LanguageVersion.CSharp14));

    private const string Source = """
        using OpenCode.Sdk.Models;

        public static class Consumer
        {
            public static string? Route(IEvent e)
            {
                if (e is ISessionScopedEvent { SessionId: var id })
                {
                    return id;
                }

                return null;
            }

            public static ISessionScopedEvent Scope(SessionCreated e) => e;
        }
        """;
}
