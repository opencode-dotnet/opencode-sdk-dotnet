using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Internal.BackgroundService.Abstractions;
using OpenCode.Sdk.Internal.BackgroundService.Handoff;
using OpenCode.Sdk.Internal.BackgroundService.Registration;
using OpenCode.Sdk.Internal.BackgroundService.Stop;
using OpenCode.Sdk.Tests.BackgroundService.Registration;
using OpenCode.Sdk.Tests.Support;
using OpenCode.Sdk.TestSupport;
using Testably.Abstractions.Testing;

namespace OpenCode.Sdk.Tests.BackgroundService.Handoff;

/// <summary>
/// The pinned client's sidecar orchestration (<c>pty-handoff.ts</c>) over the Testably
/// filesystem double and a real loopback HTTP daemon double: publication through a temporary plus
/// rename, the skip-when-current fast path, the best-effort shutdown fallback that every failed or
/// refused ticket takes, the concurrent-prepare recheck, the environment adoption matrix,
/// completion by source, and the idempotent clear.
/// </summary>
public sealed class ServicePtyHandoffTests
{
    private const int RegisteredPid = 48213;
    private const string SourceId = "srv_1";
    private const string Version = "2.0.3";
    private const string Password = "s3cr3t-p455w0rd";
    private const string FixedUrl = "http://127.0.0.1:49374";
    private const string HandoffPath = "/api/experimental/persistent-pty/handoff";
    private const string ShutdownPath = "/api/experimental/persistent-pty/shutdown";
    private const long TicketExpiry = 1700000060000;
    private const long NowMilliseconds = 1_700_000_000_000;
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeMilliseconds(NowMilliseconds);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private static readonly FixtureLoader Fixtures = new();
    private static readonly JsonElement Ticket = LoadTicket();

    private readonly MockFileSystem _fileSystem = new();
    private readonly IServiceClock _clock = Substitute.For<IServiceClock>();

    public ServicePtyHandoffTests()
    {
        _clock.UtcNow.Returns(Now);
    }

    [Test]
    public async Task PrepareAsync_Should_Publish_A_Sidecar_From_The_Handoff_Ticket()
    {
        EnsureSidecarDirectory();
        await using var server = LoopbackHttpServer.Start(static _ =>
            Json(HttpStatusCode.OK, Fixtures.LoadJson("BackgroundService.pty-handoff-response-ticket.json")));
        var registration = Registration(server.Endpoint);

        await Handoff().PrepareAsync(RegistrationPath(), registration, RequestTimeout, CancellationToken.None);

        var sidecar = ReadSidecar();
        await Assert.That(sidecar).IsNotNull();
        await Assert.That(sidecar!.SourceId).IsEqualTo(SourceId);
        await Assert.That(sidecar.SourcePid).IsEqualTo(RegisteredPid);
        await Assert.That(sidecar.SourceUrl).IsEqualTo(registration.Url);
        await Assert.That(sidecar.ExpiresAt).IsEqualTo(TicketExpiry);
        await Assert.That(sidecar.Handoff!.Value.GetRawText()).IsEqualTo(Ticket.GetRawText());
        await Assert.That(TemporaryFiles()).IsEmpty();
        await Assert.That(server.RequestPaths).IsEquivalentTo([HandoffPath]);
    }

    [Test]
    public async Task PrepareAsync_Should_Reuse_A_Fresh_Matching_Sidecar_Without_Contacting_The_Daemon()
    {
        await using var server = LoopbackHttpServer.Start(static _ =>
            Json(HttpStatusCode.InternalServerError, "{}"));
        var registration = Registration(server.Endpoint);
        Seed(SidecarPath(), Sidecar(registration, handoff: null, NowMilliseconds + 60_000).ToUtf8Json());

        await Handoff().PrepareAsync(RegistrationPath(), registration, RequestTimeout, CancellationToken.None);

        await Assert.That(server.Requests).IsEmpty();
        await Assert.That(ReadSidecar()).IsNotNull();
    }

