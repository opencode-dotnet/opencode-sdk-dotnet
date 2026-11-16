using System.Security.Cryptography;
using NSubstitute;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Harness;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Hosting.BackgroundService;

[ClassDataSource<PinnedManagedServiceFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel(ParallelConstraintKeys.ServerProcess)]
public sealed class OpenCodeServiceConfigurationLiveTests(PinnedManagedServiceFixture service)
{
    [Test]
    public async Task GetOrCreatePasswordAsync_Should_Return_The_Managed_Service_Password_Without_Writing()
    {
        var environment = Substitute.For<IServiceEnvironment>();
        environment.GetEnvironmentVariable(Arg.Any<string>()).Returns(call =>
            service.Environment.TryGetValue(call.Arg<string>(), out var value) ? value : null);
        environment.UserProfile.Returns(service.Environment.TryGetValue("HOME", out var home) ? home : service.Environment["USERPROFILE"]);
        using var random = RandomNumberGenerator.Create();
        var actual = new ServiceFileSystem();
        var files = Substitute.For<IServiceFileSystem>();
        files.TryReadAllBytesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
            actual.TryReadAllBytesAsync(call.ArgAt<string>(0), call.ArgAt<CancellationToken>(1)));
        var options = new OpenCodeServiceConfigOptions { Channel = PinnedManagedServiceFixture.Channel };
        var paths = new ServicePathResolver(environment).Resolve(ServiceSelection.Snapshot(options));
        var before = await actual.TryReadAllBytesAsync(paths.ConfigFile!, CancellationToken.None);
        await Assert.That(before).IsNotNull();
        var registration = await ServiceRegistrationReader.TryReadAsync(actual, service.RegistrationFile, CancellationToken.None);
        await Assert.That(registration).IsNotNull();
        var editor = new ServiceConfigEditor(new ServiceConfigStore(environment, files, TimeProvider.System, new RandomCredential(random)));

        var password = await editor.GetOrCreatePasswordAsync(options, CancellationToken.None);

        await Assert.That(password).IsEqualTo(registration.Password);
        await Assert.That(files.ReceivedCalls().Select(static call => call.GetMethodInfo().Name))
            .IsEquivalentTo(["TryReadAllBytesAsync"], CollectionOrdering.Matching);
        var read = files.ReceivedCalls().Single();
        await Assert.That(read.GetArguments()[0]).IsEqualTo(paths.ConfigFile);
        await Assert.That(await actual.TryReadAllBytesAsync(paths.ConfigFile!, CancellationToken.None))
            .IsEquivalentTo(before, CollectionOrdering.Matching);
    }
}
