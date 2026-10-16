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
        var pending = PrepareAsync(server.Endpoint, shutdown, caller.Token);
        try
        {
            Task[] arrivalTasks = [arrived.Task];
            await Task.WhenAll(arrivalTasks).WaitAsync(HangGuard.Bound);
            await AssertCanceledPreparationAsync(pending, caller, server);
            _ = shutdown.DidNotReceiveWithAnyArgs().ShutdownAsync(default!, default);
        }
        finally
        {
            await caller.CancelOnWorkerAsync();
            server.ReleaseResponses();
            Task[] cleanupTasks = [pending];
            _ = await Assert.That(async () => await Task.WhenAll(cleanupTasks).WaitAsync(HangGuard.Bound)).Throws<OperationCanceledException>();
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
        var pending = PrepareAsync(server.Endpoint, shutdown, caller.Token);
        try
        {
            Task[] entryTasks = [entered.Task];
            await Task.WhenAll(entryTasks).WaitAsync(HangGuard.Bound);
            var boundToken = await entered.Task;
            await Assert.That(boundToken).IsNotEqualTo(caller.Token);
            await AssertCanceledPreparationAsync(pending, caller, server);
            _ = shutdown.Received(1).ShutdownAsync(Arg.Any<ServiceRegistration>(), boundToken);
        }
        finally
        {
            await caller.CancelOnWorkerAsync();
            Task[] cleanupTasks = [pending];
            _ = await Assert.That(async () => await Task.WhenAll(cleanupTasks).WaitAsync(HangGuard.Bound)).Throws<OperationCanceledException>();
        }
    }

    private Task PrepareAsync(Uri endpoint, IServicePtyShutdown shutdown, CancellationToken cancellationToken) =>
        new ServicePtyHandoff(new TestablyServiceFileSystem(_scenario.FileSystem), _scenario.Clock, shutdown)
            .PrepareAsync(_scenario.RegistrationPath(), ServicePtyHandoffScenario.Registration(endpoint), RequestTimeout, cancellationToken);

    private async Task AssertCanceledPreparationAsync(Task pending, CancellationTokenSource caller, LoopbackHttpServer server)
    {
        await caller.CancelOnWorkerAsync();

        Task[] pendingTasks = [pending];
        var exception = await Assert.That(async () => await Task.WhenAll(pendingTasks).WaitAsync(HangGuard.Bound, CancellationToken.None)).Throws<OperationCanceledException>();

        await Assert.That(exception!.CancellationToken).IsEqualTo(caller.Token);
        await Assert.That(_scenario.ReadSidecar()).IsNull();
        await Assert.That(_scenario.TemporaryFiles()).IsEmpty();
        await Assert.That(server.RequestPaths).IsEquivalentTo([HandoffPath], CollectionOrdering.Matching);
    }
}
