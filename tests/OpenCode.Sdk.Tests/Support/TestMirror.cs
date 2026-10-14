namespace OpenCode.Sdk.Tests.Support;

/// <summary>
/// The test mirror rule: a test sits at its subject's source path with every <c>Internal</c>
/// segment dropped, so <c>Sessions/Internal/X.cs</c> is tested in <c>Sessions/XTests.cs</c>. Two
/// source types that project onto one test path would leave a test without one obvious home, so
/// such a pair is a collision. Partial parts of one type project together and are one subject.
/// </summary>
internal static class TestMirror
{
    private const string InternalFolder = "Internal";

    /// <summary>Returns the test path <paramref name="file"/> projects onto.</summary>
    public static string TestPath(SdkSourceFile file) => string.Join(
        '/',
        file.Folders
            .Where(static folder => !string.Equals(folder, InternalFolder, StringComparison.Ordinal))
            .Append($"{file.TypeName}Tests.cs"));

    /// <summary>Returns one description per test path that more than one subject projects onto, naming the subjects in ordinal order.</summary>
    public static IReadOnlyList<string> Collisions(IEnumerable<SdkSourceFile> files) =>
    [
        .. files
            .GroupBy(TestPath, StringComparer.Ordinal)
            .Select(static projection => projection
                .Select(static file => string.Join('/', file.Folders.Append(file.TypeName)))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray())
            .Where(static subjects => subjects.Length > 1)
            .Select(static subjects => string.Join(" and ", subjects)),
    ];
}
