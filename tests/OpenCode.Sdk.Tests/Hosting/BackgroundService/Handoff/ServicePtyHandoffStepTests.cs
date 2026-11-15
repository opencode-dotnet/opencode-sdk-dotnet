using NSubstitute;
using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk.Tests.Hosting.BackgroundService.Handoff;

/// <summary>The shared handoff step's best-effort boundary, independently of the later terminator.</summary>
public sealed class ServicePtyHandoffStepTests
{
    private static readonly ServiceRegistration Registered = ServicePtyHandoffScenario.Registration(new Uri("http://127.0.0.1:49374"));
    private readonly ServicePtyHandoffScenario _scenario = new();
    private readonly IServicePtyHandoff _handoff = Substitute.For<IServicePtyHandoff>();

    [Test]
    public async Task RunAsync_Should_Propagate_Caller_Cancellation()
    {
        var caller = new CancellationToken(canceled: true);
        _handoff.PrepareAsync(_scenario.RegistrationPath(), Registered, ServicePtyHandoffScenario.RequestTimeout, caller)
            .Returns(Task.FromException(new OperationCanceledException(caller)));

        var exception = await Assert.That(async () => await new ServicePtyHandoffStep(_handoff, ServicePtyHandoffScenario.RequestTimeout)
            .RunAsync(_scenario.RegistrationPath(), Registered, handOff: true, caller)).Throws<OperationCanceledException>();

        await Assert.That(exception!.CancellationToken).IsEqualTo(caller);
    }

    [Test]
    [Arguments("io", true)]
    [Arguments("server", true)]
    [Arguments("timeout", true)]
    [Arguments("io", false)]
    [Arguments("server", false)]
    [Arguments("timeout", false)]
    public async Task RunAsync_Should_Swallow_Every_Other_Failure(string failureKind, bool handOff)
    {
        Exception failure = failureKind switch
        {
            "io" => new IOException("The handoff or clear failed."),
            "timeout" => new OperationCanceledException("The handoff request timed out."),
            _ => new OpenCodeServerException("The handoff or clear failed."),
        };
        _handoff.PrepareAsync(_scenario.RegistrationPath(), Registered, ServicePtyHandoffScenario.RequestTimeout, CancellationToken.None)
            .Returns(Task.FromException(failure));
        _handoff.ClearAsync(_scenario.RegistrationPath(), CancellationToken.None).Returns(Task.FromException(failure));

        await Assert.That(async () => await new ServicePtyHandoffStep(_handoff, ServicePtyHandoffScenario.RequestTimeout)
            .RunAsync(_scenario.RegistrationPath(), Registered, handOff, CancellationToken.None)).ThrowsNothing();

        _ = _handoff.Received(handOff ? 1 : 0)
            .PrepareAsync(_scenario.RegistrationPath(), Registered, ServicePtyHandoffScenario.RequestTimeout, CancellationToken.None);
        _ = _handoff.Received(handOff ? 0 : 1).ClearAsync(_scenario.RegistrationPath(), CancellationToken.None);
    }
}
