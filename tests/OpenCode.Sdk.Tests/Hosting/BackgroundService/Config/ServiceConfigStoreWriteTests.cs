using NSubstitute;
using NSubstitute.ExceptionExtensions;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Hosting.BackgroundService.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Hosting.BackgroundService.Config;

public sealed class ServiceConfigStoreWriteTests
{
    [Test]
    public async Task WriteAsync_Should_Retain_A_Partially_Written_Temporary_File()
    {
        var scenario = new ServiceConfigScenario();
        var temporary = scenario.Paths.ConfigFile + ".12345.tmp";
        var cause = new IOException("partial write");
        scenario.FileSystem.WriteOwnerOnlyAsync(temporary, Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await scenario.Adapter.WriteOwnerOnlyAsync(temporary, "partial"u8.ToArray(), call.ArgAt<CancellationToken>(2));
                throw cause;
            });
        scenario.ClearCalls();
        var error = await Assert.That(async () =>
            await scenario.Store.WriteAsync(scenario.Paths, new ServiceConfigDocument(), CancellationToken.None)).Throws<OpenCodeServerException>();
        await Assert.That(error).IsNotNull();
        await Assert.That(error.InnerException).IsSameReferenceAs(cause);
        await Assert.That(await scenario.ReadTextAsync(temporary)).IsEqualTo("partial");
        await Assert.That(scenario.Calls).IsEquivalentTo([
            "CreateDirectory:" + scenario.Files.Path.GetDirectoryName(scenario.Paths.ConfigFile),
            "WriteOwnerOnlyAsync:" + temporary], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task WriteAsync_Should_Create_Overwrite_And_Publish_The_Pid_Temporary_File(bool existing)
    {
        var scenario = new ServiceConfigScenario();
        var temporary = scenario.Paths.ConfigFile + ".12345.tmp";
        if (existing)
        {
            await scenario.SeedAsync(temporary, "longer existing temporary content");
        }
        scenario.ClearCalls();

        await scenario.Store.WriteAsync(scenario.Paths, new ServiceConfigDocument(port: 4096), CancellationToken.None);

        await Assert.That(scenario.Calls).IsEquivalentTo([
            "CreateDirectory:" + scenario.Files.Path.GetDirectoryName(scenario.Paths.ConfigFile),
            "WriteOwnerOnlyAsync:" + temporary, "Rename:" + temporary], CollectionOrdering.Matching);
        await Assert.That(await scenario.ReadTextAsync(scenario.Paths.ConfigFile!))
            .IsEqualTo("{\n  \"port\": 4096\n}\n");
        await Assert.That(scenario.Inventory()).DoesNotContain("file:" + temporary);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task WriteAsync_Should_Wrap_The_Failing_Stage_And_Stop(bool write)
    {
        var scenario = new ServiceConfigScenario();
        var temporary = scenario.Paths.ConfigFile + ".12345.tmp";
        Exception cause = write ? new IOException("write failed") : new UnauthorizedAccessException("directory failed");
        if (write)
        {
            scenario.FileSystem.WriteOwnerOnlyAsync(temporary, Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>()).ThrowsAsync(cause);
        }
        else
        {
            scenario.FileSystem.When(files => files.CreateDirectory(Arg.Any<string>())).Do(_ => throw cause);
        }
        scenario.ClearCalls();

        var failure = await Assert.That(async () =>
            await scenario.Store.WriteAsync(scenario.Paths, new ServiceConfigDocument(), CancellationToken.None)).Throws<OpenCodeServerException>();

        await Assert.That(failure).IsNotNull();
        await Assert.That(failure.InnerException).IsSameReferenceAs(cause);
        await Assert.That(failure.Message).Contains(scenario.Paths.ConfigFile!);
        if (write)
        {
            await Assert.That(failure.Message).Contains(temporary);
        }
        string[] expected = write
            ? ["CreateDirectory:" + scenario.Files.Path.GetDirectoryName(scenario.Paths.ConfigFile), "WriteOwnerOnlyAsync:" + temporary]
            : ["CreateDirectory:" + scenario.Files.Path.GetDirectoryName(scenario.Paths.ConfigFile)];
        await Assert.That(scenario.Calls).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }
}
