using System.Net;
using NSubstitute;
using OpenCode.Sdk.Internal;
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
    public const long NowMilliseconds = 1_700_000_000_000;
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeMilliseconds(NowMilliseconds);

    /// <summary>
    /// The request bound every preparation test hands the product, far past any loopback exchange a
    /// loaded runner still completes: the daemon double's answer, a recorded request, or the caller's
    /// cancellation decides which path the preparation takes, never this bound expiring first. Only
    /// the test about the bound itself passes a short one.
    /// </summary>
    public static readonly TimeSpan RequestTimeout = ServiceTimingData.PatientRequestTimeout;

    public ServicePtyHandoffScenario()
    {
        Clock.UtcNow.Returns(Now);
    }

    public MockFileSystem FileSystem { get; } = new();

    public IServiceClock Clock { get; } = Substitute.For<IServiceClock>();

    public static ServiceRegistration Registration(Uri endpoint) =>
        new(SourceId, Version, endpoint.ToString(), endpoint, RegisteredPid, Password);

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