    [Test]
    public async Task PrepareAsync_Should_Shut_The_Daemon_Down_And_Publish_A_Null_Sidecar_When_The_Route_Is_Absent()
    {
        EnsureSidecarDirectory();
        await using var server = LoopbackHttpServer.Start(path => path == ShutdownPath
            ? NoContent()
            : Json(HttpStatusCode.NotFound, "{}"));
        var registration = Registration(server.Endpoint);

        await Handoff().PrepareAsync(RegistrationPath(), registration, RequestTimeout, CancellationToken.None);

        var sidecar = ReadSidecar();
        await Assert.That(sidecar).IsNotNull();
        await Assert.That(sidecar!.Handoff).IsNull();
        await Assert.That(sidecar.ExpiresAt).IsEqualTo(NowMilliseconds + 30_000d);
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
        EnsureSidecarDirectory();
        await using var server = LoopbackHttpServer.Start(path => path == ShutdownPath
            ? NoContent()
            : Json(HttpStatusCode.OK, Fixtures.LoadJson(fixture)));

        await PrepareAsync(server.Endpoint);

        var sidecar = ReadSidecar();
        await Assert.That(sidecar).IsNotNull();
        await Assert.That(sidecar!.Handoff).IsNull();
        await Assert.That(sidecar.ExpiresAt).IsEqualTo(NowMilliseconds + 30_000d);
        await Assert.That(server.RequestPaths).IsEquivalentTo([HandoffPath, ShutdownPath]);
    }

    [Test]
    public async Task PrepareAsync_Should_Shut_The_Daemon_Down_And_Publish_A_Null_Sidecar_When_The_Daemon_Answers_An_Error()
    {
        EnsureSidecarDirectory();
        await using var server = LoopbackHttpServer.Start(static path => path == ShutdownPath
            ? NoContent()
            : Json(HttpStatusCode.InternalServerError, "{}"));

        await PrepareAsync(server.Endpoint);

        await Assert.That(ReadSidecar()!.Handoff).IsNull();
        await Assert.That(server.RequestPaths).IsEquivalentTo([HandoffPath, ShutdownPath]);
    }

    [Test]
    public async Task PrepareAsync_Should_Complete_Without_A_Sidecar_When_The_Publish_Fails()
    {
        await using var server = LoopbackHttpServer.Start(static _ =>
            Json(HttpStatusCode.OK, Fixtures.LoadJson("BackgroundService.pty-handoff-response-ticket.json")));
        var fileSystem = Substitute.For<IServiceFileSystem>();
        fileSystem.TryReadAllBytesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((byte[]?)null);
        fileSystem.TryCreateExclusiveAsync(Arg.Any<string>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new IOException("The sidecar directory vanished."));

        await new ServicePtyHandoff(fileSystem, _clock, new ServicePtyShutdown()).PrepareAsync(
            RegistrationPath(), Registration(server.Endpoint), RequestTimeout, CancellationToken.None);

        _ = fileSystem.Received(1).TryCreateExclusiveAsync(Arg.Any<string>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>());
        fileSystem.DidNotReceiveWithAnyArgs().Rename(default!, default!);
    }

    [Test]
    public async Task PrepareAsync_Should_Return_When_A_Concurrent_Caller_Published_A_Fresh_Matching_Sidecar()
    {
        var arrived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = LoopbackHttpServer.Start(_ => Pending(arrived));
        var registration = Registration(server.Endpoint);

        var pending = Handoff().PrepareAsync(RegistrationPath(), registration, RequestTimeout, CancellationToken.None);
        _ = await arrived.Task.WaitAsync(RequestTimeout);
        Seed(SidecarPath(), Sidecar(registration, Ticket, NowMilliseconds + 60_000).ToUtf8Json());
        server.ReleaseResponses();

        await pending;

        await Assert.That(ReadSidecar()!.Handoff).IsNotNull();
    }

    [Test]
    public async Task EnvironmentAsync_Should_Adopt_A_Fresh_Matching_Sidecar()
    {
        Seed(RegistrationPath(), ServiceRegistrationData.Passwordless);
        Seed(SidecarPath(), Sidecar(FixedRegistration(), Ticket, NowMilliseconds + 60_000).ToUtf8Json());

        var overlay = await Handoff().EnvironmentAsync(RegistrationPath(), Caller(), CancellationToken.None);

        await Assert.That(overlay["OPENCODE_PTY_HANDOFF"]).IsEqualTo(Ticket.GetRawText());
        await Assert.That(overlay["KEEP"]).IsEqualTo("1");
    }

