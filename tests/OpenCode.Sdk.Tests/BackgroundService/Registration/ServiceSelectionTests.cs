using OpenCode.Sdk.Internal;
using Testably.Abstractions.Testing;

namespace OpenCode.Sdk.Tests.BackgroundService.Registration;

public sealed class ServiceSelectionTests
{
    private static readonly string DirectFile = new MockFileSystem().Path.Combine(
        new MockFileSystem().Path.GetTempPath(), "opencode", "service.json");

    [Test]
    public async Task Snapshot_Should_Default_To_The_Shared_Registration()
    {
        var selection = ServiceSelection.Snapshot((OpenCodeServerDiscoverOptions?)null);

        await Assert.That(selection.Channel).IsNull();
        await Assert.That(selection.DirectRegistrationFile).IsNull();
        await Assert.That(selection.InstalledVersion).IsNull();
        await Assert.That(selection.ExpectedVersion).IsNull();
    }

    [Test]
    public async Task Snapshot_Should_Keep_A_Named_Channel()
    {
        var selection = Select("dev", null, null, null);

        await Assert.That(selection.Channel).IsEqualTo("dev");
        await Assert.That(selection.DirectRegistrationFile).IsNull();
    }

    [Test]
    public async Task Snapshot_Should_Keep_An_Absolute_Direct_File_Without_A_Channel()
    {
        var selection = Select(null, DirectFile, null, "2.0.3");

        await Assert.That(selection.Channel).IsNull();
        await Assert.That(selection.DirectRegistrationFile).IsEqualTo(DirectFile);
        await Assert.That(selection.ExpectedVersion).IsEqualTo("2.0.3");
    }

    [Test]
    public async Task Snapshot_Should_Refuse_Channel_With_A_Direct_File()
    {
        var exception = await Assert
            .That(() => Select("dev", DirectFile, null, null))
            .Throws<ArgumentException>();

        await Assert.That(exception!.ParamName).IsEqualTo("options");
    }

    [Test]
    public async Task Snapshot_Should_Refuse_A_Relative_Direct_File()
    {
        var exception = await Assert
            .That(() => Select(null, "state/opencode/service.json", null, null))
            .Throws<ArgumentException>();

        await Assert.That(exception!.ParamName).IsEqualTo("options");
    }

    [Test]
    [Arguments("C:service.json")]
    [Arguments("C:")]
    [Arguments("\\opencode\\service.json")]
    public async Task Snapshot_Should_Refuse_A_Direct_File_That_Is_Rooted_But_Not_Fully_Qualified(string registrationFilePath)
    {
        // Drive-relative and current-drive-relative spellings pass Path.IsPathRooted on Windows and
        // resolve against process state; on Unix the same strings are plainly relative. Every case
        // is refused on every platform, which keeps the test one. (A bare "/" is not here: it is
        // fully qualified on Unix, and the guard checks qualification, not that a file is named.)
        var exception = await Assert
            .That(() => Select(null, registrationFilePath, null, null))
            .Throws<ArgumentException>();

        await Assert.That(exception!.ParamName).IsEqualTo("options");
    }

    [Test]
    [Arguments(" ", null, null, null)]
    [Arguments(null, " ", null, null)]
    [Arguments(null, null, " ", null)]
    [Arguments(null, null, null, "")]
    public async Task Snapshot_Should_Refuse_Blank_Channel_Path_And_Version_Values(
        string? channel, string? registrationFilePath, string? installedVersion, string? expectedVersion)
    {
        var exception = await Assert
            .That(() => Select(channel, registrationFilePath, installedVersion, expectedVersion))
            .Throws<ArgumentException>();

        await Assert.That(exception!.ParamName).IsEqualTo("options");
    }

    [Test]
    public async Task Snapshot_Should_Default_InstalledVersion_From_ExpectedVersion_In_Channel_Mode()
    {
        var selection = Select(null, null, null, "2.0.3");

        await Assert.That(selection.InstalledVersion).IsEqualTo("2.0.3");
        await Assert.That(selection.ExpectedVersion).IsEqualTo("2.0.3");
    }

