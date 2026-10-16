using System.Net;
using NSubstitute;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Support;
using TUnit.Assertions.Enums;
using static OpenCode.Sdk.Tests.Hosting.BackgroundService.Handoff.ServicePtyHandoffScenario;

namespace OpenCode.Sdk.Tests.Hosting.BackgroundService.Handoff;

public sealed class ServicePtyHandoffCancellationTests
{
    private readonly ServicePtyHandoffScenario _scenario = new();

    [Test]
    public async Task PrepareAsync_Should_Preserve_The_Caller_Token_During_The_Ticket_Request()
    {
        _scenario.EnsureSidecarDirectory();
        var arrived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = LoopbackHttpServer.Start(_ => Pending(arrived));
        using var caller = new CancellationTokenSource();
        var shutdown = Substitute.For<IServicePtyShutdown>();
        var handoff = new ServicePtyHandoff(new TestablyServiceFileSystem(_scenario.FileSystem), _scenario.Clock, shutdown);
        var pending = handoff.PrepareAsync(_scenario.RegistrationPath(), ServicePtyHandoffScenario.Registration(server.Endpoint), RequestTimeout, caller.Token);
        try
        {
            await arrived.Task.WaitAsync(RequestTimeout);
            await caller.CancelOnWorkerAsync();

            Task[] pendingTasks = [pending];
            var exception = await Assert.That(async () => await Task.WhenAll(pendingTasks).WaitAsync(RequestTimeout)).Throws<OperationCanceledException>();

            await Assert.That(exception!.CancellationToken).IsEqualTo(caller.Token);
            await Assert.That(_scenario.ReadSidecar()).IsNull();
            await Assert.That(_scenario.TemporaryFiles()).IsEmpty();
            await Assert.That(server.RequestPaths).IsEquivalentTo([HandoffPath], CollectionOrdering.Matching);
            _ = shutdown.DidNotReceiveWithAnyArgs().ShutdownAsync(default!, default);
        }
        finally
        {
            await caller.CancelOnWorkerAsync();
            server.ReleaseResponses();
            Task[] cleanupTasks = [pending];
            _ = await Assert.That(async () => await Task.WhenAll(cleanupTasks).WaitAsync(RequestTimeout)).Throws<OperationCanceledException>();
        }
    }

    [Test]
    public async Task PrepareAsync_Should_Preserve_The_Caller_Token_During_Shutdown_Fallback()
    {
        _scenario.EnsureSidecarDirectory();
        await using var server = LoopbackHttpServer.Start(static _ => Json(HttpStatusCode.NotFound, "{}"));
        using var caller = new CancellationTokenSource();
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var shutdown = Substitute.For<IServicePtyShutdown>();
        shutdown.ShutdownAsync(Arg.Any<ServiceRegistration>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var token = call.Arg<CancellationToken>();
            entered.SetResult(token);
            Task[] heldTasks = [held.Task];
            await Task.WhenAll(heldTasks).WaitAsync(token);
        });
        var handoff = new ServicePtyHandoff(new TestablyServiceFileSystem(_scenario.FileSystem), _scenario.Clock, shutdown);
        var pending = handoff.PrepareAsync(_scenario.RegistrationPath(), ServicePtyHandoffScenario.Registration(server.Endpoint), RequestTimeout, caller.Token);
        try
        {
            var boundToken = await entered.Task.WaitAsync(RequestTimeout);
            await Assert.That(boundToken).IsNotEqualTo(caller.Token);
            await caller.CancelOnWorkerAsync();

            Task[] pendingTasks = [pending];
            var exception = await Assert.That(async () => await Task.WhenAll(pendingTasks).WaitAsync(RequestTimeout)).Throws<OperationCanceledException>();

            await Assert.That(exception!.CancellationToken).IsEqualTo(caller.Token);
            await Assert.That(_scenario.ReadSidecar()).IsNull();
            await Assert.That(_scenario.TemporaryFiles()).IsEmpty();
            await Assert.That(server.RequestPaths).IsEquivalentTo([HandoffPath], CollectionOrdering.Matching);
            _ = shutdown.Received(1).ShutdownAsync(Arg.Any<ServiceRegistration>(), boundToken);
        }
        finally
        {
            await caller.CancelOnWorkerAsync();
            Task[] cleanupTasks = [pending];
            _ = await Assert.That(async () => await Task.WhenAll(cleanupTasks).WaitAsync(RequestTimeout)).Throws<OperationCanceledException>();
        }
    }
}
