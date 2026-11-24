using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Hosting.BackgroundService.Registration;
using OpenCode.Sdk.Tests.Support;
using TUnit.Assertions.Enums;
using static OpenCode.Sdk.Tests.Hosting.BackgroundService.Handoff.ServicePtyHandoffScenario;

namespace OpenCode.Sdk.Tests.Hosting.BackgroundService.Handoff;

/// <summary>
/// The pinned client's sidecar orchestration (<c>pty-handoff.ts</c>) over the Testably
/// filesystem double and a real loopback HTTP daemon double: publication through a temporary plus
/// rename, the skip-when-current fast path, the null-sidecar fallback for an unusable ticket,
/// the concurrent-prepare recheck, the environment adoption matrix,
/// completion by source, and the idempotent clear.
/// </summary>
public sealed class ServicePtyHandoffTests
{
    private const long TicketExpiry = 1700000060000;

    private static readonly JsonElement Ticket = LoadTicket();

    private readonly ServicePtyHandoffScenario _scenario = new();

    [Test]
    public async Task PrepareAsync_Should_Publish_A_Sidecar_From_The_Handoff_Ticket()
    {
        _scenario.EnsureSidecarDirectory();
        await using var server = LoopbackHttpServer.Start(static _ =>
            Json(HttpStatusCode.OK, Fixtures.LoadJson("BackgroundService.pty-handoff-response-ticket.json")));
        var registration = ServicePtyHandoffScenario.Registration(server.Endpoint);

        await _scenario.PrepareAsync(registration);

        await Assert.That(server.RequestPaths).IsEquivalentTo([HandoffPath], CollectionOrdering.Matching);
        var sidecar = _scenario.ReadSidecar();
        await Assert.That(sidecar).IsNotNull();
        await Assert.That(sidecar!.SourceId).IsEqualTo(SourceId);
        await Assert.That(sidecar.SourcePid).IsEqualTo(RegisteredPid);
        await Assert.That(sidecar.SourceUrl).IsEqualTo(registration.Url);
        await Assert.That(sidecar.ExpiresAt).IsEqualTo(TicketExpiry);
        await Assert.That(sidecar.Handoff!.Value.GetRawText()).IsEqualTo(Ticket.GetRawText());
        await Assert.That(_scenario.TemporaryFiles()).IsEmpty();
    }

    [Test]
    public async Task PrepareAsync_Should_Reuse_A_Fresh_Matching_Sidecar_Without_Contacting_The_Daemon()
    {
        await using var server = LoopbackHttpServer.Start(static _ =>
            Json(HttpStatusCode.InternalServerError, "{}"));
        var registration = ServicePtyHandoffScenario.Registration(server.Endpoint);
        Seed(_scenario.SidecarPath(), Sidecar(registration, handoff: null, NowMilliseconds + 60_000).ToUtf8Json());

        await _scenario.PrepareAsync(registration);

        await Assert.That(server.Requests).IsEmpty();
        await Assert.That(_scenario.ReadSidecar()).IsNotNull();
    }

    [Test]
    public async Task PrepareAsync_Should_Shut_The_Daemon_Down_And_Publish_A_Null_Sidecar_When_The_Route_Is_Absent()
    {
        _scenario.EnsureSidecarDirectory();
        await using var server = LoopbackHttpServer.Start(path => path == ShutdownPath
            ? NoContent()
            : Json(HttpStatusCode.NotFound, "{}"));
        var registration = ServicePtyHandoffScenario.Registration(server.Endpoint);

        await _scenario.PrepareAsync(registration);

        _ = await _scenario.AssertNullSidecarAsync();
        await Assert.That(server.RequestPaths).Contains(ShutdownPath);
    }

    [Test]
    [Arguments("BackgroundService.pty-handoff-response-expired.json")]
    [Arguments("BackgroundService.pty-handoff-response-invalid.json")]
    [Arguments("BackgroundService.pty-handoff-response-missing.json")]
    public async Task PrepareAsync_Should_Shut_The_Daemon_Down_And_Publish_A_Null_Sidecar_For_An_Answer_Without_A_Usable_Ticket(string fixture)
    {
        // An expired ticket, a ticket missing a member the pin knows, and an answer without a handoff
        // member are refused, and a refusal takes the same best-effort fallback a failed request does.
        _scenario.EnsureSidecarDirectory();
        await using var server = StartShutdownFallbackServer(fixture);

        await _scenario.PrepareAsync(server.Endpoint);

        await AssertShutdownFallbackAsync(server);
    }