    [Test]
    public async Task Snapshot_Should_Keep_InstalledVersion_Null_For_A_Direct_File_With_ExpectedVersion()
    {
        var selection = Select(null, DirectFile, null, "2.0.3");

        await Assert.That(selection.InstalledVersion).IsNull();
    }

    [Test]
    public async Task Snapshot_Should_Keep_Equal_Installed_And_Expected_Versions()
    {
        var selection = Select("dev", null, "0.0.0-dev-19646", "0.0.0-dev-19646");

        await Assert.That(selection.InstalledVersion).IsEqualTo("0.0.0-dev-19646");
    }

    [Test]
    public async Task Snapshot_Should_Refuse_Different_Installed_And_Expected_Versions()
    {
        var exception = await Assert
            .That(() => Select(null, null, "2.0.2", "2.0.3"))
            .Throws<ArgumentException>();

        await Assert.That(exception!.ParamName).IsEqualTo("options");
    }

    [Test]
    public async Task Snapshot_Should_Refuse_InstalledVersion_With_A_Direct_File()
    {
        var exception = await Assert
            .That(() => Select(null, DirectFile, "2.0.3", null))
            .Throws<ArgumentException>();

        await Assert.That(exception!.ParamName).IsEqualTo("options");
    }

    [Test]
    public async Task Snapshot_Should_Default_Stop_Options_To_The_Shared_Registration()
    {
        var selection = ServiceSelection.Snapshot((OpenCodeServerStopOptions?)null);

        await Assert.That(selection.Channel).IsNull();
        await Assert.That(selection.DirectRegistrationFile).IsNull();
        await Assert.That(selection.InstalledVersion).IsNull();
        await Assert.That(selection.ExpectedVersion).IsNull();
    }

    [Test]
    public async Task Snapshot_Should_Keep_Stop_Options_Without_An_Expected_Version()
    {
        var selection = ServiceSelection.Snapshot(new OpenCodeServerStopOptions { Channel = "dev", InstalledVersion = "0.0.0-dev-19646" });

        await Assert.That(selection.Channel).IsEqualTo("dev");
        await Assert.That(selection.InstalledVersion).IsEqualTo("0.0.0-dev-19646");
        await Assert.That(selection.ExpectedVersion).IsNull();
    }

    [Test]
    [Arguments(" ", null, null)]
    [Arguments(null, " ", null)]
    [Arguments(null, null, "")]
    public async Task Snapshot_Should_Refuse_Blank_Stop_Values_Naming_The_Stop_Options(
        string? channel, string? registrationFilePath, string? installedVersion)
    {
        var exception = await Assert
            .That(() => SelectStop(channel, registrationFilePath, installedVersion))
            .Throws<ArgumentException>();

        await Assert.That(exception!.ParamName).IsEqualTo("options");
        await Assert.That(exception.Message).StartsWith("OpenCodeServerStopOptions.");
    }

    [Test]
    public async Task Snapshot_Should_Refuse_Stop_Options_That_Combine_A_Channel_With_A_Direct_File()
    {
        var exception = await Assert
            .That(() => SelectStop("dev", DirectFile, null))
            .Throws<ArgumentException>();

        await Assert.That(exception!.ParamName).IsEqualTo("options");
    }

    [Test]
    public async Task Snapshot_Should_Refuse_Stop_Options_That_Migrate_A_Direct_File()
    {
        var exception = await Assert
            .That(() => SelectStop(null, DirectFile, "2.0.3"))
            .Throws<ArgumentException>();

        await Assert.That(exception!.ParamName).IsEqualTo("options");
    }

    private static ServiceSelection SelectStop(string? channel, string? registrationFilePath, string? installedVersion) =>
        ServiceSelection.Snapshot(new OpenCodeServerStopOptions
        {
            Channel = channel,
            RegistrationFilePath = registrationFilePath,
            InstalledVersion = installedVersion,
        });

    private static ServiceSelection Select(string? channel, string? registrationFilePath, string? installedVersion, string? expectedVersion) =>
        ServiceSelection.Snapshot(new OpenCodeServerDiscoverOptions
        {
            Channel = channel,
            RegistrationFilePath = registrationFilePath,
            InstalledVersion = installedVersion,
            ExpectedVersion = expectedVersion,
        });
}
