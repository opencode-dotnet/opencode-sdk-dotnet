using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests;

/// <summary>
/// Folders the vertical-slice layout emptied stay empty: a file added to one, for example by a
/// branch that started before the move, fails here with the folder its kind now lives in. A file
/// that still sits in a vacated folder until its own slice moves it is listed by name, and the
/// listing refuses once that file is gone.
/// </summary>
public sealed class LayoutGuardTests
{
    private const string Resident = "Models/IOpenCodeStreamFailureCause.cs";

    private static readonly VacatedFolder[] VacatedFolders =
    [
        new("Models", "the owning family's Models/ folder, or Common/Errors/ for the shared error spine"),
        new("Internal/ResponseAdapters", "the owning family's Internal/ folder"),
        new("Internal/StreamAdapters", "the owning family's Internal/ folder"),
    ];

    /// <summary>Hand-written files still in a vacated folder; each leaves with the slice that moves its area.</summary>
    private static readonly string[] Residents = [Resident];

    [Test]
    public async Task VacatedFolders_Should_Hold_No_Source_File_Except_Their_Listed_Residents()
    {
        var files = await new SdkSourceTree().LoadAsync();

        await Assert.That(files).IsNotEmpty();
        await Assert.That(Intruders(files)).IsEmpty();
    }

    [Test]
    [Arguments("Models/Intruder.cs")]
    [Arguments("Internal/ResponseAdapters/IntruderResponseAdapter.cs")]
    [Arguments("Internal/StreamAdapters/IntruderStreamAdapter.cs")]
    public async Task Intruders_Should_Name_A_File_Added_To_A_Vacated_Folder(string relativePath)
    {
        var intruders = Intruders([new SdkSourceFile(relativePath, []), new SdkSourceFile(Resident, [])]);

        await Assert.That(intruders).HasSingleItem();
        await Assert.That(intruders[0]).StartsWith(relativePath);
    }

    [Test]
    public async Task Residents_Should_Each_Still_Sit_In_A_Vacated_Folder()
    {
        var paths = (await new SdkSourceTree().LoadAsync()).Select(static file => file.RelativePath).ToHashSet(StringComparer.Ordinal);

        var stale = Residents.Where(path => !paths.Contains(path)).ToArray();

        await Assert.That(stale).IsEmpty();
    }

    private static string[] Intruders(IEnumerable<SdkSourceFile> files) =>
    [
        .. files
            .Where(static file => !Residents.Contains(file.RelativePath, StringComparer.Ordinal))
            .SelectMany(static file => VacatedFolders
                .Where(folder => string.Equals(string.Join('/', file.Folders), folder.Path, StringComparison.Ordinal))
                .Select(folder => $"{file.RelativePath} belongs in {folder.NewHome}")),
    ];

    private sealed record VacatedFolder(string Path, string NewHome);
}
