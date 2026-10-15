using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests;

/// <summary>
/// Folders the vertical-slice layout emptied stay empty: a file added to one, for example by a
/// branch that started before the move, fails here with the folder its kind now lives in. The root
/// <c>Internal/</c> holds area folders only, so no source file sits directly in it.
/// </summary>
public sealed class LayoutGuardTests
{
    private static readonly VacatedFolder[] VacatedFolders =
    [
        new("Models", "the owning family's Models/ folder, or Common/Errors/ for the shared error spine"),
        new("Pagination", "Common/Pagination/"),
        new("Internal/Abstractions", "the seam's area folder under Internal/, such as Internal/Terminal/"),
        new("Internal/BackgroundService", "Hosting/BackgroundService/Internal/"),
        new("Internal/Launcher", "Hosting/Launcher/Internal/"),
        new("Internal/Pagination", "Common/Internal/"),
        new("Internal/Posix", "Hosting/Internal/Platform/Posix/"),
        new("Internal/ResponseAdapters", "the owning family's Internal/ folder"),
        new("Internal/StreamAdapters", "the owning family's Internal/ folder"),
        new("Internal/Windows", "Hosting/Internal/Platform/Windows/"),
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
    [Arguments("Internal/BackgroundService/Stop/IntruderStopper.cs")]
    [Arguments("Internal/Launcher/IntruderLadder.cs")]
    [Arguments("Internal/Posix/IntruderSpawn.cs")]
    [Arguments("Internal/Windows/Abstractions/IIntruderJob.cs")]
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

    [Test]
    public async Task RootInternal_Should_Hold_No_Source_File_Directly()
    {
        var files = await new SdkSourceTree().LoadAsync();

        await Assert.That(files).IsNotEmpty();
        await Assert.That(LooseInRootInternal(files)).IsEmpty();
    }

    [Test]
    public async Task LooseInRootInternal_Should_Name_A_File_Placed_Directly_In_Internal()
    {
        const string relativePath = "Internal/IntruderPolicy.cs";

        var intruders = LooseInRootInternal([new SdkSourceFile(relativePath, [])]);

        await Assert.That(intruders).HasSingleItem();
        await Assert.That(intruders[0]).IsEqualTo($"{relativePath} belongs in {RootInternalHome}");
    }

    /// <summary>Area folders under the root <c>Internal/</c> and every slice's own <c>Internal/</c> are where machinery belongs.</summary>
    [Test]
    [Arguments("Internal/Transport/Pipeline.cs")]
    [Arguments("Ptys/Internal/PtyConnectUriBuilder.cs")]
    [Arguments("Hosting/Internal/ExecutableResolver.cs")]
    public async Task LooseInRootInternal_Should_Ignore_A_File_In_An_Internal_Folder_Below_The_Root(string relativePath)
    {
        var intruders = LooseInRootInternal([new SdkSourceFile(relativePath, [])]);

        await Assert.That(intruders).IsEmpty();
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

    private static string[] LooseInRootInternal(IEnumerable<SdkSourceFile> files) =>
    [
        .. files
            .Where(static file => string.Equals(string.Join('/', file.Folders), "Internal", StringComparison.Ordinal))
            .Select(static file => $"{file.RelativePath} belongs in {RootInternalHome}"),
    ];

    /// <summary>A folder holds its own files and every file below it, never a sibling that merely shares its name prefix.</summary>
    private static bool IsWithin(string fileFolder, string folder) =>
        string.Equals(fileFolder, folder, StringComparison.Ordinal) || fileFolder.StartsWith($"{folder}/", StringComparison.Ordinal);

    private const string RootInternalHome =
        "Internal/<Area>/ when several slices share it, or that slice's Internal/ when one slice uses it";

    private sealed record VacatedFolder(string Path, string NewHome);
}
