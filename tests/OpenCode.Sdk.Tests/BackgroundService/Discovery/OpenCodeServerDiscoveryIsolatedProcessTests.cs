using System.Globalization;
using System.Net;
using OpenCode.Sdk.Tests.Support;
using OpenCode.Sdk.TestSupport;
using Testably.Abstractions;

namespace OpenCode.Sdk.Tests.BackgroundService.Discovery;

/// <summary>
/// The environment reading of <c>DiscoverAsync</c>, proven from a process whose environment the
/// test owns entirely (the isolated discovery executable): the default shared registration under
/// <c>XDG_STATE_HOME</c>, the home-directory fallback with no XDG root at all, and a stale
/// registration answered without waiting out the bound. The daemon each child probes is a loopback
/// health server hosted in this test process, so no test reads the developer's profile or
/// registration.
/// </summary>
/// <remarks>
/// Every child probes under the patient bound of <see cref="ServiceTimingData"/>, because these
/// tests are about the answer each environment leads to: a child starved of CPU on a loaded runner
/// can report a timeout under the pinned two seconds although its socket was refused well inside
/// them, since the probe counts a failure as timed out whenever its bound has expired by the time
/// the failure reaches it. The class is keyless <c>[NotInParallel]</c> because the
/// stale-registration test reads a wall-clock interval inside its child and the daemon the other
/// two probe lives in this process: running these alone after every other test keeps the host,
/// and the children it starts, as quiet as the run allows.
/// </remarks>
[NotInParallel]
public sealed class OpenCodeServerDiscoveryIsolatedProcessTests
{
    private const string IsolatedPassword = "isolated-p455";

    private static readonly RealFileSystem FileSystem = new();

    [Test]
    [Timeout(120_000)]
    public async Task DiscoverAsync_Should_Use_The_Default_Shared_Registration(CancellationToken cancellationToken)
    {
        using var root = new TestRunRoot(FileSystem);
        await using var health = LoopbackHttpServer.Start(static _ => ReadyHealth());
        var state = FileSystem.Path.Combine(root.Path, "state");
        Seed(FileSystem.Path.Combine(state, "opencode", "service.json"), health.Endpoint);
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["XDG_STATE_HOME"] = state,
            ["XDG_CONFIG_HOME"] = FileSystem.Path.Combine(root.Path, "config"),
            ["OPENCODE_CONFIG_DIR"] = null,
        };

        var result = await new ServiceFixtureCommand(FileSystem)
            .RunAsync(["discover-default", ServiceTimingData.PatientRequestTimeoutMilliseconds], environment, cancellationToken);