    [Test]
    public async Task EnvironmentAsync_Should_Adopt_When_The_Registration_Is_Absent()
    {
        Seed(SidecarPath(), Sidecar(FixedRegistration(), Ticket, NowMilliseconds + 60_000).ToUtf8Json());

        var overlay = await Handoff().EnvironmentAsync(RegistrationPath(), callerEnvironment: null, CancellationToken.None);

        await Assert.That(overlay["OPENCODE_PTY_HANDOFF"]).IsEqualTo(Ticket.GetRawText());
    }

    [Test]
    public async Task EnvironmentAsync_Should_Remove_The_Variable_When_The_Sidecar_Is_Expired()
    {
        Seed(RegistrationPath(), ServiceRegistrationData.Passwordless);
        Seed(SidecarPath(), Sidecar(FixedRegistration(), Ticket, NowMilliseconds - 1).ToUtf8Json());

        var overlay = await Handoff().EnvironmentAsync(RegistrationPath(), Caller(), CancellationToken.None);

        await Assert.That(overlay["OPENCODE_PTY_HANDOFF"]).IsNull();
    }

    [Test]
    public async Task EnvironmentAsync_Should_Remove_The_Variable_When_The_Source_Does_Not_Match_The_Registration()
    {
        Seed(RegistrationPath(), ServiceRegistrationData.DuplicatePidResolved);
        Seed(SidecarPath(), Sidecar(FixedRegistration(), Ticket, NowMilliseconds + 60_000).ToUtf8Json());

        var overlay = await Handoff().EnvironmentAsync(RegistrationPath(), Caller(), CancellationToken.None);

        await Assert.That(overlay["OPENCODE_PTY_HANDOFF"]).IsNull();
    }

    /// <summary>
    /// A fractional source pid (<c>typeof pid === "number"</c> admits it) is never the registration's
    /// integer pid, so <c>same</c> fails even where truncation would land on the registered pid.
    /// </summary>
    [Test]
    public async Task EnvironmentAsync_Should_Remove_The_Variable_When_The_Source_Pid_Is_Fractional_And_A_Registration_Is_Current()
    {
        Seed(RegistrationPath(), ServiceRegistrationData.Passwordless);
        Seed(SidecarPath(), FractionalSourceSidecar());

        var overlay = await Handoff().EnvironmentAsync(RegistrationPath(), Caller(), CancellationToken.None);

        await Assert.That(overlay["OPENCODE_PTY_HANDOFF"]).IsNull();
        await Assert.That(overlay["KEEP"]).IsEqualTo("1");
    }

    /// <summary><c>current === undefined</c> adopts whatever source the sidecar names, a fractional pid included.</summary>
    [Test]
    public async Task EnvironmentAsync_Should_Adopt_A_Fractional_Source_Pid_When_No_Registration_Is_Readable()
    {
        Seed(SidecarPath(), FractionalSourceSidecar());

        var overlay = await Handoff().EnvironmentAsync(RegistrationPath(), callerEnvironment: null, CancellationToken.None);

        await Assert.That(overlay["OPENCODE_PTY_HANDOFF"]).IsEqualTo(Ticket.GetRawText());
    }

    [Test]
    public async Task EnvironmentAsync_Should_Remove_The_Variable_When_The_Sidecar_Is_Absent()
    {
        var overlay = await Handoff().EnvironmentAsync(RegistrationPath(), Caller(), CancellationToken.None);

        await Assert.That(overlay["OPENCODE_PTY_HANDOFF"]).IsNull();
        await Assert.That(overlay["KEEP"]).IsEqualTo("1");
    }

    [Test]
    public async Task EnvironmentAsync_Should_Replace_A_Caller_Provided_Handoff_Value()
    {
        Seed(RegistrationPath(), ServiceRegistrationData.Passwordless);
        Seed(SidecarPath(), Sidecar(FixedRegistration(), Ticket, NowMilliseconds + 60_000).ToUtf8Json());
        var caller = new Dictionary<string, string>(StringComparer.Ordinal) { ["OPENCODE_PTY_HANDOFF"] = "stale" };

        var overlay = await Handoff().EnvironmentAsync(RegistrationPath(), caller, CancellationToken.None);

        await Assert.That(overlay["OPENCODE_PTY_HANDOFF"]).IsEqualTo(Ticket.GetRawText());
    }

    [Test]
    public async Task CompleteAsync_Should_Clear_A_Foreign_Source_Sidecar()
    {
        Seed(SidecarPath(), Sidecar(FixedRegistration(), Ticket, NowMilliseconds + 60_000).ToUtf8Json());
        var winner = new ServiceRegistration(SourceId, Version, FixedUrl, new Uri(FixedUrl), RegisteredPid + 1, Password);

        await Handoff().CompleteAsync(RegistrationPath(), winner, CancellationToken.None);

        await Assert.That(ReadSidecar()).IsNull();
    }

