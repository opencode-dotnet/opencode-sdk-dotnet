using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace OpenCode.Sdk.Tests.Support;

/// <summary>
/// The SDK's namespace rule, which replaces IDE0130 for the SDK project: a file under any
/// <c>Internal/</c> folder declares <c>OpenCode.Sdk.Internal</c> and no public type; every other
/// file declares <c>OpenCode.Sdk</c> or <c>OpenCode.Sdk.Models</c> and only public top-level
/// types. Folders are placement and namespaces are API surface, so moving a file never changes a
/// namespace. A file the rule cannot classify is refused.
/// </summary>
internal static class SdkNamespacePolicy
{
    public const string InternalNamespace = "OpenCode.Sdk.Internal";

    private const string RootNamespace = "OpenCode.Sdk";

    private const string InternalFolder = "Internal";

    private static readonly string[] PublicNamespaces = [RootNamespace, "OpenCode.Sdk.Models"];

    /// <summary>Gets the files that declare no namespace and no type by design.</summary>
    public static IReadOnlyList<string> ExemptFiles { get; } = ["Properties/AssemblyInfo.cs"];

    /// <summary>Returns why <paramref name="file"/> breaks the rule, or <see langword="null"/> when it keeps it.</summary>
    public static string? Violation(SdkSourceFile file)
    {
        if (ExemptFiles.Contains(file.RelativePath, StringComparer.Ordinal))
        {
            return null;
        }

        var declarations = Parse(file);
        if (declarations.Namespace is not { } declaredNamespace || declarations.TopLevelTypes.Count is 0)
        {
            return $"{file.RelativePath}: declares no file-scoped namespace or no top-level type";
        }

        return file.Folders.Contains(InternalFolder, StringComparer.Ordinal)
            ? InternalFileViolation(file, declaredNamespace, declarations.TopLevelTypes)
            : PublicFileViolation(file.RelativePath, declaredNamespace, declarations.TopLevelTypes);
    }

    /// <summary>
    /// Reads the namespace and the top-level types a file declares from its syntax tree, so modifiers
    /// on their own lines and attributes between them classify the same as a one-line declaration.
    /// </summary>
    public static SourceDeclarations Parse(SdkSourceFile file)
    {
        var root = CSharpSyntaxTree.ParseText(string.Join('\n', file.Lines)).GetCompilationUnitRoot();
        var namespaceDeclaration = root.Members.OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
        var members = namespaceDeclaration?.Members ?? root.Members;
        return new SourceDeclarations(
            namespaceDeclaration?.Name.ToString(),
            [
                .. members
                    .Where(static member => member is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax)
                    .Select(static member => new TopLevelType(
                        member switch
                        {
                            BaseTypeDeclarationSyntax type => type.Identifier.ValueText,
                            DelegateDeclarationSyntax del => del.Identifier.ValueText,
                            _ => throw new InvalidOperationException("Only type and delegate declarations reach here."),
                        },
                        member.Modifiers.Any(SyntaxKind.PublicKeyword))),
            ]);
    }

    private static string? InternalFileViolation(SdkSourceFile file, string declaredNamespace, IReadOnlyList<TopLevelType> types)
    {
        if (!string.Equals(declaredNamespace, InternalNamespace, StringComparison.Ordinal))
        {
            return $"{file.RelativePath}: an internal file declares '{declaredNamespace}', not '{InternalNamespace}'";
        }

        return types.FirstOrDefault(static type => type.IsPublic) is { } exposed
            ? $"{file.RelativePath}: an internal file declares the public type '{exposed.Name}'"
            : null;
    }

    private static string? PublicFileViolation(string relativePath, string declaredNamespace, IReadOnlyList<TopLevelType> types)
    {
        if (!PublicNamespaces.Contains(declaredNamespace, StringComparer.Ordinal))
        {
            return $"{relativePath}: a public file declares '{declaredNamespace}', not one of the public namespaces";
        }

        return types.FirstOrDefault(static type => !type.IsPublic) is { } hidden
            ? $"{relativePath}: the type '{hidden.Name}' is not public"
            : null;
    }

    /// <summary>The file-scoped namespace and the top-level types one source file declares.</summary>
    internal sealed record SourceDeclarations(string? Namespace, IReadOnlyList<TopLevelType> TopLevelTypes);

    /// <summary>One top-level type: its name and whether it is declared public.</summary>
    internal sealed record TopLevelType(string Name, bool IsPublic);
}
