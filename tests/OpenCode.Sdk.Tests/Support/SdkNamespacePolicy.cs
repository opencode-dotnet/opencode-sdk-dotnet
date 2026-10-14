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

    /// <summary>
    /// Gets the hand-written internal folders whose files may still declare their folder-derived
    /// namespace. Each row names the area it belongs to; a row goes when its area is flattened into
    /// <c>OpenCode.Sdk.Internal</c>, and the list goes with the last row.
    /// </summary>
    public static IReadOnlyList<string> LegacyNamespaceFolders { get; } =
    [
        "Internal/BackgroundService", // Hosting background service
        "Internal/Launcher", // Hosting launcher
        "Internal/Posix", // Hosting platform interop
        "Internal/Windows", // Hosting platform interop
    ];

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

    /// <summary>Returns the legacy folders that no longer hold a file declaring a namespace other than <see cref="InternalNamespace"/>.</summary>
    public static IReadOnlyList<string> StaleLegacyNamespaceFolders(IEnumerable<SdkSourceFile> files)
    {
        var legacyPaths = files
            .Where(static file => Parse(file).Namespace is { } declared && !string.Equals(declared, InternalNamespace, StringComparison.Ordinal))
            .Select(static file => file.RelativePath)
            .ToArray();

        return [.. LegacyNamespaceFolders.Where(folder => !legacyPaths.Any(path => IsUnder(path, folder)))];
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
        var legacy = LegacyNamespaceFolders.Any(folder => IsUnder(file.RelativePath, folder));
        var namespaceAllowed = string.Equals(declaredNamespace, InternalNamespace, StringComparison.Ordinal)
                               || (legacy && string.Equals(declaredNamespace, FolderNamespace(file), StringComparison.Ordinal));
        if (!namespaceAllowed)
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

    /// <summary>The namespace IDE0130 derives from the folder, which the legacy folders still use.</summary>
    private static string FolderNamespace(SdkSourceFile file) => string.Join('.', file.Folders.Prepend(RootNamespace));

    private static bool IsUnder(string relativePath, string folder) =>
        relativePath.StartsWith($"{folder}/", StringComparison.Ordinal);

    /// <summary>The file-scoped namespace and the top-level types one source file declares.</summary>
    internal sealed record SourceDeclarations(string? Namespace, IReadOnlyList<TopLevelType> TopLevelTypes);

    /// <summary>One top-level type: its name and whether it is declared public.</summary>
    internal sealed record TopLevelType(string Name, bool IsPublic);
}