    [Test]
    public async Task CompleteAsync_Should_Keep_A_Same_Source_Sidecar_Even_When_Stale()
    {
        Seed(SidecarPath(), Sidecar(FixedRegistration(), Ticket, NowMilliseconds - 1).ToUtf8Json());

        await Handoff().CompleteAsync(RegistrationPath(), FixedRegistration(), CancellationToken.None);

        await Assert.That(ReadSidecar()).IsNotNull();
    }

    [Test]
    public async Task CompleteAsync_Should_Do_Nothing_When_The_Sidecar_Is_Absent()
    {
        await Handoff().CompleteAsync(RegistrationPath(), FixedRegistration(), CancellationToken.None);

        await Assert.That(ReadSidecar()).IsNull();
    }

    [Test]
    public async Task ClearAsync_Should_Remove_The_Sidecar_And_Be_Idempotent()
    {
        Seed(SidecarPath(), Sidecar(FixedRegistration(), null, NowMilliseconds + 60_000).ToUtf8Json());

        await Handoff().ClearAsync(RegistrationPath(), CancellationToken.None);
        await Assert.That(ReadSidecar()).IsNull();

        await Handoff().ClearAsync(RegistrationPath(), CancellationToken.None);
    }

    [Test]
    public async Task ClearAsync_Should_Report_A_Sidecar_That_Cannot_Be_Removed()
    {
        var fileSystem = Substitute.For<IServiceFileSystem>();
        fileSystem.TryDelete(SidecarPath()).Throws(new UnauthorizedAccessException("The sidecar is locked."));

        var exception = await Assert
            .That(async () => await new ServicePtyHandoff(fileSystem, _clock, new ServicePtyShutdown()).ClearAsync(RegistrationPath(), CancellationToken.None))
            .Throws<OpenCodeServerException>();

        await Assert.That(exception!.InnerException).IsTypeOf<UnauthorizedAccessException>();
    }

    [Test]
    public async Task PrepareAsync_Should_Rethrow_Caller_Cancellation()
    {
        var cancelled = new CancellationToken(canceled: true);

        _ = await Assert
            .That(async () => await Handoff().PrepareAsync(RegistrationPath(), FixedRegistration(), RequestTimeout, cancelled))
            .Throws<OperationCanceledException>();
    }

    /// <summary>A cancellation that arrives while the ticket request is in flight is the caller's, never a preparation failure.</summary>
    [Test]
    public async Task PrepareAsync_Should_Rethrow_Caller_Cancellation_During_The_Ticket_Request()
    {
        var arrived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = LoopbackHttpServer.Start(_ => Pending(arrived));
        using var caller = new CancellationTokenSource();

        var pending = Handoff().PrepareAsync(RegistrationPath(), Registration(server.Endpoint), RequestTimeout, caller.Token);
        _ = await arrived.Task.WaitAsync(RequestTimeout);
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
        Seed(RegistrationPath(), ServiceRegistrationData.Passwordless);
        Seed(SidecarPath(), Sidecar(FixedRegistration(), handoff: null, NowMilliseconds + 60_000).ToUtf8Json());

        var overlay = await Handoff().EnvironmentAsync(RegistrationPath(), Caller(), CancellationToken.None);

        await Assert.That(overlay["OPENCODE_PTY_HANDOFF"]).IsNull();
        await Assert.That(overlay["KEEP"]).IsEqualTo("1");
    }

    [Test]
    public async Task PrepareAsync_Should_Take_The_Fallback_When_The_Request_Bound_Expires()
    {
        // The ticket request and the shutdown both outlive their bound; neither expiry is the
        // caller's cancellation, so both are failures the best-effort path absorbs.
        EnsureSidecarDirectory();
        await using var server = LoopbackHttpServer.Start(static _ => Hold(HttpStatusCode.OK, "{}"));

        await Handoff().PrepareAsync(
            RegistrationPath(), Registration(server.Endpoint), TimeSpan.FromMilliseconds(200), CancellationToken.None);

        await Assert.That(ReadSidecar()!.Handoff).IsNull();
        await Assert.That(server.RequestPaths).Contains(ShutdownPath);
        server.ReleaseResponses();
    }

