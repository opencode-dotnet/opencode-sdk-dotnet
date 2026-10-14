using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests;

/// <summary>
/// Folders the vertical-slice layout emptied stay empty: a file added to one, for example by a
/// branch that started before the move, fails here with the folder its kind now lives in.
/// </summary>
public sealed class LayoutGuardTests
{
    private static readonly VacatedFolder[] VacatedFolders =
    [
        new("Models", "the owning family's Models/ folder, or Common/Errors/ for the shared error spine"),
        new("Pagination", "Common/Pagination/"),
        new("Internal/Abstractions", "the seam's area folder under Internal/, such as Internal/Terminal/"),
        new("Internal/Pagination", "Common/Internal/"),
        new("Internal/ResponseAdapters", "the owning family's Internal/ folder"),
        new("Internal/StreamAdapters", "the owning family's Internal/ folder"),
    ];

    [Test]
    public async Task VacatedFolders_Should_Hold_No_Source_File()
    {
        var files = await new SdkSourceTree().LoadAsync();

        await Assert.That(files).IsNotEmpty();
        await Assert.That(Intruders(files)).IsEmpty();
    }

    [Test]
    [Arguments("Models/Intruder.cs")]
    [Arguments("Pagination/IntruderCursor.cs")]
    [Arguments("Internal/Abstractions/IIntruderSeam.cs")]
    [Arguments("Internal/Pagination/IntruderPaginator.cs")]
    [Arguments("Internal/ResponseAdapters/IntruderResponseAdapter.cs")]
    [Arguments("Internal/StreamAdapters/IntruderStreamAdapter.cs")]
    [Arguments("Models/Sub/Intruder.cs")]
    [Arguments("Internal/ResponseAdapters/Sub/IntruderResponseAdapter.cs")]
    public async Task Intruders_Should_Name_A_File_Added_To_A_Vacated_Folder(string relativePath)
    {
        var intruders = Intruders([new SdkSourceFile(relativePath, [])]);

        await Assert.That(intruders).HasSingleItem();
        await Assert.That(intruders[0]).StartsWith(relativePath);
    }

    /// <summary>A folder that only shares a vacated folder's name prefix is not inside it.</summary>
    [Test]
    public async Task Intruders_Should_Ignore_A_Sibling_Folder_That_Shares_The_Name_Prefix()
    {
        var intruders = Intruders([new SdkSourceFile("ModelsCatalog/Catalog.cs", [])]);

        await Assert.That(intruders).IsEmpty();
    }

    private static string[] Intruders(IEnumerable<SdkSourceFile> files) =>
    [
        .. files.SelectMany(static file => VacatedFolders
            .Where(folder => IsWithin(string.Join('/', file.Folders), folder.Path))
            .Select(folder => $"{file.RelativePath} belongs in {folder.NewHome}")),
    ];

    /// <summary>A folder holds its own files and every file below it, never a sibling that merely shares its name prefix.</summary>
    private static bool IsWithin(string fileFolder, string folder) =>
        string.Equals(fileFolder, folder, StringComparison.Ordinal) || fileFolder.StartsWith($"{folder}/", StringComparison.Ordinal);

    private sealed record VacatedFolder(string Path, string NewHome);
}
