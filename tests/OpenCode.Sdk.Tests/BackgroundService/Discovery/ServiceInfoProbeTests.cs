using System.Globalization;
using System.Net;
using System.Text;
using OpenCode.Sdk.Internal.BackgroundService.Discovery;
using OpenCode.Sdk.Internal.BackgroundService.Ensure;
using OpenCode.Sdk.Internal.BackgroundService.Registration;
using OpenCode.Sdk.Tests.BackgroundService.Registration;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.BackgroundService.Discovery;

/// <summary>
/// The raw authenticated info exchange the pinned client performs (<c>probeResult</c>): the
/// root-relative path, the Basic credential, the status-to-state mapping for a modern body, the
/// pid and version gates, the required identity fields, and the separation of the internal bound from the
/// caller's token, all against the loopback server through the SDK's own owned handler.
/// </summary>
public sealed class ServiceInfoProbeTests
{
    private const string Password = ServiceRegistrationData.Password;
    private static readonly ServiceTiming FastTiming = ServiceTiming.Default with { RequestTimeout = TimeSpan.FromMilliseconds(100) };

    [Test]
    public async Task ProbeAsync_Should_Report_A_Ready_Service_And_Send_The_Basic_Credential()
    {
        await using var server = LoopbackHttpServer.Start(static _ => Json(HttpStatusCode.OK, ServiceInfoBodyData.Ready));

        var result = await Probe().ProbeAsync(Registration(server.Endpoint), CancellationToken.None);

        await Assert.That(result.State).IsEqualTo(ServiceState.Ready);
        await Assert.That(result.Version).IsEqualTo(ServiceInfoBodyData.Version);
        await Assert.That(result.TimedOut).IsFalse();
        var request = server.Requests.Single();
        await Assert.That(request.Method).IsEqualTo("GET");
        await Assert.That(request.Path).IsEqualTo("/api/info");
        await Assert.That(request.Headers["Authorization"])
            .IsEqualTo("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("opencode:" + Password)));
    }

    [Test]
    public async Task ProbeAsync_Should_Ignore_Fields_Outside_The_Identity_Decoder()
    {
        await using var server = LoopbackHttpServer.Start(static _ => Json(HttpStatusCode.OK, ServiceInfoBodyData.IgnoredFields));

        var result = await Probe().ProbeAsync(Registration(server.Endpoint), CancellationToken.None);

        await Assert.That(result.State).IsEqualTo(ServiceState.Ready);
        await Assert.That(result.Version).IsEqualTo(ServiceInfoBodyData.Version);
    }

    [Test]
    public async Task ProbeAsync_Should_Resolve_The_Info_Path_Against_The_Authority_Only()
    {
        await using var server = LoopbackHttpServer.Start(static _ => Json(HttpStatusCode.OK, ServiceInfoBodyData.Ready));
        var prefixed = new Uri(server.Endpoint, "/some/prefix/");

        var result = await Probe().ProbeAsync(Registration(prefixed), CancellationToken.None);

        await Assert.That(result.State).IsEqualTo(ServiceState.Ready);
        await Assert.That(server.RequestPaths).IsEquivalentTo(["/api/info"]);
    }

    [Test]
    public async Task ProbeAsync_Should_Send_No_Credential_For_A_Passwordless_Registration()
    {
        await using var server = LoopbackHttpServer.Start(static _ => Json(HttpStatusCode.OK, ServiceInfoBodyData.Ready));

        _ = await Probe().ProbeAsync(Registration(server.Endpoint, password: null), CancellationToken.None);

        await Assert.That(server.Requests.Single().Headers.ContainsKey("Authorization")).IsFalse();
    }

    [Test]
    [Arguments(HttpStatusCode.InternalServerError, (int)ServiceState.Failed)]
    [Arguments(HttpStatusCode.ServiceUnavailable, (int)ServiceState.Waiting)]
    [Arguments(HttpStatusCode.Accepted, (int)ServiceState.Ready)]
    public async Task ProbeAsync_Should_Map_The_Status_Of_A_Modern_Body(HttpStatusCode status, int expected)
    {
        await using var server = LoopbackHttpServer.Start(_ => Json(status, ServiceInfoBodyData.Ready));

        var result = await Probe().ProbeAsync(Registration(server.Endpoint), CancellationToken.None);

        // The enum is internal, so the data row carries its integer; the cast is the assertion's.
        await Assert.That(result.State).IsEqualTo((ServiceState)expected);
    }

    [Test]
    public async Task ProbeAsync_Should_Report_A_Redirect_With_A_Modern_Body_As_Waiting_Without_Following_It()
    {
        await using var server = LoopbackHttpServer.Start(static _ => Json(HttpStatusCode.Found, ServiceInfoBodyData.Ready) with { Location = "/elsewhere" });

        var result = await Probe().ProbeAsync(Registration(server.Endpoint), CancellationToken.None);

        await Assert.That(result.State).IsEqualTo(ServiceState.Waiting);
        await Assert.That(server.RequestPaths).IsEquivalentTo(["/api/info"]);
    }

    [Test]
    [Arguments(ServiceInfoBodyData.MissingIdentity)]
    [Arguments(ServiceInfoBodyData.MissingPid)]
    [Arguments(ServiceInfoBodyData.OtherPid)]
    [Arguments(ServiceInfoBodyData.OtherVersion)]
    [Arguments(ServiceInfoBodyData.PidAboveInt32)]
    [Arguments(ServiceInfoBodyData.PidAboveInt64)]
    [Arguments(ServiceInfoBodyData.FractionalPid)]
    [Arguments(ServiceInfoBodyData.Malformed)]
    [Arguments(ServiceInfoBodyData.InvalidVersionUnicode)]
    [Arguments(ServiceInfoBodyData.ArrayRoot)]
    [Arguments("")]
    public async Task ProbeAsync_Should_Report_No_Service_For_A_Body_That_Is_Not_This_Daemon(string body)
    {
        await using var server = LoopbackHttpServer.Start(_ => Json(HttpStatusCode.OK, body));

        var result = await Probe().ProbeAsync(Registration(server.Endpoint), CancellationToken.None);

        await Assert.That(result.IsService).IsFalse();
        await Assert.That(result.TimedOut).IsFalse();
    }

    /// <summary>
    /// The pinned client decodes <c>pid</c> as a schema integer, <c>Number.isSafeInteger</c> over the
    /// value <c>JSON.parse</c> produced, so the notation the daemon's pid is written in does not matter.
    /// </summary>
    [Test]
    [Arguments(ServiceInfoBodyData.PidWithZeroFraction)]
    [Arguments(ServiceInfoBodyData.PidInExponentNotation)]
    public async Task ProbeAsync_Should_Accept_An_Integral_Pid_In_Any_Number_Notation(string body)
    {
        await using var server = LoopbackHttpServer.Start(_ => Json(HttpStatusCode.OK, body));

        var result = await Probe().ProbeAsync(Registration(server.Endpoint), CancellationToken.None);

        await Assert.That(result.State).IsEqualTo(ServiceState.Ready);
        await Assert.That(result.Version).IsEqualTo(ServiceInfoBodyData.Version);
    }

    [Test]
    public async Task ProbeAsync_Should_Accept_A_Registration_Without_A_Version()
    {
        await using var server = LoopbackHttpServer.Start(static _ => Json(HttpStatusCode.OK, ServiceInfoBodyData.OtherVersion));

        var result = await Probe().ProbeAsync(Registration(server.Endpoint, version: null), CancellationToken.None);

        await Assert.That(result.State).IsEqualTo(ServiceState.Ready);
        await Assert.That(result.Version).IsEqualTo("0.0.0-other");
    }

    [Test]
    public async Task ProbeAsync_Should_Compare_The_Unknown_Version_Literal_As_A_String()
    {
        await using var server = LoopbackHttpServer.Start(static _ => Json(HttpStatusCode.OK, ServiceInfoBodyData.UnknownVersion));

        var matched = await Probe().ProbeAsync(Registration(server.Endpoint, version: "unknown"), CancellationToken.None);
        var mismatched = await Probe().ProbeAsync(Registration(server.Endpoint, version: "2.0.3"), CancellationToken.None);

        await Assert.That(matched.State).IsEqualTo(ServiceState.Ready);
        await Assert.That(mismatched.IsService).IsFalse();
    }

    [Test]
    [Arguments(ServiceInfoBodyData.Ready, false)]
    [Arguments(ServiceInfoBodyData.Malformed, false)]
    [Arguments("", false)]
    [Arguments(ServiceInfoBodyData.Ready, true)]
    public async Task ProbeAsync_Should_Report_An_Incompatible_Service_For_NotFound_Before_Reading_The_Body(string body, bool keepOpen)
    {
        await using var server = LoopbackHttpServer.Start(_ => Json(HttpStatusCode.NotFound, body) with { KeepOpen = keepOpen });

        var result = await Probe().ProbeAsync(Registration(server.Endpoint), CancellationToken.None);

        // The pinned client's own answer (packages/client/src/effect/service.ts:228-240): an
        // authenticated 404 is the registered daemon, present and ready, speaking another protocol.
        server.ReleaseResponses();
        await Assert.That(result.IsService).IsTrue();
        await Assert.That(result.Compatible).IsFalse();
        await Assert.That(result.State).IsEqualTo(ServiceState.Ready);
        await Assert.That(result.Version).IsEqualTo(ServiceInfoBodyData.Version);
        await Assert.That(result.TimedOut).IsFalse();
    }

    [Test]
    public async Task ProbeAsync_Should_Report_No_Service_For_An_Unauthorized_Answer()
    {
        await using var server = LoopbackHttpServer.Start(static _ => new LoopbackHttpResponse { StatusCode = HttpStatusCode.Unauthorized });

        var result = await Probe().ProbeAsync(Registration(server.Endpoint), CancellationToken.None);

        await Assert.That(result.IsService).IsFalse();
        await Assert.That(result.TimedOut).IsFalse();
    }

    /// <summary>
    /// A closed loopback port is "no service", never a timeout, on every host: the pinned client
    /// counts only an aborted fetch as timed out, and Ensure terminates a registered pid after
    /// three of those. Windows reports a refused loopback connect only after its SYN
    /// retransmissions (about two seconds, past the pinned bound), so the probe's transport
    /// disables them the way libuv, Go, and Bun do; the half-second bound here is far under that
    /// delay and far over the refusal it leaves. Keyless NotInParallel: the assertion rides a
    /// wall-clock bound (research log Q157).
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task ProbeAsync_Should_Report_No_Service_When_Nothing_Listens()
    {
        Uri endpoint;
        await using (var server = LoopbackHttpServer.Start(static _ => Json(HttpStatusCode.OK, ServiceInfoBodyData.Ready)))
        {
            endpoint = server.Endpoint;
        }

        var result = await new ServiceInfoProbe(RefusalTiming).ProbeAsync(Registration(endpoint), CancellationToken.None);

        await Assert.That(result.IsService).IsFalse();
        await Assert.That(result.TimedOut).IsFalse();
    }

    [Test]
    public async Task ProbeAsync_Should_Report_A_Timeout_At_The_Injected_Bound()
    {
        await using var server = LoopbackHttpServer.Start(static _ => Json(HttpStatusCode.OK, ServiceInfoBodyData.Ready) with { KeepOpen = true });

        var result = await new ServiceInfoProbe(FastTiming).ProbeAsync(Registration(server.Endpoint), CancellationToken.None);

        await Assert.That(result.TimedOut).IsTrue();
        await Assert.That(result.IsService).IsFalse();
        server.ReleaseResponses();
    }

    [Test]
    public async Task ProbeAsync_Should_Rethrow_Caller_Cancellation_Rather_Than_Report_A_Timeout()
    {
        await using var server = LoopbackHttpServer.Start(static _ => Json(HttpStatusCode.OK, ServiceInfoBodyData.Ready) with { KeepOpen = true });
        var cancelled = new CancellationToken(canceled: true);

        var exception = await Assert
            .That(async () => _ = await Probe().ProbeAsync(Registration(server.Endpoint), cancelled))
            .Throws<OperationCanceledException>();

        await Assert.That(exception!.CancellationToken).IsEqualTo(cancelled);
        await Assert.That(exception.ToString()).DoesNotContain(Password);
        server.ReleaseResponses();
    }

    /// <summary>
    /// A daemon bound to every interface registers the unspecified address; Bun connects to it
    /// (measured on Windows and Linux), .NET refuses it as a target, so the reader's loopback
    /// connect target is what reaches the daemon.
    /// </summary>
    [Test]
    public async Task ProbeAsync_Should_Reach_A_Daemon_Registered_On_The_Unspecified_Address()
    {
        await using var server = LoopbackHttpServer.Start(static _ => Json(HttpStatusCode.OK, ServiceInfoBodyData.Ready));
        var document = $$"""{"version":"{{ServiceInfoBodyData.Version}}","url":"http://0.0.0.0:{{server.Endpoint.Port.ToString(CultureInfo.InvariantCulture)}}","pid":{{ServiceInfoBodyData.Pid.ToString(CultureInfo.InvariantCulture)}},"password":"{{Password}}"}""";
        var registration = ServiceRegistrationReader.TryRead(Encoding.UTF8.GetBytes(document));

        var result = await Probe().ProbeAsync(registration!, CancellationToken.None);

        await Assert.That(result.State).IsEqualTo(ServiceState.Ready);
    }

    /// <summary>
    /// The pinned client reads any rejected exchange as timed out exactly when its signal aborted
    /// (<c>timedOut: signal.aborted</c>), whatever the rejection was: .NET Framework's handler can
    /// surface the bound's abort as an <see cref="HttpRequestException"/>, which must still count.
    /// </summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task ClassifyFailure_Should_Report_A_Timeout_Exactly_When_The_Bound_Expired(bool boundExpired)
    {
        var bound = new CancellationToken(canceled: boundExpired);

        var result = ServiceInfoProbe.ClassifyFailure(CancellationToken.None, bound);

        await Assert.That(result.TimedOut).IsEqualTo(boundExpired);
        await Assert.That(result.IsService).IsFalse();
    }

    [Test]
    public async Task ClassifyFailure_Should_Rethrow_Caller_Cancellation_Whatever_The_Bound()
    {
        var cancelled = new CancellationToken(canceled: true);

        var exception = await Assert
            .That(() => ServiceInfoProbe.ClassifyFailure(cancelled, cancelled))
            .Throws<OperationCanceledException>();

        await Assert.That(exception!.CancellationToken).IsEqualTo(cancelled);
    }

    [Test]
    public async Task IsTransportFailure_Should_Admit_The_Exchange_Failures_And_Nothing_Else()
    {
        await Assert.That(ServiceInfoProbe.IsTransportFailure(new HttpRequestException())).IsTrue();
        await Assert.That(ServiceInfoProbe.IsTransportFailure(new IOException())).IsTrue();
        await Assert.That(ServiceInfoProbe.IsTransportFailure(new OperationCanceledException())).IsTrue();
        await Assert.That(ServiceInfoProbe.IsTransportFailure(new ObjectDisposedException("content"))).IsTrue();
        await Assert.That(ServiceInfoProbe.IsTransportFailure(new InvalidOperationException())).IsFalse();
    }

    [Test]
    public async Task The_Result_Should_Never_Render_The_Credential()
    {
        await using var server = LoopbackHttpServer.Start(static _ => Json(HttpStatusCode.OK, ServiceInfoBodyData.Ready));

        var result = await Probe().ProbeAsync(Registration(server.Endpoint), CancellationToken.None);

        await Assert.That(result.ToString()).DoesNotContain(Password);
        await Assert.That(result.ToString()).DoesNotContain("Basic");
    }
    /// <summary>
    /// Classification tests are about the answer, not the bound: a loaded runner (the macOS leg,
    /// three assemblies at once) can miss the pinned two-second request timeout on a loopback
    /// exchange, which would report a timeout where a state was expected. The bound itself is
    /// proven once, with <see cref="FastTiming"/>, against a kept-open response.
    /// </summary>
    private static readonly ServiceTiming PatientTiming = ServiceTiming.Default with { RequestTimeout = TimeSpan.FromSeconds(30) };

    /// <summary>Under the Windows SYN-retransmission delay, over the refusal the probe's transport leaves.</summary>
    private static readonly ServiceTiming RefusalTiming = ServiceTiming.Default with { RequestTimeout = TimeSpan.FromMilliseconds(500) };

    private static ServiceInfoProbe Probe() => new(PatientTiming);
    private static ServiceRegistration Registration(Uri endpoint, string? version = ServiceInfoBodyData.Version, string? password = Password) =>
        new("srv_1", version, endpoint.ToString(), endpoint, ServiceInfoBodyData.Pid, password);

    private static LoopbackHttpResponse Json(HttpStatusCode status, string body) =>
        new() { StatusCode = status, ContentType = "application/json", Body = body };
}
