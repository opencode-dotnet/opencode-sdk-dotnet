using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace OpenCode.Sdk.Tools.Tests.Packaging;

/// <summary>Uses manifest ownership and C# declarations, not filename guesses, to identify types.</summary>
internal sealed class GeneratedTypeCatalog
{
    private readonly CSharpParseOptions _parseOptions = new(LanguageVersion.CSharp14);

    public IReadOnlyList<CompatibilityTypeDeclaration> ReadDeclarations(string source, string path, bool generated)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var tree = CSharpSyntaxTree.ParseText(source, _parseOptions, path);
        var errors = tree.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
        if (errors.Length > 0)
        {
            throw new InvalidOperationException($"Cannot classify C# declarations in '{path}': {string.Join<Diagnostic>("; ", errors)}");
        }

        var root = tree.GetCompilationUnitRoot();
        if (generated && (root.Members.Count != 1 || root.Members[0] is not FileScopedNamespaceDeclarationSyntax))
        {
            throw new InvalidOperationException($"Generated file '{path}' must declare a file-scoped namespace.");
        }

        return [.. root.DescendantNodes()
            .Where(node => node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax
                && (!generated || !node.Ancestors().OfType<BaseTypeDeclarationSyntax>().Any()))
            .Select(node => new CompatibilityTypeDeclaration(FullName(node),
                node is TypeDeclarationSyntax type && type.Modifiers.Any(SyntaxKind.PartialKeyword)))];
    }

    private static string FullName(SyntaxNode declaration)
    {
        var ancestors = declaration.Ancestors().Reverse().ToArray();
        var namespaces = ancestors.OfType<BaseNamespaceDeclarationSyntax>()
            .Select(static item => string.Concat(item.Name.DescendantTokens().Select(static token => token.ValueText)));
        var types = ancestors.OfType<BaseTypeDeclarationSyntax>().Select(TypeName).Append(TypeName(declaration));
        return string.Join('.', namespaces.Concat(types));
    }

    private static string TypeName(SyntaxNode declaration)
    {
        var identifier = declaration switch
        {
            BaseTypeDeclarationSyntax type => type.Identifier.ValueText,
            DelegateDeclarationSyntax type => type.Identifier.ValueText,
            _ => throw new InvalidOperationException($"Unsupported type declaration '{declaration.Kind()}'."),
        };
        var arity = declaration switch
        {
            TypeDeclarationSyntax type => type.TypeParameterList?.Parameters.Count ?? 0,
            DelegateDeclarationSyntax type => type.TypeParameterList?.Parameters.Count ?? 0,
            _ => 0,
        };
        return arity == 0 ? identifier : identifier + "`" + arity.ToString(CultureInfo.InvariantCulture);
    }
}
