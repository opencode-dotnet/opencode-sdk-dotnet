using System.Globalization;
using System.IO.Abstractions;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.DependencyInjection;

namespace OpenCode.Sdk.Tools.Tests.RepositoryGates;

internal sealed partial class SnippetCompileGuard(IFileSystem fileSystem, SnippetCompileGuard.SnippetCompiler compiler)
{
    public async Task<IReadOnlyList<string>> FindAsync(string root)
    {
        var guide = fileSystem.Path.Combine(root, "docs", "guide");
        IEnumerable<string> pages = [fileSystem.Path.Combine(root, "README.md")];
        if (fileSystem.Directory.Exists(guide))
        {
            pages = pages.Concat(fileSystem.Directory.EnumerateFiles(guide, "*.md", SearchOption.TopDirectoryOnly));
        }

        var violations = new List<string>();
        foreach (var page in pages.Order(StringComparer.Ordinal))
        {
            var name = fileSystem.Path.GetRelativePath(root, page).Replace('\\', '/');
            var blocks = SnippetExtractor.Read(name, await fileSystem.File.ReadAllLinesAsync(page), violations);
            violations.AddRange(compiler.CompilePage(blocks));
        }

        return violations;
    }

    internal sealed record SnippetBlock(string Page, int FenceLine, string Source);

    internal static partial class SnippetExtractor
    {
        private const string Fragment = "<!-- snippet: fragment -->";
        [GeneratedRegex("^ {0,3}(?<fence>`{3,}|~{3,})(?<info>.*)$", RegexOptions.NonBacktracking, matchTimeoutMilliseconds: Timeout.Infinite)]
        private static partial Regex Fence();

        public static SnippetBlock[] Read(string page, string[] lines, ICollection<string> violations)
        {
            var blocks = new List<SnippetBlock>();
            string? fence = null;
            var start = 0;
            var csharp = false;
            for (var index = 0; index < lines.Length; index++)
            {
                if (fence is null && lines[index] == Fragment
                    && (index + 1 == lines.Length || Fence().Match(lines[index + 1]).Groups["info"].Value.Trim() != "csharp"))
                {
                    violations.Add(string.Create(CultureInfo.InvariantCulture, $"{page}:{index + 1}: fragment marker must be directly followed by a csharp fence."));
                }

                var match = Fence().Match(lines[index]);
                if (!match.Success)
                {
                    continue;
                }

                if (fence is null)
                {
                    fence = match.Groups["fence"].Value;
                    start = index;
                    var info = match.Groups["info"].Value.Trim();
                    csharp = info == "csharp";
                    if (!csharp && (info.Equals("cs", StringComparison.OrdinalIgnoreCase)
                        || info.Equals("c#", StringComparison.OrdinalIgnoreCase) || info.Equals("csharp", StringComparison.OrdinalIgnoreCase)))
                    {
                        violations.Add(string.Create(CultureInfo.InvariantCulture, $"{page}:{index + 1}: write csharp instead of '{info}' for a C# fence."));
                    }
                }
                else if (match.Groups["fence"].Value.StartsWith(fence, StringComparison.Ordinal) && string.IsNullOrWhiteSpace(match.Groups["info"].Value))
                {
                    if (csharp && !(start > 0 && lines[start - 1] == Fragment))
                    {
                        blocks.Add(new SnippetBlock(page, start + 1, string.Join('\n', lines.Skip(start + 1).Take(index - start - 1))));
                    }

                    fence = null;
                }
            }

            return [.. blocks];
        }
    }