    [Test]
    public async Task PrepareAsync_Should_Publish_The_Ticket_Exactly_As_The_Route_Answered_It()
    {
        // pty-handoff.ts writes body.handoff as it came: a member this pin does not know yet must
        // still reach the replacement daemon, whose own schema may require it.
        const string ticket = "{\"directory\":\"/work\",\"instanceID\":\"pty_1\",\"ticket\":\"t-1\",\"expiresAt\":1700000060000,\"signature\":\"sig-1\"}";
        EnsureSidecarDirectory();
        await using var server = LoopbackHttpServer.Start(static _ => Json(HttpStatusCode.OK, "{\"handoff\":" + ticket + "}"));

        await PrepareAsync(server.Endpoint);

        await Assert.That(ReadSidecar()!.Handoff!.Value.GetRawText()).IsEqualTo(ticket);
    }

    [Test]
    public async Task PrepareAsync_Should_Publish_A_Null_Sidecar_When_The_Route_Answers_No_Ticket()
    {
        EnsureSidecarDirectory();
        await using var server = LoopbackHttpServer.Start(static _ =>
            Json(HttpStatusCode.OK, Fixtures.LoadJson("BackgroundService.pty-handoff-response-null.json")));

        await PrepareAsync(server.Endpoint);

        var sidecar = ReadSidecar();
        await Assert.That(sidecar).IsNotNull();
        await Assert.That(sidecar!.Handoff).IsNull();
        await Assert.That(sidecar.ExpiresAt).IsEqualTo(NowMilliseconds + 30_000d);
    }

    [Test]
    public async Task PrepareAsync_Should_Send_The_Registration_Credential()
    {
        EnsureSidecarDirectory();
        await using var server = LoopbackHttpServer.Start(static _ =>
            Json(HttpStatusCode.OK, Fixtures.LoadJson("BackgroundService.pty-handoff-response-ticket.json")));

        await PrepareAsync(server.Endpoint);

        await Assert.That(server.Requests.Single().Headers["Authorization"])
            .IsEqualTo("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("opencode:" + Password)));
    }

    [Test]
    public async Task PrepareAsync_Should_Publish_A_Null_Sidecar_When_The_Shutdown_Fails()
    {
        EnsureSidecarDirectory();
        await using var server = LoopbackHttpServer.Start(static path => path == ShutdownPath
            ? Json(HttpStatusCode.InternalServerError, "{}")
            : Json(HttpStatusCode.NotFound, "{}"));

        await PrepareAsync(server.Endpoint);

        await Assert.That(ReadSidecar()!.Handoff).IsNull();
        await Assert.That(server.RequestPaths).Contains(ShutdownPath);
    }

    [Test]
    public async Task PrepareAsync_Should_Publish_A_Null_Sidecar_When_The_Shutdown_Route_Is_Absent_Too()
    {
        EnsureSidecarDirectory();
        await using var server = LoopbackHttpServer.Start(static _ => Json(HttpStatusCode.NotFound, "{}"));

        await PrepareAsync(server.Endpoint);

        await Assert.That(ReadSidecar()!.Handoff).IsNull();
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

            Seed(SidecarPath(), Sidecar(registration!, Ticket, NowMilliseconds + 60_000).ToUtf8Json());
            return Json(HttpStatusCode.NotFound, "{}");
        });
        registration = Registration(server.Endpoint);

        await Handoff().PrepareAsync(RegistrationPath(), registration, RequestTimeout, CancellationToken.None);

