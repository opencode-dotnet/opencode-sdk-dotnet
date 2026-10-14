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

    private static readonly string[] TypeKeywords = ["class", "struct", "interface", "enum", "record", "delegate"];

    private static readonly char[] WordSeparators = [' ', '(', '<', ':'];

    /// <summary>Gets the files that declare no namespace and no type by design.</summary>
    public static IReadOnlyList<string> ExemptFiles { get; } = ["Properties/AssemblyInfo.cs"];

    /// <summary>
    /// Gets the hand-written internal folders whose files may still declare their folder-derived
    /// namespace. Each row names the area it belongs to; a row goes when its area is flattened into
    /// <c>OpenCode.Sdk.Internal</c>, and the list goes with the last row.
    /// </summary>
    public static IReadOnlyList<string> LegacyNamespaceFolders { get; } =
    [
        "Internal/Abstractions", // root Internal machinery
        "Internal/BackgroundService", // Hosting background service
        "Internal/Diagnostics", // root Internal machinery
        "Internal/Launcher", // Hosting launcher
        "Internal/Posix", // Hosting platform interop
        "Internal/Windows", // Hosting platform interop
    ];

    /// <summary>
    /// Gets the internal raw clients that still sit in their family's public folder. They move under
    /// the family's <c>Internal/</c> folder when the generated files move, and their rows go with them.
    /// </summary>
    public static IReadOnlyList<string> InternalFilesInPublicFolders { get; } =
    [
        "PersistentPtys/PersistentPtyRawClient.cs",
        "PersistentPtys/PersistentPtysRawClient.cs",
        "Ptys/PtyRawClient.cs",
        "Ptys/PtysRawClient.cs",
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

        var internalFile = file.Folders.Contains(InternalFolder, StringComparer.Ordinal)
                           || InternalFilesInPublicFolders.Contains(file.RelativePath, StringComparer.Ordinal);
        return internalFile
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

    /// <summary>Returns the listed internal files in public folders that no longer exist.</summary>
    public static IReadOnlyList<string> StaleInternalFilesInPublicFolders(IEnumerable<SdkSourceFile> files)
    {
        var paths = files.Select(static file => file.RelativePath).ToHashSet(StringComparer.Ordinal);

        return [.. InternalFilesInPublicFolders.Where(path => !paths.Contains(path))];
    }

    /// <summary>Reads the file-scoped namespace and the top-level types a file declares.</summary>
    public static SourceDeclarations Parse(SdkSourceFile file) =>
        new(FileScopedNamespace(file.Lines), [.. file.Lines.Select(TopLevelTypeOf).OfType<TopLevelType>()]);

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

    private static string? FileScopedNamespace(IEnumerable<string> lines) => lines
        .Select(static line => line.TrimEnd('\r'))
        .Where(static line => line.StartsWith("namespace ", StringComparison.Ordinal) && line[^1] is ';')
        .Select(static line => line["namespace ".Length..^1].Trim())
        .FirstOrDefault();

    /// <summary>
    /// File-scoped namespaces put every top-level declaration at column zero, so a line that starts
    /// with a modifier or a type keyword there declares a top-level type.
    /// </summary>
    private static TopLevelType? TopLevelTypeOf(string line)
    {
        if (line.Length is 0 || char.IsWhiteSpace(line[0]) || !char.IsLetter(line[0]))
        {
            return null;
        }

        var words = line.TrimEnd('\r').Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries);
        var keyword = Array.FindIndex(words, static word => TypeKeywords.Contains(word, StringComparer.Ordinal));
        if (keyword < 0 || words.Take(keyword).Any(static word => word is "namespace" or "using"))
        {
            return null;
        }

        // 'record struct' and 'record class' name the type one word later; a delegate names it before its parameters.
        var nameIndex = words[keyword] switch
        {
            "record" when keyword + 1 < words.Length && words[keyword + 1] is "struct" or "class" => keyword + 2,
            "delegate" => keyword + 2,
            _ => keyword + 1,
        };
        return nameIndex < words.Length
            ? new TopLevelType(words[nameIndex], words.Take(keyword).Contains("public", StringComparer.Ordinal))
            : null;
    }

    /// <summary>The file-scoped namespace and the top-level types one source file declares.</summary>
    internal sealed record SourceDeclarations(string? Namespace, IReadOnlyList<TopLevelType> TopLevelTypes);

    /// <summary>One top-level type: its name and whether it is declared public.</summary>
    internal sealed record TopLevelType(string Name, bool IsPublic);
}