    [Test]
    [Arguments("BackgroundService.pty-handoff-ticket-trailing-garbage.json")]
    [Arguments("BackgroundService.pty-handoff-ticket-second-value.json")]
    public async Task PrepareAsync_Should_Take_The_Shutdown_Fallback_For_A_Ticket_With_Trailing_Content(string fixture)
    {
        _scenario.EnsureSidecarDirectory();
        await using var server = StartShutdownFallbackServer(fixture);

        await _scenario.PrepareAsync(server.Endpoint);

        await AssertShutdownFallbackAsync(server);
        await Assert.That(_scenario.TemporaryFiles()).IsEmpty();
    }

    private async Task AssertShutdownFallbackAsync(LoopbackHttpServer server)
    {
        _ = await _scenario.AssertNullSidecarAsync();
        await Assert.That(server.RequestPaths).IsEquivalentTo([HandoffPath, ShutdownPath], CollectionOrdering.Matching);
    }

    [Test]
    public async Task PrepareAsync_Should_Admit_Trailing_Whitespace_And_Publish_The_Raw_Ticket()
    {
        _scenario.EnsureSidecarDirectory();
        var body = Fixtures.LoadJson("BackgroundService.pty-handoff-unknown-members.json") + " \t\r\n";
        using var expected = JsonDocument.Parse(body);
        await using var server = LoopbackHttpServer.Start(_ => Json(HttpStatusCode.OK, body));

        await _scenario.PrepareAsync(server.Endpoint);

        var sidecar = _scenario.ReadSidecar();
        await Assert.That(sidecar).IsNotNull();
        await Assert.That(sidecar!.Handoff!.Value.GetRawText()).IsEqualTo(expected.RootElement.GetProperty("handoff").GetRawText());
        await Assert.That(server.RequestPaths).IsEquivalentTo([HandoffPath], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("http://127.0.0.1:49374?query=1")]
    [Arguments("http://127.0.0.1:49374#fragment")]
    [Arguments("http://synthetic@127.0.0.1:49374")]
    public async Task PrepareAsync_Should_Fall_Back_When_The_Registered_Endpoint_Is_Refused(string address)
    {
        _scenario.EnsureSidecarDirectory();
        Seed(_scenario.RegistrationPath(), ServiceRegistrationDocument.Compose(SourceId, ServicePtyHandoffScenario.Version, new Uri(address), RegisteredPid, Password));
        var registration = await ServiceRegistrationReader.TryReadAsync(
            new TestablyServiceFileSystem(_scenario.FileSystem), _scenario.RegistrationPath(), CancellationToken.None);
        await Assert.That(registration).IsNotNull();
        var shutdown = Substitute.For<IServicePtyShutdown>();
        shutdown.ShutdownAsync(Arg.Any<ServiceRegistration>(), Arg.Any<CancellationToken>())
            .Returns(call => new ServicePtyShutdown().ShutdownAsync(call.Arg<ServiceRegistration>(), call.Arg<CancellationToken>()));

        await new ServicePtyHandoff(new TestablyServiceFileSystem(_scenario.FileSystem), _scenario.Clock, shutdown)
            .PrepareAsync(_scenario.RegistrationPath(), registration!, RequestTimeout, CancellationToken.None);

        _ = shutdown.Received(1).ShutdownAsync(registration!, Arg.Any<CancellationToken>());
        var sidecar = await _scenario.AssertNullSidecarAsync();
        await Assert.That(sidecar.SourceUrl).IsEqualTo(registration!.Url);
        await Assert.That(_scenario.TemporaryFiles()).IsEmpty();
    }

    /// <summary>
    /// The concurrent caller's sidecar lands once the ticket request has reached the daemon double and
    /// before its failed answer is released, so the recheck finds it: the preparation returns without
    /// a shutdown and leaves that sidecar in place.
    /// </summary>
    [Test]
    public async Task PrepareAsync_Should_Return_When_A_Concurrent_Caller_Published_A_Fresh_Matching_Sidecar()
    {
        var arrived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        // No request starts until both captured locals are assigned below.
        LoopbackHttpServer? respondingServer = null;
        ServiceRegistration? registration = null;
        await using var server = LoopbackHttpServer.Start(path =>
        {
            if (path == ShutdownPath)
            {
                return NoContent();
            }

            // Arrival is marked before the release closes this request: net472 does not retry, so PrepareAsync can return at once.
            var first = !arrived.Task.IsCompleted;
            var response = Pending(arrived);
            if (first)
            {
                Seed(_scenario.SidecarPath(), Sidecar(registration!, Ticket, NowMilliseconds + 60_000).ToUtf8Json());
                respondingServer!.ReleaseResponses();
            }

            return response;
        });
        respondingServer = server;
        registration = ServicePtyHandoffScenario.Registration(server.Endpoint);

        var pending = _scenario.Handoff().PrepareAsync(_scenario.RegistrationPath(), registration, RequestTimeout, CancellationToken.None);
        await AssertTicketRequestArrivedAsync(arrived, pending);

        await pending.WaitAsync(PreparationHangGuard);

        await Assert.That(server.RequestPaths).DoesNotContain(ShutdownPath);
        await Assert.That(_scenario.ReadSidecar()!.Handoff).IsNotNull();
#if NET
        // The platform handler retries a request whose connection closed before any response.
        await Assert.That(server.RequestPaths.Count(static path => path == HandoffPath)).IsGreaterThan(1);
#endif
    }

    [Test]
    public async Task EnvironmentAsync_Should_Adopt_A_Fresh_Matching_Sidecar()
    {
        Seed(_scenario.RegistrationPath(), ServiceRegistrationData.Passwordless);
        SeedFreshSidecar(Ticket);

        var overlay = await EnvironmentAsync(Caller());

        await Assert.That(overlay["OPENCODE_PTY_HANDOFF"]).IsEqualTo(Ticket.GetRawText());
        await Assert.That(overlay["KEEP"]).IsEqualTo("1");
    }

    [Test]
    public async Task EnvironmentAsync_Should_Adopt_When_The_Registration_Is_Absent()
    {
        SeedFreshSidecar(Ticket);

        var overlay = await EnvironmentAsync(caller: null);

        await Assert.That(overlay["OPENCODE_PTY_HANDOFF"]).IsEqualTo(Ticket.GetRawText());
    }

    [Test]
    public async Task EnvironmentAsync_Should_Remove_The_Variable_When_The_Sidecar_Is_Expired()
    {
        Seed(_scenario.RegistrationPath(), ServiceRegistrationData.Passwordless);
        Seed(_scenario.SidecarPath(), Sidecar(FixedRegistration(), Ticket, NowMilliseconds - 1).ToUtf8Json());

        var overlay = await EnvironmentAsync(Caller());

        await Assert.That(overlay["OPENCODE_PTY_HANDOFF"]).IsNull();
    }

    [Test]
    public async Task EnvironmentAsync_Should_Remove_The_Variable_When_The_Source_Does_Not_Match_The_Registration()
    {
        Seed(_scenario.RegistrationPath(), ServiceRegistrationData.DuplicatePidResolved);
        SeedFreshSidecar(Ticket);

        var overlay = await EnvironmentAsync(Caller());

        await Assert.That(overlay["OPENCODE_PTY_HANDOFF"]).IsNull();
    }

    /// <summary>
    /// A fractional source pid (<c>typeof pid === "number"</c> admits it) is never the registration's
    /// integer pid, so <c>same</c> fails even where truncation would land on the registered pid.
    /// </summary>
    [Test]
    public async Task EnvironmentAsync_Should_Remove_The_Variable_When_The_Source_Pid_Is_Fractional_And_A_Registration_Is_Current()
    {
        Seed(_scenario.RegistrationPath(), ServiceRegistrationData.Passwordless);
        Seed(_scenario.SidecarPath(), FractionalSourceSidecar());

        var overlay = await EnvironmentAsync(Caller());

        await Assert.That(overlay["OPENCODE_PTY_HANDOFF"]).IsNull();
        await Assert.That(overlay["KEEP"]).IsEqualTo("1");
    }

    /// <summary><c>current === undefined</c> adopts whatever source the sidecar names, a fractional pid included.</summary>
    [Test]
    public async Task EnvironmentAsync_Should_Adopt_A_Fractional_Source_Pid_When_No_Registration_Is_Readable()
    {
        Seed(_scenario.SidecarPath(), FractionalSourceSidecar());

        var overlay = await EnvironmentAsync(caller: null);

        await Assert.That(overlay["OPENCODE_PTY_HANDOFF"]).IsEqualTo(Ticket.GetRawText());
    }

    [Test]
    public async Task EnvironmentAsync_Should_Remove_The_Variable_When_The_Sidecar_Is_Absent()
    {
        var overlay = await EnvironmentAsync(Caller());

        await Assert.That(overlay["OPENCODE_PTY_HANDOFF"]).IsNull();
        await Assert.That(overlay["KEEP"]).IsEqualTo("1");
    }

    [Test]
    public async Task EnvironmentAsync_Should_Replace_A_Caller_Provided_Handoff_Value()
    {
        Seed(_scenario.RegistrationPath(), ServiceRegistrationData.Passwordless);
        SeedFreshSidecar(Ticket);
        var caller = new Dictionary<string, string>(StringComparer.Ordinal) { ["OPENCODE_PTY_HANDOFF"] = "stale" };

        var overlay = await EnvironmentAsync(caller);

        await Assert.That(overlay["OPENCODE_PTY_HANDOFF"]).IsEqualTo(Ticket.GetRawText());
    }

    [Test]
    public async Task CompleteAsync_Should_Clear_A_Foreign_Source_Sidecar()
    {
        SeedFreshSidecar(Ticket);
        var winner = new ServiceRegistration(SourceId, ServicePtyHandoffScenario.Version, FixedUrl, new Uri(FixedUrl), RegisteredPid + 1, Password);

        await _scenario.Handoff().CompleteAsync(_scenario.RegistrationPath(), winner, CancellationToken.None);

        await Assert.That(_scenario.ReadSidecar()).IsNull();
    }

    [Test]
    public async Task CompleteAsync_Should_Keep_A_Same_Source_Sidecar_Even_When_Stale()
    {
        Seed(_scenario.SidecarPath(), Sidecar(FixedRegistration(), Ticket, NowMilliseconds - 1).ToUtf8Json());

        await _scenario.Handoff().CompleteAsync(_scenario.RegistrationPath(), FixedRegistration(), CancellationToken.None);

        await Assert.That(_scenario.ReadSidecar()).IsNotNull();
    }

    [Test]
    public async Task CompleteAsync_Should_Do_Nothing_When_The_Sidecar_Is_Absent()
    {
        await _scenario.Handoff().CompleteAsync(_scenario.RegistrationPath(), FixedRegistration(), CancellationToken.None);

        await Assert.That(_scenario.ReadSidecar()).IsNull();
    }

    [Test]
    public async Task ClearAsync_Should_Remove_The_Sidecar_And_Be_Idempotent()
    {
        SeedFreshSidecar(handoff: null);

        await _scenario.Handoff().ClearAsync(_scenario.RegistrationPath(), CancellationToken.None);
        await Assert.That(_scenario.ReadSidecar()).IsNull();

        await _scenario.Handoff().ClearAsync(_scenario.RegistrationPath(), CancellationToken.None);
    }

    [Test]
    public async Task ClearAsync_Should_Report_A_Sidecar_That_Cannot_Be_Removed()
    {
        var fileSystem = Substitute.For<IServiceFileSystem>();
        fileSystem.TryDelete(_scenario.SidecarPath()).Throws(new UnauthorizedAccessException("The sidecar is locked."));

        var exception = await Assert
            .That(async () => await new ServicePtyHandoff(fileSystem, _scenario.Clock, new ServicePtyShutdown()).ClearAsync(_scenario.RegistrationPath(), CancellationToken.None))
            .Throws<OpenCodeServerException>();

        await Assert.That(exception!.InnerException).IsTypeOf<UnauthorizedAccessException>();
    }

    [Test]
    public async Task PrepareAsync_Should_Rethrow_Caller_Cancellation()
    {
        var cancelled = new CancellationToken(canceled: true);

        _ = await Assert
            .That(async () => await _scenario.Handoff().PrepareAsync(_scenario.RegistrationPath(), FixedRegistration(), RequestTimeout, cancelled))
            .Throws<OperationCanceledException>();
    }

    /// <summary>
    /// A cancellation that arrives while the ticket request is in flight is the caller's, never a
    /// preparation failure. The caller cancels once the request has reached the daemon double, which
    /// holds its answer, so the cancellation is the only thing that can end the request.
    /// </summary>
    [Test]
    public async Task PrepareAsync_Should_Rethrow_Caller_Cancellation_During_The_Ticket_Request()
    {
        var arrived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = LoopbackHttpServer.Start(path => path == ShutdownPath ? NoContent() : Pending(arrived));
        using var caller = new CancellationTokenSource();

        var pending = _scenario.Handoff().PrepareAsync(_scenario.RegistrationPath(), ServicePtyHandoffScenario.Registration(server.Endpoint), RequestTimeout, caller.Token);
        await AssertTicketRequestArrivedAsync(arrived, pending);
        await caller.CancelOnWorkerAsync();

        OperationCanceledException? caught = null;
        try
        {
            await pending;
        }
        catch (OperationCanceledException exception)
        {
            caught = exception;
        }

        // Cancellation, never the fallback a daemon error takes.
        await Assert.That(caught).IsNotNull();
        server.ReleaseResponses();
    }

    /// <summary>The pinned client's <c>environment</c> reads a JSON-null ticket as no ticket and removes the variable.</summary>
    [Test]
    public async Task EnvironmentAsync_Should_Remove_The_Variable_For_A_Fresh_Matching_Sidecar_Without_A_Ticket()
    {
        Seed(_scenario.RegistrationPath(), ServiceRegistrationData.Passwordless);
        SeedFreshSidecar(handoff: null);

        var overlay = await EnvironmentAsync(Caller());

        await Assert.That(overlay["OPENCODE_PTY_HANDOFF"]).IsNull();
        await Assert.That(overlay["KEEP"]).IsEqualTo("1");
    }

    [Test]
    public async Task PrepareAsync_Should_Publish_The_Ticket_Exactly_As_The_Route_Answered_It()
    {
        // pty-handoff.ts writes body.handoff as it came: a member this pin does not know yet must
        // still reach the replacement daemon, whose own schema may require it.
        const string ticket = "{\"directory\":\"/work\",\"instanceID\":\"pty_1\",\"ticket\":\"t-1\",\"expiresAt\":1700000060000,\"signature\":\"sig-1\"}";
        _scenario.EnsureSidecarDirectory();
        await using var server = LoopbackHttpServer.Start(static _ => Json(HttpStatusCode.OK, "{\"handoff\":" + ticket + "}"));

        await _scenario.PrepareAsync(server.Endpoint);

        // The exchange first, so a recorded shutdown identifies the null-sidecar fallback.
        await Assert.That(server.RequestPaths).IsEquivalentTo([HandoffPath], CollectionOrdering.Matching);
        await Assert.That(_scenario.ReadSidecar()!.Handoff!.Value.GetRawText()).IsEqualTo(ticket);
    }

    [Test]
    public async Task PrepareAsync_Should_Publish_A_Null_Sidecar_When_The_Route_Answers_No_Ticket()
    {
        _scenario.EnsureSidecarDirectory();
        await using var server = LoopbackHttpServer.Start(static _ =>
            Json(HttpStatusCode.OK, Fixtures.LoadJson("BackgroundService.pty-handoff-response-null.json")));

        await _scenario.PrepareAsync(server.Endpoint);

        _ = await _scenario.AssertNullSidecarAsync();
    }

    [Test]
    public async Task PrepareAsync_Should_Send_The_Registration_Credential()
    {
        _scenario.EnsureSidecarDirectory();
        await using var server = LoopbackHttpServer.Start(static _ =>
            Json(HttpStatusCode.OK, Fixtures.LoadJson("BackgroundService.pty-handoff-response-ticket.json")));

        await _scenario.PrepareAsync(server.Endpoint);

        await Assert.That(server.Requests.Single().Headers["Authorization"])
            .IsEqualTo("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("opencode:" + Password)));
    }

    [Test]
    public async Task PrepareAsync_Should_Recheck_For_A_Concurrent_Sidecar_Before_Shutting_The_Daemon_Down()
    {
        // pty-handoff.ts: any failure re-checks first; a caller that already prepared and stopped
        // this daemon leaves a fresh sidecar, and the failure then means "gone", not "unable to
        // hand off". The concurrent caller's sidecar lands while the handoff request is in flight.
        ServiceRegistration? registration = null;
        await using var server = LoopbackHttpServer.Start(path =>
        {
            if (path == ShutdownPath)
            {
                return NoContent();
            }

            Seed(_scenario.SidecarPath(), Sidecar(registration!, Ticket, NowMilliseconds + 60_000).ToUtf8Json());
            return Json(HttpStatusCode.NotFound, "{}");
        });
        registration = ServicePtyHandoffScenario.Registration(server.Endpoint);

        await _scenario.PrepareAsync(registration);

        await Assert.That(server.RequestPaths).DoesNotContain(ShutdownPath);
        await Assert.That(_scenario.ReadSidecar()!.Handoff).IsNotNull();
    }

    [Test]
    [Arguments("srv_other", FixedUrl)]
    [Arguments(SourceId, "http://127.0.0.1:49999")]
    public async Task CompleteAsync_Should_Clear_A_Sidecar_Whose_Source_Differs_In_One_Member(string id, string address)
    {
        SeedFreshSidecar(Ticket);
        var winner = new ServiceRegistration(id, ServicePtyHandoffScenario.Version, address, new Uri(address), RegisteredPid, Password);

        await _scenario.Handoff().CompleteAsync(_scenario.RegistrationPath(), winner, CancellationToken.None);

        await Assert.That(_scenario.ReadSidecar()).IsNull();
    }

    [Test]
    public async Task EnvironmentAsync_Should_Adopt_When_The_Registration_Cannot_Be_Decoded()
    {
        // The SDK reads an undecodable registration as absent, which the adoption rule accepts;
        // upstream compares whatever JSON.parse returned instead (recorded, harmless).
        Seed(_scenario.RegistrationPath(), "{\"id\":123}");
        SeedFreshSidecar(Ticket);

        var overlay = await EnvironmentAsync(caller: null);

        await Assert.That(overlay["OPENCODE_PTY_HANDOFF"]).IsEqualTo(Ticket.GetRawText());
    }

    /// <summary>The wait is a hang guard; the assertion names a ticket request that never arrived.</summary>
    private static async Task AssertTicketRequestArrivedAsync(TaskCompletionSource<bool> arrived, Task pending)
    {
        Task[] arrival = [arrived.Task, pending];
        _ = await BoundedWait.CompletesWithinAsync(Task.WhenAny(arrival), PreparationHangGuard, TimeProvider.System);
        await Assert.That(arrived.Task.IsCompleted).IsTrue();
    }

    private static JsonElement LoadTicket()
    {
        using var document = JsonDocument.Parse(Fixtures.LoadJson("BackgroundService.pty-handoff-valid.json"));
        return document.RootElement.GetProperty("handoff").Clone();
    }

    private static LoopbackHttpServer StartShutdownFallbackServer(string fixture) =>
        LoopbackHttpServer.Start(path => path == ShutdownPath
            ? NoContent()
            : Json(HttpStatusCode.OK, Fixtures.LoadJson(fixture)));

    private void SeedFreshSidecar(JsonElement? handoff) =>
        Seed(_scenario.SidecarPath(), Sidecar(FixedRegistration(), handoff, NowMilliseconds + 60_000).ToUtf8Json());

    private Task<IReadOnlyDictionary<string, string?>> EnvironmentAsync(IReadOnlyDictionary<string, string>? caller) =>
        _scenario.Handoff().EnvironmentAsync(_scenario.RegistrationPath(), caller, CancellationToken.None);

    private static PtyHandoffSidecar Sidecar(ServiceRegistration registration, JsonElement? handoff, double expiresAt) =>
        new()
        {
            SourceId = registration.Id,
            SourcePid = registration.ProcessId,
            SourceUrl = registration.Url,
            Handoff = handoff,
            ExpiresAt = expiresAt,
        };

    /// <summary>A fresh ticket-bearing sidecar naming <see cref="FixedRegistration"/>'s source with pid 48213.5.</summary>
    private static string FractionalSourceSidecar() =>
        "{\"source\":{\"id\":\"" + SourceId + "\",\"pid\":48213.5,\"url\":\"" + FixedUrl + "\"},\"handoff\":"
        + Ticket.GetRawText() + ",\"expiresAt\":" + (NowMilliseconds + 60_000).ToString(CultureInfo.InvariantCulture) + "}";

    private static Dictionary<string, string> Caller() =>
        new(StringComparer.Ordinal) { ["KEEP"] = "1" };

    private void Seed(string path, string content) => Seed(path, Encoding.UTF8.GetBytes(content));

    private void Seed(string path, byte[] bytes)
    {
        _ = _scenario.FileSystem.Directory.CreateDirectory(_scenario.FileSystem.Path.GetDirectoryName(path)!);
        // Same net472 Testably constraint as ReadSidecar.
#pragma warning disable MA0045
        _scenario.FileSystem.File.WriteAllBytes(path, bytes);
#pragma warning restore MA0045
    }
}
