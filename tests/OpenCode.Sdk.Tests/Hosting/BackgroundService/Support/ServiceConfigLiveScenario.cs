using NSubstitute;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Harness;

namespace OpenCode.Sdk.Tests.Hosting.BackgroundService.Support;

/// <summary>The real service-config file access under a managed fixture's isolated environment.</summary>
internal sealed class ServiceConfigLiveScenario
{
    public ServiceConfigLiveScenario(PinnedManagedServiceFixture service)
    {
        Environment.GetEnvironmentVariable(Arg.Any<string>()).Returns(call =>
            service.Environment.TryGetValue(call.Arg<string>(), out var value) ? value : null);
        Environment.UserProfile.Returns(service.Environment.TryGetValue("HOME", out var home) ? home : service.Environment["USERPROFILE"]);
    }

    public IServiceEnvironment Environment { get; } = Substitute.For<IServiceEnvironment>();
    public ServiceFileSystem Files { get; } = new();
}