    internal sealed class SnippetCompiler(IFileSystem fileSystem)
    {
        private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.CSharp12);
        private static readonly CSharpCompilationOptions CompilationOptions = new(OutputKind.DynamicallyLinkedLibrary,
            nullableContextOptions: NullableContextOptions.Enable,
            specificDiagnosticOptions: new Dictionary<string, ReportDiagnostic>(StringComparer.Ordinal)
            {
                ["OPENCODE001"] = ReportDiagnostic.Suppress,
                ["OPENCODE002"] = ReportDiagnostic.Suppress,
            });
        private const string Imports = """
            using System;
            using System.Collections.Generic;
            using System.Globalization;
            using System.IO;
            using System.Linq;
            using System.Net.Http;
            using System.Threading;
            using System.Threading.Channels;
            using System.Threading.Tasks;
            using Microsoft.Extensions.Hosting;
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.Logging;
            using OpenCode.Sdk;
            using OpenCode.Sdk.Models;

            """;
        private const string Ambient = """
            static OpenCodeClient client;
            static CancellationToken cancellationToken;
            static string sessionId;
            static SessionClient session;
            static PtyClient pty;
            static PersistentPtyClient terminal;
            static PtySession ptySession;
            static string[] args;
            static string projectId;
            static string worktree;
            static SessionsClient sessions;
            static SessionCreateResponse created;
            static string boundaryMessageId;
            static PermissionRule rule;
            static OpenCodeServer server;
            static CancellationTokenSource window;
            static string code;
            static Uri endpoint;
            static int port;
            static string password;
            static TimeSpan readinessTimeout;
            static TimeSpan retryInterval;
            static CancellationTokenSource deadline;
            static SessionResponse response;
            static IToolContent content;
            static SessionMessageListRequest request;
            static AgentInfo info;
            static VcsMode mode;
            static long? cursor;
            static Stream sink;
            static bool EndsTurn(IEvent @event, string sessionId) => throw new NotSupportedException();
            static string? SessionOf(IEvent @event) => throw new NotSupportedException();
            static Task RefreshStateAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

            """;
        private readonly MetadataReference[] _references =
        [
            .. ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
                ?? throw new InvalidOperationException("The runtime did not expose trusted platform assemblies."))
                .Split(fileSystem.Path.PathSeparator)
                .Where(path => fileSystem.Path.GetDirectoryName(path) == fileSystem.Path.GetDirectoryName(typeof(object).Assembly.Location)
                    || fileSystem.Path.GetFileNameWithoutExtension(path) is
                        "Microsoft.Extensions.Hosting" or
                        "Microsoft.Extensions.Hosting.Abstractions" or
                        "Microsoft.Extensions.DependencyInjection" or
                        "Microsoft.Extensions.DependencyInjection.Abstractions" or
                        "Microsoft.Extensions.Logging" or
                        "Microsoft.Extensions.Logging.Abstractions" or
                        "Microsoft.Extensions.Options" or
                        "Microsoft.Extensions.Configuration" or
                        "Microsoft.Extensions.Configuration.Abstractions" or
                        "Microsoft.Extensions.Primitives")
                .Append(typeof(OpenCodeClient).Assembly.Location)
                .Append(typeof(OpenCodeServiceCollectionExtensions).Assembly.Location)
                .Distinct(StringComparer.Ordinal)
                .Select(static path => MetadataReference.CreateFromFile(path)),
        ];

        public IReadOnlyList<string> CompilePage(IReadOnlyList<SnippetBlock> blocks)
        {
            var units = blocks.ToDictionary(static block => block,
                static block => SyntaxFactory.ParseCompilationUnit(block.Source, options: ParseOptions));
            var typeSets = units.Where(static pair => pair.Value.Members.Count > 0
                && pair.Value.Members.All(static member => member is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax or BaseNamespaceDeclarationSyntax))
                .ToDictionary(static pair => pair.Key, static pair => TypeSet(pair.Key, pair.Value));
            var origins = typeSets.ToDictionary(static pair => pair.Value, static pair => pair.Key);
            var violations = new List<string>();
            foreach (var block in blocks)
            {
                var unit = units[block];
                if (unit.Members.Any(static member => member is GlobalStatementSyntax)
                    && unit.Members.Any(static member => member is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax or BaseNamespaceDeclarationSyntax))
                {
                    violations.Add(string.Create(CultureInfo.InvariantCulture, $"{block.Page}:{block.FenceLine}: mixed statements and type declarations must use separate fences."));
                    continue;
                }

                var trees = typeSets.Values.AsEnumerable();
                if (!typeSets.ContainsKey(block))
                {
                    var tree = MethodBody(block, unit);
                    origins[tree] = block;
                    trees = trees.Append(tree);
                }

                var compilation = CSharpCompilation.Create("Snippet", trees, _references, CompilationOptions);
                foreach (var error in compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
                {
                    violations.Add(Format(error.Location.SourceTree is { } source ? origins[source] : block, error));
                }
            }

            return [.. violations.Distinct(StringComparer.Ordinal)];
        }

        private static SyntaxTree MethodBody(SnippetBlock block, CompilationUnitSyntax unit)
        {
            var body = block.Source.ToCharArray();
            foreach (var directive in unit.Usings)
            {
                for (var index = directive.SpanStart; index < directive.Span.End; index++)
                {
                    if (body[index] is not ('\r' or '\n'))
                    {
                        body[index] = ' ';
                    }
                }
            }

            var imports = string.Concat(unit.Usings.Select(directive =>
                string.Create(CultureInfo.InvariantCulture, $"#line {block.FenceLine + 1 + unit.SyntaxTree.GetLineSpan(directive.FullSpan).StartLinePosition.Line} \"{block.Page}\"\n")
                + directive.ToFullString() + "\n"));
            return CSharpSyntaxTree.ParseText(Imports + imports
                + "#line default\nstatic class Example {\n" + Ambient + "static async Task ExampleAsync() {\n"
                + string.Create(CultureInfo.InvariantCulture, $"#line {block.FenceLine + 1} \"{block.Page}\"\n")
                + new string(body) + "\n#line default\n} }", ParseOptions);
        }

        private static SyntaxTree TypeSet(SnippetBlock block, CompilationUnitSyntax unit) =>
            CSharpSyntaxTree.ParseText(Imports
                + string.Create(CultureInfo.InvariantCulture, $"#line {block.FenceLine + 1} \"{block.Page}\"\n")
                + unit.ToFullString(), ParseOptions);

        private static string Format(SnippetBlock block, Diagnostic error)
        {
            var span = error.Location.GetMappedLineSpan();
            var line = span.HasMappedPath ? span.StartLinePosition.Line + 1 : block.FenceLine;
            return string.Create(CultureInfo.InvariantCulture, $"{block.Page}:{line}: {error.Id}: {error.GetMessage(CultureInfo.InvariantCulture)}");
        }
    }
}
