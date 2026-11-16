using System.Security.Cryptography;
using NSubstitute;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Hosting.BackgroundService.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Hosting.BackgroundService;

[ClassDataSource<PinnedManagedServiceFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel(ParallelConstraintKeys.ServerProcess)]
public sealed class OpenCodeServiceConfigurationLiveTests(PinnedManagedServiceFixture service)
{
    [Test]
    public async Task GetOrCreatePasswordAsync_Should_Return_The_Managed_Service_Password_Without_Writing()
    {
        var scenario = new ServiceConfigLiveScenario(service);
        var environment = scenario.Environment;
        using var random = RandomNumberGenerator.Create();
        var actual = scenario.Files;
        var files = Substitute.For<IServiceFileSystem>();
        files.TryReadAllBytesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
            actual.TryReadAllBytesAsync(call.ArgAt<string>(0), call.ArgAt<CancellationToken>(1)));
        var options = new OpenCodeServiceConfigOptions { Channel = PinnedManagedServiceFixture.Channel };
        var paths = new ServicePathResolver(environment).Resolve(ServiceSelection.Snapshot(options));
        var before = await actual.TryReadAllBytesAsync(paths.ConfigFile!, CancellationToken.None);
        await Assert.That(before).IsNotNull();
        var registration = await ServiceRegistrationReader.TryReadAsync(actual, service.RegistrationFile, CancellationToken.None);
        await Assert.That(registration).IsNotNull();
        var stops = 0;
        var editor = new ServiceConfigEditor(new ServiceConfigStore(environment, files, TimeProvider.System, new RandomCredential(random)),
            (options, token) => { stops++; return Task.CompletedTask; });

        var password = await editor.GetOrCreatePasswordAsync(options, CancellationToken.None);

        await Assert.That(password).IsEqualTo(registration.Password);
        await Assert.That(stops).IsEqualTo(0);
        await Assert.That(files.ReceivedCalls().Select(static call => call.GetMethodInfo().Name))
            .IsEquivalentTo(["TryReadAllBytesAsync"], CollectionOrdering.Matching);
        var read = files.ReceivedCalls().Single();
        await Assert.That(read.GetArguments()[0]).IsEqualTo(paths.ConfigFile);
        await Assert.That(await actual.TryReadAllBytesAsync(paths.ConfigFile!, CancellationToken.None))
            .IsEquivalentTo(before, CollectionOrdering.Matching);
    }
}