        await Assert.That(server.RequestPaths).DoesNotContain(ShutdownPath);
        await Assert.That(ReadSidecar()!.Handoff).IsNotNull();
    }

    [Test]
    [Arguments("srv_other", FixedUrl)]
    [Arguments(SourceId, "http://127.0.0.1:49999")]
    public async Task CompleteAsync_Should_Clear_A_Sidecar_Whose_Source_Differs_In_One_Member(string id, string address)
    {
        Seed(SidecarPath(), Sidecar(FixedRegistration(), Ticket, NowMilliseconds + 60_000).ToUtf8Json());
        var winner = new ServiceRegistration(id, Version, address, new Uri(address), RegisteredPid, Password);

        await Handoff().CompleteAsync(RegistrationPath(), winner, CancellationToken.None);

        await Assert.That(ReadSidecar()).IsNull();
    }

    [Test]
    public async Task EnvironmentAsync_Should_Adopt_When_The_Registration_Cannot_Be_Decoded()
    {
        // The SDK reads an undecodable registration as absent, which the adoption rule accepts;
        // upstream compares whatever JSON.parse returned instead (recorded, harmless).
        Seed(RegistrationPath(), "{\"id\":123}");
        Seed(SidecarPath(), Sidecar(FixedRegistration(), Ticket, NowMilliseconds + 60_000).ToUtf8Json());

        var overlay = await Handoff().EnvironmentAsync(RegistrationPath(), callerEnvironment: null, CancellationToken.None);

        await Assert.That(overlay["OPENCODE_PTY_HANDOFF"]).IsEqualTo(Ticket.GetRawText());
    }

    private static JsonElement LoadTicket()
    {
        using var document = JsonDocument.Parse(Fixtures.LoadJson("BackgroundService.pty-handoff-valid.json"));
        return document.RootElement.GetProperty("handoff").Clone();
    }

    private async Task PrepareAsync(Uri endpoint) =>
        await Handoff().PrepareAsync(RegistrationPath(), Registration(endpoint), RequestTimeout, CancellationToken.None);

    private ServicePtyHandoff Handoff() => new(new TestablyServiceFileSystem(_fileSystem), _clock, new ServicePtyShutdown());

    private static ServiceRegistration Registration(Uri endpoint) =>
        new(SourceId, Version, endpoint.ToString(), endpoint, RegisteredPid, Password);

    private static ServiceRegistration FixedRegistration() =>
        new(SourceId, Version, FixedUrl, new Uri(FixedUrl), RegisteredPid, Password);

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

    private static LoopbackHttpResponse Pending(TaskCompletionSource<bool> arrived)
    {
        _ = arrived.TrySetResult(true);
        return new LoopbackHttpResponse
        {
            StatusCode = HttpStatusCode.ServiceUnavailable,
            ContentType = "application/json",
            Body = "{}",
            KeepOpen = true,
        };
    }

    /// <summary>An answer the server sends only after <see cref="LoopbackHttpServer.ReleaseResponses"/>.</summary>
    private static LoopbackHttpResponse Hold(HttpStatusCode status, string body) =>
        new() { StatusCode = status, ContentType = "application/json", Body = body, KeepOpen = true };

    private static LoopbackHttpResponse Json(HttpStatusCode status, string body) =>
        new() { StatusCode = status, ContentType = "application/json", Body = body };

    private static LoopbackHttpResponse NoContent() => new() { StatusCode = HttpStatusCode.NoContent };

    private string RegistrationPath() =>
        _fileSystem.Path.Combine(_fileSystem.Path.GetTempPath(), "service-handoff", "service.json");

    private string SidecarPath() => RegistrationPath() + ServicePtyHandoff.SidecarSuffix;

    private PtyHandoffSidecar? ReadSidecar()
    {
        if (!_fileSystem.File.Exists(SidecarPath()))
        {
            return null;
        }

        // The net472 leg compiles the netstandard2.0 Testably asset, which has no async overloads;
        // the read is arrangement over an in-memory fake, so the synchronous member is the shape
        // every test leg shares (the ServiceEnsurerTests.Seed precedent).
#pragma warning disable MA0045
        return PtyHandoffSidecar.TryRead(_fileSystem.File.ReadAllBytes(SidecarPath()));
#pragma warning restore MA0045
    }

    private IReadOnlyList<string> TemporaryFiles() =>
    [
        .. _fileSystem.Directory
            .GetFiles(_fileSystem.Path.GetDirectoryName(SidecarPath())!)
            .Where(static path => path.EndsWith(".tmp", StringComparison.Ordinal)),
    ];

    private void EnsureSidecarDirectory() =>
        _ = _fileSystem.Directory.CreateDirectory(_fileSystem.Path.GetDirectoryName(SidecarPath())!);

    private void Seed(string path, string content) => Seed(path, Encoding.UTF8.GetBytes(content));

    private void Seed(string path, byte[] bytes)
    {
        _ = _fileSystem.Directory.CreateDirectory(_fileSystem.Path.GetDirectoryName(path)!);
        // Same net472 Testably constraint as ReadSidecar.
#pragma warning disable MA0045
        _fileSystem.File.WriteAllBytes(path, bytes);
#pragma warning restore MA0045
    }
}
