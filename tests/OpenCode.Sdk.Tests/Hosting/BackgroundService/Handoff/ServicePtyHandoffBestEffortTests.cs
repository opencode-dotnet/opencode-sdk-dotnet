using System.Net;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Support;
using TUnit.Assertions.Enums;
using static OpenCode.Sdk.Tests.Hosting.BackgroundService.Handoff.ServicePtyHandoffScenario;

namespace OpenCode.Sdk.Tests.Hosting.BackgroundService.Handoff;

/// <summary>Failed inner preparation steps are best-effort; only the caller's cancellation propagates.</summary>
public sealed class ServicePtyHandoffBestEffortTests
{
    private readonly ServicePtyHandoffScenario _scenario = new();

    [Test]
    public async Task PrepareAsync_Should_Expire_The_Ticket_On_The_Injected_Clock()
    {
        _scenario.EnsureSidecarDirectory();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = LoopbackHttpServer.Start(_ => Pending(entered));
        var shutdown = Substitute.For<IServicePtyShutdown>();
        shutdown.ShutdownAsync(Arg.Any<ServiceRegistration>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        using var caller = new CancellationTokenSource();
        var registration = ServicePtyHandoffScenario.Registration(server.Endpoint);
        var pending = new ServicePtyHandoff(new TestablyServiceFileSystem(_scenario.FileSystem), _scenario.Clock, shutdown)
            .PrepareAsync(_scenario.RegistrationPath(), registration, RequestTimeout, caller.Token);
        try
        {
            await entered.Task.WaitAsync(PreparationHangGuard);
            await AdvanceToJustBeforeRequestTimeoutAsync();
            await Assert.That(pending.IsCompleted).IsFalse();
            _ = shutdown.DidNotReceiveWithAnyArgs().ShutdownAsync(default!, default);
            _scenario.Clock.Advance(TimeSpan.FromTicks(1));
            await pending.WaitAsync(PreparationHangGuard);

            _ = shutdown.Received(1).ShutdownAsync(registration, Arg.Any<CancellationToken>());
            await AssertNullSidecarAfterExpiryAsync();
        }
        finally
        {
            await caller.CancelOnWorkerAsync();
            server.ReleaseResponses();
        }
    }

    [Test]
    public async Task PrepareAsync_Should_Expire_The_Fallback_On_The_Injected_Clock()
    {
        _scenario.EnsureSidecarDirectory();
        await using var server = LoopbackHttpServer.Start(static _ => Json(HttpStatusCode.NotFound, "{}"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var shutdown = Substitute.For<IServicePtyShutdown>();
        CancellationToken received = default;
        shutdown.ShutdownAsync(Arg.Any<ServiceRegistration>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            received = call.Arg<CancellationToken>();
            var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellation = received.Register(() => held.TrySetCanceled(received));
            entered.TrySetResult();
            await held.Task;
        });
        using var caller = new CancellationTokenSource();
        var pending = new ServicePtyHandoff(new TestablyServiceFileSystem(_scenario.FileSystem), _scenario.Clock, shutdown)
            .PrepareAsync(_scenario.RegistrationPath(), ServicePtyHandoffScenario.Registration(server.Endpoint), RequestTimeout, caller.Token);
        try
        {
            await entered.Task.WaitAsync(PreparationHangGuard);
            await AdvanceToJustBeforeRequestTimeoutAsync();
            await Assert.That(received.IsCancellationRequested).IsFalse();
            await Assert.That(pending.IsCompleted).IsFalse();
            _scenario.Clock.Advance(TimeSpan.FromTicks(1));
            await pending.WaitAsync(PreparationHangGuard);

            await Assert.That(received.IsCancellationRequested).IsTrue();
            await AssertNullSidecarAfterExpiryAsync();
        }
        finally
        {
            await caller.CancelOnWorkerAsync();
        }
    }

    [Test]
    public async Task PrepareAsync_Should_Shut_The_Daemon_Down_And_Publish_A_Null_Sidecar_When_The_Daemon_Answers_An_Error()
    {
        _scenario.EnsureSidecarDirectory();
        await using var server = LoopbackHttpServer.Start(static path => path == ShutdownPath
            ? NoContent()
            : Json(HttpStatusCode.InternalServerError, "{}"));

        await _scenario.PrepareAsync(server.Endpoint);

        await Assert.That(_scenario.ReadSidecar()!.Handoff).IsNull();
        await Assert.That(server.RequestPaths).IsEquivalentTo([HandoffPath, ShutdownPath], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("options")]
    [Arguments(null)]
    [Arguments("io")]
    public async Task PrepareAsync_Should_Swallow_A_Failed_Shutdown_Fallback(string? parameterName)
    {
        _scenario.EnsureSidecarDirectory();
        await using var server = LoopbackHttpServer.Start(static _ => Json(HttpStatusCode.NotFound, "{}"));
        Exception failure = parameterName == "io"
            ? new IOException("The shutdown failed.")
            : new ArgumentException("The shutdown implementation rejected another parameter.", parameterName);
        var shutdown = Substitute.For<IServicePtyShutdown>();
        shutdown.ShutdownAsync(Arg.Any<ServiceRegistration>(), Arg.Any<CancellationToken>()).Returns(Task.FromException(failure));
        var handoff = new ServicePtyHandoff(new TestablyServiceFileSystem(_scenario.FileSystem), _scenario.Clock, shutdown);

        await Assert.That(async () => await handoff.PrepareAsync(
            _scenario.RegistrationPath(), ServicePtyHandoffScenario.Registration(server.Endpoint), RequestTimeout, CancellationToken.None)).ThrowsNothing();

        _ = await _scenario.AssertNullSidecarAsync();
    }

    [Test]
    public async Task PrepareAsync_Should_Take_The_Shutdown_Fallback_When_The_Ticket_Request_Fails_With_An_IO_Error()
    {
        _scenario.EnsureSidecarDirectory();
        var files = new TestablyServiceFileSystem(_scenario.FileSystem);
        var fileSystem = Substitute.For<IServiceFileSystem>();
        fileSystem.TryReadAllBytesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => files.TryReadAllBytesAsync(call.Arg<string>(), call.Arg<CancellationToken>()));
        fileSystem.TryCreateExclusiveAsync(Arg.Any<string>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
            .Returns(call => files.TryCreateExclusiveAsync(call.Arg<string>(), call.Arg<ReadOnlyMemory<byte>>(), call.Arg<CancellationToken>()));
        fileSystem.When(static system => system.Rename(Arg.Any<string>(), Arg.Any<string>()))
            .Do(call => files.Rename(call.ArgAt<string>(0), call.ArgAt<string>(1)));
        var time = Substitute.For<TimeProvider>();
        time.GetUtcNow().Returns(_scenario.Clock.GetUtcNow());
        // Fail the ticket request's bound once, before transport can wrap the IOException;
        // the shutdown fallback gets a working bound from the same clock seam.
        time.CreateTimer(Arg.Any<TimerCallback>(), Arg.Any<object?>(), Arg.Any<TimeSpan>(), Arg.Any<TimeSpan>())
            .Returns(_ => throw new IOException("The ticket request's timer could not be created."),
                call => _scenario.Clock.CreateTimer(call.Arg<TimerCallback>(), call.Arg<object?>(), call.ArgAt<TimeSpan>(2), call.ArgAt<TimeSpan>(3)));
        var shutdown = Substitute.For<IServicePtyShutdown>();
        shutdown.ShutdownAsync(Arg.Any<ServiceRegistration>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var registration = FixedRegistration();

        await Assert.That(async () => await new ServicePtyHandoff(fileSystem, time, shutdown).PrepareAsync(
            _scenario.RegistrationPath(), registration, RequestTimeout, CancellationToken.None)).ThrowsNothing();

        _ = fileSystem.Received(2).TryReadAllBytesAsync(_scenario.SidecarPath(), CancellationToken.None);
        _ = shutdown.Received(1).ShutdownAsync(registration, Arg.Any<CancellationToken>());
        _ = await _scenario.AssertNullSidecarAsync();
    }

    [Test]
    [Arguments("io")]
    [Arguments("argument")]
    public async Task PrepareAsync_Should_Complete_Without_A_Sidecar_When_The_Publish_Fails(string failureKind)
    {
        await using var server = LoopbackHttpServer.Start(static _ =>
            Json(HttpStatusCode.OK, Fixtures.LoadJson("BackgroundService.pty-handoff-response-ticket.json")));
        var fileSystem = Substitute.For<IServiceFileSystem>();
        fileSystem.TryReadAllBytesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((byte[]?)null);
        Exception failure = failureKind == "io"
            ? new IOException("The sidecar directory vanished.")
            : new ArgumentException("The sidecar write was refused.", nameof(failureKind));
        fileSystem.TryCreateExclusiveAsync(Arg.Any<string>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(failure);

        await Assert.That(async () => await new ServicePtyHandoff(fileSystem, _scenario.Clock, new ServicePtyShutdown()).PrepareAsync(
            _scenario.RegistrationPath(), ServicePtyHandoffScenario.Registration(server.Endpoint), RequestTimeout, CancellationToken.None)).ThrowsNothing();

        _ = fileSystem.Received(1).TryCreateExclusiveAsync(Arg.Any<string>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>());
        fileSystem.DidNotReceiveWithAnyArgs().Rename(default!, default!);
    }

    [Test]
    public async Task PrepareAsync_Should_Rethrow_Caller_Cancellation_During_The_Publish()
    {
        await using var server = LoopbackHttpServer.Start(static _ =>
            Json(HttpStatusCode.OK, Fixtures.LoadJson("BackgroundService.pty-handoff-response-ticket.json")));
        using var caller = new CancellationTokenSource();
        var fileSystem = Substitute.For<IServiceFileSystem>();
        fileSystem.TryReadAllBytesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((byte[]?)null);
        fileSystem.TryCreateExclusiveAsync(Arg.Any<string>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await caller.CancelOnWorkerAsync();
                return await Task.FromCanceled<bool>(call.Arg<CancellationToken>());
            });

        var exception = await Assert.That(async () => await new ServicePtyHandoff(fileSystem, _scenario.Clock, new ServicePtyShutdown()).PrepareAsync(
            _scenario.RegistrationPath(), ServicePtyHandoffScenario.Registration(server.Endpoint), RequestTimeout, caller.Token)).Throws<OperationCanceledException>();

        await Assert.That(exception!.CancellationToken).IsEqualTo(caller.Token);
        fileSystem.DidNotReceiveWithAnyArgs().Rename(default!, default!);
    }

    [Test]
    public async Task PrepareAsync_Should_Take_The_Fallback_When_The_Request_Bound_Expires()
    {
        // The ticket request and the shutdown both outlive their bound; neither expiry is the
        // caller's cancellation, so both are failures the best-effort path absorbs. The shutdown
        // seam records the call and holds it until its own bound cancels it, so the proof rests on
        // the two timers alone, not on how fast a loaded host delivers a request.
        _scenario.EnsureSidecarDirectory();
        await using var server = LoopbackHttpServer.Start(static _ => Hold(HttpStatusCode.OK, "{}"));
        var shutdown = new BoundOutlivingShutdown();
        var registration = ServicePtyHandoffScenario.Registration(server.Endpoint);

        await new ServicePtyHandoff(new TestablyServiceFileSystem(_scenario.FileSystem), TimeProvider.System, shutdown).PrepareAsync(
            _scenario.RegistrationPath(), registration, TimeSpan.FromMilliseconds(200), CancellationToken.None);

        await Assert.That(_scenario.ReadSidecar()!.Handoff).IsNull();
        await Assert.That(shutdown.Calls).IsEqualTo(1);
        await Assert.That(shutdown.Received).IsSameReferenceAs(registration);
        await Assert.That(shutdown.EndedByItsBound).IsTrue();
        server.ReleaseResponses();
    }

    [Test]
    public async Task PrepareAsync_Should_Publish_A_Null_Sidecar_When_The_Shutdown_Fails()
    {
        _scenario.EnsureSidecarDirectory();
        await using var server = LoopbackHttpServer.Start(static path => path == ShutdownPath
            ? Json(HttpStatusCode.InternalServerError, "{}")
            : Json(HttpStatusCode.NotFound, "{}"));

        await _scenario.PrepareAsync(server.Endpoint);

        await Assert.That(_scenario.ReadSidecar()!.Handoff).IsNull();
        await Assert.That(server.RequestPaths).Contains(ShutdownPath);
    }

    [Test]
    public async Task PrepareAsync_Should_Publish_A_Null_Sidecar_When_The_Shutdown_Route_Is_Absent_Too()
    {
        _scenario.EnsureSidecarDirectory();
        await using var server = LoopbackHttpServer.Start(static _ => Json(HttpStatusCode.NotFound, "{}"));

        await _scenario.PrepareAsync(server.Endpoint);

        await Assert.That(_scenario.ReadSidecar()!.Handoff).IsNull();
    }

    private async Task AdvanceToJustBeforeRequestTimeoutAsync()
    {
        await _scenario.Clock.WaitForArmedAsync(RequestTimeout, PreparationHangGuard);
        _scenario.Clock.Advance(RequestTimeout - TimeSpan.FromTicks(1));
    }

    private async Task AssertNullSidecarAfterExpiryAsync()
    {
        await Assert.That(_scenario.ReadSidecar()!.Handoff).IsNull();
        await Assert.That(_scenario.ReadSidecar()!.ExpiresAt)
            .IsEqualTo(_scenario.Clock.GetUtcNow().ToUnixTimeMilliseconds() + 30_000d);
    }

    /// <summary>A shutdown seam that records its call and answers only when its token is cancelled.</summary>
    private sealed class BoundOutlivingShutdown : IServicePtyShutdown
    {
        public int Calls { get; private set; }

        public ServiceRegistration? Received { get; private set; }

        public bool EndedByItsBound { get; private set; }

        public async Task ShutdownAsync(ServiceRegistration registration, CancellationToken cancellationToken)
        {
            Calls++;
            Received = registration;
            var held = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(() => held.TrySetCanceled(cancellationToken)))
            {
                try
                {
                    _ = await held.Task.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    EndedByItsBound = true;
                    throw;
                }
            }
        }
    }

    /// <summary>An answer the server sends only after <see cref="LoopbackHttpServer.ReleaseResponses"/>.</summary>
    private static LoopbackHttpResponse Hold(HttpStatusCode status, string body) =>
        new() { StatusCode = status, ContentType = "application/json", Body = body, KeepOpen = true };
}