        await Assert.That(result.ExitCode).IsEqualTo(0).Because(result.StandardError);
        await Assert.That(result.StandardOutput.Trim())
            .IsEqualTo(ServiceFixtureOutput.FoundLine(ServiceInfoBodyData.Pid, health.Endpoint))
            .Because(result.StandardError);
        await Assert.That(health.RequestPaths).IsEquivalentTo(["/api/info"]);
    }

    [Test]
    [Timeout(120_000)]
    public async Task DiscoverAsync_Should_Fall_Back_To_The_Home_Directory(CancellationToken cancellationToken)
    {
        using var root = new TestRunRoot(FileSystem);
        await using var health = LoopbackHttpServer.Start(static _ => ReadyHealth());
        var home = FileSystem.Path.Combine(root.Path, "home");
        Seed(FileSystem.Path.Combine(home, ".local", "state", "opencode", "service.json"), health.Endpoint);
        // No XDG state root at all: the only way to the registration is the redirected home,
        // read through USERPROFILE on Windows and HOME elsewhere (the libuv rule the SDK follows).
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["XDG_STATE_HOME"] = null,
            ["XDG_CONFIG_HOME"] = null,
            ["OPENCODE_CONFIG_DIR"] = null,
            ["HOME"] = home,
            ["USERPROFILE"] = home,
        };

        var result = await new ServiceFixtureCommand(FileSystem)
            .RunAsync(["discover-default", ServiceTimingData.PatientRequestTimeoutMilliseconds], environment, cancellationToken);

        await Assert.That(result.ExitCode).IsEqualTo(0).Because(result.StandardError);
        await Assert.That(result.StandardOutput.Trim())
            .IsEqualTo(ServiceFixtureOutput.FoundLine(ServiceInfoBodyData.Pid, health.Endpoint))
            .Because(result.StandardError);
        await Assert.That(health.RequestPaths).IsEquivalentTo(["/api/info"]);
    }

    /// <summary>
    /// A registration whose daemon is gone (the port is closed) answers "missing" because the probe
    /// classified the refusal, without waiting for its bound. Every witness comes from the child, the
    /// real SDK and its probe transport: its verdict, under a bound so patient that only a probe
    /// that waits its bound out on a refused connect reports a timeout; its timeline's
    /// refused-connect event, the socket's own failure; and, on Windows, how long that connect took
    /// on the child's own clock. That interval stays under
    /// <see cref="ServiceTimingData.RefusalWithoutRetransmission"/> only when the transport disabled
    /// the SYN retransmissions that otherwise hold a refused loopback connect for about two seconds.
    /// It starts when the socket starts connecting, so the child's startup and any wait for a thread
    /// before the connect stay outside it. Linux and macOS refuse a closed loopback port within
    /// milliseconds with no option at all, so there the interval guards nothing and stays unchecked.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task DiscoverAsync_Should_Report_A_Stale_Registration_Without_Waiting_For_The_Bound(CancellationToken cancellationToken)
    {
        using var root = new TestRunRoot(FileSystem);
        Uri stale;
        await using (var gone = LoopbackHttpServer.Start(static _ => ReadyHealth()))
        {
            stale = gone.Endpoint;
        }

        var state = FileSystem.Path.Combine(root.Path, "state");
        Seed(FileSystem.Path.Combine(state, "opencode", "service.json"), stale);
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["XDG_STATE_HOME"] = state,
            ["XDG_CONFIG_HOME"] = FileSystem.Path.Combine(root.Path, "config"),
            ["OPENCODE_CONFIG_DIR"] = null,
        };

        var result = await new ServiceFixtureCommand(FileSystem)
            .RunAsync(["discover-default", ServiceTimingData.PatientRequestTimeoutMilliseconds], environment, cancellationToken);

        await Assert.That(result.ExitCode).IsEqualTo(0).Because(result.StandardError);
        await Assert.That(result.StandardOutput.Trim()).IsEqualTo("missing").Because(result.StandardError);
        await Assert.That(ServiceFixtureOutput.ProbeTimedOut(result.StandardError)).IsFalse()
            .Because(result.StandardError);
        await Assert.That(ServiceFixtureOutput.ConnectRefused(result.StandardError)).IsTrue()
            .Because(result.StandardError);
        if (OperatingSystem.IsWindows())
        {
            var refusal = ServiceFixtureOutput.ConnectRefusalTime(result.StandardError);
            await Assert.That(refusal).IsNotNull().Because(result.StandardError);
            await Assert.That(refusal!.Value).IsLessThan(ServiceTimingData.RefusalWithoutRetransmission)
                .Because(result.StandardError);
        }
    }

    private static LoopbackHttpResponse ReadyHealth() =>
        new() { StatusCode = HttpStatusCode.OK, Body = ServiceInfoBodyData.Ready, ContentType = "application/json" };

    /// <summary>Writes the registration the loopback daemon (pid 42, version 0.0.0-test) would have published.</summary>
    private static void Seed(string path, Uri endpoint)
    {
        var document = "{\"id\":\"isolated\",\"version\":\"" + ServiceInfoBodyData.Version + "\",\"url\":\"" + endpoint
            + "\",\"pid\":" + ServiceInfoBodyData.Pid.ToString(CultureInfo.InvariantCulture)
            + ",\"password\":\"" + IsolatedPassword + "\"}";
        _ = FileSystem.Directory.CreateDirectory(FileSystem.Path.GetDirectoryName(path)!);
        FileSystem.File.WriteAllText(path, document);
    }
}
