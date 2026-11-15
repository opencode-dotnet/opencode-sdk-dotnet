using System.Net;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Hosting.BackgroundService.Support;
using OpenCode.Sdk.Tests.Support;
using Testably.Abstractions.Testing;

namespace OpenCode.Sdk.Tests.Hosting.BackgroundService.Handoff;

internal sealed class ServicePtyHandoffScenario
{
    public const int RegisteredPid = 48213;
    public const string SourceId = "srv_1";
    public const string Version = "2.0.3";
    public const string Password = "s3cr3t-p455w0rd";
    public const string HandoffPath = "/api/experimental/persistent-pty/handoff";
    public const string ShutdownPath = "/api/experimental/persistent-pty/shutdown";
    public const string FixedUrl = "http://127.0.0.1:49374";
    public const long NowMilliseconds = 1_700_000_000_000;
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeMilliseconds(NowMilliseconds);

    public static readonly FixtureLoader Fixtures = new();

    /// <summary>
    /// Bounds a wait for an event; it decides no verdict. A ticket request's arrival is awaited under it
    /// too: the preparation's own bound runs on the scenario's manual clock, which these tests never
    /// advance, so only this guard ends a wait for a request that never arrives, and the arrival
    /// assertion after it names the miss.
    /// </summary>
    public static readonly TimeSpan PreparationHangGuard = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The request bound every preparation test hands the product, far past any loopback exchange a
    /// loaded runner still completes: the daemon double's answer, a recorded request, or the caller's
    /// cancellation decides which path the preparation takes, never this bound expiring first. Only
    /// the test about the bound itself passes a short one.
    /// </summary>
    public static readonly TimeSpan RequestTimeout = ServiceTimingData.PatientRequestTimeout;

    public MockFileSystem FileSystem { get; } = new();

    public ManualTimeProvider Clock { get; } = new(Now);

    public static ServiceRegistration Registration(Uri endpoint) =>
        new(SourceId, Version, endpoint.ToString(), endpoint, RegisteredPid, Password);

    public static ServiceRegistration FixedRegistration() =>
        new(SourceId, Version, FixedUrl, new Uri(FixedUrl), RegisteredPid, Password);

    public Task PrepareAsync(Uri endpoint) => PrepareAsync(Registration(endpoint));

    public async Task PrepareAsync(ServiceRegistration registration) =>
        await Handoff().PrepareAsync(RegistrationPath(), registration, RequestTimeout, CancellationToken.None);

    public ServicePtyHandoff Handoff() => new(new TestablyServiceFileSystem(FileSystem), Clock, new ServicePtyShutdown());

    public async Task<PtyHandoffSidecar> AssertNullSidecarAsync()
    {
        var sidecar = ReadSidecar();
        await Assert.That(sidecar).IsNotNull();
        await Assert.That(sidecar!.Handoff).IsNull();
        await Assert.That(sidecar.ExpiresAt).IsEqualTo(NowMilliseconds + 30_000d);
        return sidecar;
    }

    public static LoopbackHttpResponse Pending(TaskCompletionSource<bool> arrived)
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

    public static LoopbackHttpResponse Json(HttpStatusCode status, string body) =>
        new() { StatusCode = status, ContentType = "application/json", Body = body };

    public static LoopbackHttpResponse NoContent() => new() { StatusCode = HttpStatusCode.NoContent };

    public string RegistrationPath() =>
        FileSystem.Path.Combine(FileSystem.Path.GetTempPath(), "service-handoff", "service.json");

    public string SidecarPath() => RegistrationPath() + ServicePtyHandoff.SidecarSuffix;

    public PtyHandoffSidecar? ReadSidecar()
    {
        if (!FileSystem.File.Exists(SidecarPath()))
        {
            return null;
        }

        // The net472 leg compiles the netstandard2.0 Testably asset, which has no async overloads;
        // the read is arrangement over an in-memory fake, so the synchronous member is the shape
        // every test leg shares (the ServiceEnsurerTests.Seed precedent).
#pragma warning disable MA0045
        return PtyHandoffSidecar.TryRead(FileSystem.File.ReadAllBytes(SidecarPath()));
#pragma warning restore MA0045
    }

    public IReadOnlyList<string> TemporaryFiles() =>
    [
        .. FileSystem.Directory
            .GetFiles(FileSystem.Path.GetDirectoryName(SidecarPath())!)
            .Where(static path => path.EndsWith(".tmp", StringComparison.Ordinal)),
    ];

    public void EnsureSidecarDirectory() =>
        _ = FileSystem.Directory.CreateDirectory(FileSystem.Path.GetDirectoryName(SidecarPath())!);
}
