using System.Diagnostics;
using System.Globalization;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.TestSupport;

namespace OpenCode.Sdk.Tests.Support;

/// <summary>
/// The session-start scrub of the test host's inherited environment: which names it removes,
/// which it keeps, what it does to <c>NO_PROXY</c>, and — through a real child process — that a
/// hazard seeded in this process is gone from what a fixture's child would inherit.
/// </summary>
public sealed class InheritedEnvironmentTests
{
    /// <summary>How long an ended child may take to exit.</summary>
    private static readonly TimeSpan ChildExitBound = TimeSpan.FromSeconds(15);

    [Test]
    public async Task Plan_Should_Remove_Every_Pinned_Server_Variable_But_Keep_The_Suite_Knobs()
    {
        var plan = InheritedEnvironment.Plan(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["OPENCODE_SIMULATE"] = "1",
            ["OPENCODE_API_KEY"] = "secret",
            ["opencode_pty_bin"] = "/elsewhere/opencode-pty",
            ["OPENCODE_SDK_TESTS_KEEP_LOGS"] = "1",
            ["OPENCODE_SDK_TESTS_SERVER_COMMAND"] = "opencode|serve",
            ["PATH"] = "/usr/bin",
            ["HOME"] = "/home/dev",
        });

        string[] expected = ["OPENCODE_API_KEY", "OPENCODE_SIMULATE", "opencode_pty_bin"];
        await Assert.That(plan.Removed.OrderBy(static name => name, StringComparer.Ordinal)).IsEquivalentTo(expected);
        await Assert.That(plan.NoProxy).IsNull();
    }

    [Test]
    public async Task Plan_Should_Remove_The_Git_Variables_That_Redirect_A_Repository()
    {
        var plan = InheritedEnvironment.Plan(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GIT_DIR"] = "/other/.git",
            ["GIT_WORK_TREE"] = "/other",
            ["GIT_INDEX_FILE"] = "/other/.git/index",
            ["GIT_CEILING_DIRECTORIES"] = "/",
            ["GIT_AUTHOR_NAME"] = "dev",
        });

        string[] expected = ["GIT_CEILING_DIRECTORIES", "GIT_DIR", "GIT_INDEX_FILE", "GIT_WORK_TREE"];
        await Assert.That(plan.Removed.OrderBy(static name => name, StringComparer.Ordinal)).IsEquivalentTo(expected);
    }

    [Test]
    [Arguments("HTTP_PROXY")]
    [Arguments("https_proxy")]
    [Arguments("ALL_PROXY")]
    public async Task Plan_Should_Name_Loopback_In_NoProxy_When_A_Proxy_Is_Set(string proxyVariable)
    {
        var plan = InheritedEnvironment.Plan(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [proxyVariable] = "http://proxy.example:3128",
        });

        await Assert.That(plan.NoProxy).IsEqualTo("127.0.0.1,localhost,::1");
        await Assert.That(plan.Removed).IsEmpty();
    }

    [Test]
    public async Task Plan_Should_Extend_An_Existing_NoProxy_Without_Duplicates()
    {
        var plan = InheritedEnvironment.Plan(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["HTTP_PROXY"] = "http://proxy.example:3128",
            ["NO_PROXY"] = "localhost, .example.internal",
        });

        await Assert.That(plan.NoProxy).IsEqualTo("localhost, .example.internal,127.0.0.1,::1");
    }

    [Test]
    public async Task Plan_Should_Leave_NoProxy_Alone_When_No_Proxy_Is_Set()
    {
        var plan = InheritedEnvironment.Plan(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["NO_PROXY"] = "corp.internal",
        });

        await Assert.That(plan.NoProxy).IsNull();
    }

    [Test]
    public async Task Plan_Should_Remove_Provider_Credentials_And_The_Aws_Chain()
    {
        var plan = InheritedEnvironment.Plan(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["anthropic_api_key"] = "secret",
                ["OPENAI_API_KEY"] = "secret",
                ["AWS_PROFILE"] = "dev",
                ["AWS_SESSION_TOKEN"] = "secret",
                ["PATH"] = "/usr/bin",
                ["DOTNET_ROOT"] = "/usr/share/dotnet",
            },
            [],
            ["ANTHROPIC_API_KEY", "OPENAI_API_KEY"]);

        string[] expected = ["AWS_PROFILE", "AWS_SESSION_TOKEN", "OPENAI_API_KEY", "anthropic_api_key"];
        await Assert.That(plan.Removed.OrderBy(static name => name, StringComparer.Ordinal)).IsEquivalentTo(expected);
    }

    [Test]
    public async Task Plan_Should_Remove_Every_Telemetry_Variable()
    {
        var plan = InheritedEnvironment.Plan(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "https://collector.example",
            ["OTEL_EXPORTER_OTLP_HEADERS"] = "authorization=secret",
            ["otel_resource_attributes"] = "service.name=dev",
            ["OTELX"] = "kept",
            ["PATH"] = "/usr/bin",
        });

        string[] expected = ["OTEL_EXPORTER_OTLP_ENDPOINT", "OTEL_EXPORTER_OTLP_HEADERS", "otel_resource_attributes"];
        await Assert.That(plan.Removed.OrderBy(static name => name, StringComparer.Ordinal)).IsEquivalentTo(expected);
    }

    [Test]
    public async Task Plan_Should_Keep_The_Variables_The_Suite_Set_Itself()
    {
        var plan = InheritedEnvironment.Plan(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["OPENCODE_DB"] = "/runs/session/data/opencode.db",
                ["OPENCODE_CONFIG_DIR"] = "/runs/session/config/opencode",
                ["OPENCODE_SIMULATE"] = "1",
            },
            ["OPENCODE_DB", "OPENCODE_CONFIG_DIR"],
            []);

        string[] expected = ["OPENCODE_SIMULATE"];
        await Assert.That(plan.Removed).IsEquivalentTo(expected);
    }

    /// <summary>
    /// What a child that only inherits gets from the session: every root the pinned server
    /// resolves, under the session's own root, and none of the variables the scrub removes.
    /// </summary>
    [Test]
    public async Task Session_Should_Hand_Every_Child_The_Isolated_Roots()
    {
        var root = SessionIsolation.Root;

        await Assert.That(root).IsNotNull();
        foreach (var name in new[] { "XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_STATE_HOME", "XDG_CACHE_HOME", "OPENCODE_CONFIG_DIR", "OPENCODE_DB", "OPENCODE_TEST_HOME" })
        {
            await Assert.That(Environment.GetEnvironmentVariable(name)).StartsWith(root!);
            await Assert.That(SessionIsolation.AppliedNames).Contains(name);
        }

        await Assert.That(Environment.GetEnvironmentVariable("OPENCODE_CONFIG_CONTENT")).IsEqualTo("{}");
        await Assert.That(Environment.GetEnvironmentVariable("OPENCODE_DISABLE_MODELS_FETCH")).IsEqualTo("1");
        await Assert.That(Environment.GetEnvironmentVariable(ServerIsolation.ProjectConfigurationSwitch)).IsEqualTo("1");
        await Assert.That(SessionIsolation.AppliedNames).Contains(ServerIsolation.ProjectConfigurationSwitch);
        foreach (var name in new[] { "HOME", "USERPROFILE", "TMPDIR", "TMP", "TEMP" })
        {
            // The test host's own home and temp directory, which it and every tool it starts read.
            await Assert.That(SessionIsolation.AppliedNames).DoesNotContain(name);
        }
    }

    /// <summary>
    /// What a child that only inherits really receives: a real child process reads every isolated
    /// root this session set, each under the session's own root.
    /// </summary>
    [Test]
    [NotInParallel]
    [Timeout(60_000)]
    public async Task Session_Should_Reach_A_Child_That_Only_Inherits(CancellationToken cancellationToken)
    {
        var root = SessionIsolation.Root;

        await Assert.That(root).IsNotNull();
        foreach (var name in new[] { "XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_STATE_HOME", "XDG_CACHE_HOME", "OPENCODE_CONFIG_DIR", "OPENCODE_DB", "OPENCODE_TEST_HOME" })
        {
            await Assert.That(await ChildSeesAsync(name, cancellationToken)).StartsWith(root!);
        }

        await Assert.That((await ChildSeesAsync("OPENCODE_CONFIG_CONTENT", cancellationToken)).TrimEnd()).IsEqualTo("{}");
        await Assert.That((await ChildSeesAsync(ServerIsolation.ProjectConfigurationSwitch, cancellationToken)).TrimEnd()).IsEqualTo("1");
    }

    [Test]
    public async Task RequireScrubbed_Should_Refuse_A_Server_Before_The_Scrub_Has_Run()
    {
        var refusal = await Assert.That(() => InheritedEnvironment.RequireScrubbed(InheritedEnvironment.CredentialScrub.NotRun, null))
            .Throws<InvalidOperationException>();

        await Assert.That(refusal!.Message).Contains("scrubbed");
    }

    [Test]
    public async Task RequireScrubbed_Should_Refuse_With_The_Reason_When_The_Credentials_Could_Not_Be_Named()
    {
        const string reason = "The pinned checkout is missing.";

        var refusal = await Assert.That(() => InheritedEnvironment.RequireScrubbed(InheritedEnvironment.CredentialScrub.Unavailable, reason))
            .Throws<InvalidOperationException>();

        await Assert.That(refusal!.Message).IsEqualTo(reason);
    }

    [Test]
    public async Task RequireScrubbed_Should_Accept_A_Completed_Scrub()
    {
        await Assert.That(() => InheritedEnvironment.RequireScrubbed(InheritedEnvironment.CredentialScrub.Scrubbed, null)).ThrowsNothing();
    }

    /// <summary>This session's own scrub ran before its first test, so a server may start.</summary>
    [Test]
    public async Task RequireProviderCredentialsScrubbed_Should_Accept_This_Session()
    {
        await Assert.That(InheritedEnvironment.RequireProviderCredentialsScrubbed).ThrowsNothing();
    }

    /// <summary>
    /// The mechanism end to end: a hazard seeded in this process reaches a child (the control),
    /// and after the scrub it does not, while a suite knob survives. Process-wide state, so alone.
    /// </summary>
    [Test]
    [NotInParallel]
    [Timeout(60_000)]
    public async Task ScrubProcess_Should_Remove_A_Seeded_Hazard_From_What_A_Child_Inherits(CancellationToken cancellationToken)
    {
        const string hazard = "OPENCODE_SIMULATE";
        const string knob = "OPENCODE_SDK_TESTS_SCRUB_PROBE";
        var value = "scrub-probe-" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(hazard, value);
        Environment.SetEnvironmentVariable(knob, value);
        try
        {
            await Assert.That(await ChildSeesAsync(hazard, cancellationToken)).Contains(value);

            var plan = await InheritedEnvironment.ScrubProcessAsync(cancellationToken);

            await Assert.That(plan.Removed).Contains(hazard);
            await Assert.That(plan.Removed).DoesNotContain(knob);
            await Assert.That(Environment.GetEnvironmentVariable(hazard)).IsNull();
            await Assert.That(Environment.GetEnvironmentVariable(knob)).IsEqualTo(value);
            await Assert.That(await ChildSeesAsync(hazard, cancellationToken)).DoesNotContain(value);
        }
        finally
        {
            Environment.SetEnvironmentVariable(hazard, null);
            Environment.SetEnvironmentVariable(knob, null);
        }
    }

    /// <summary>
    /// A child still running when its test is cancelled is ended, and has exited by the time the
    /// cancellation reaches the test. The child reads its input, which nothing ever closes, so it
    /// runs until it is ended.
    /// </summary>
    [Test]
    [Timeout(60_000)]
    public async Task ReadToExitAsync_Should_End_A_Running_Child_When_Cancelled(CancellationToken cancellationToken)
    {
        var startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe") { Arguments = "/d /c set /p line=" }
            : new ProcessStartInfo("/bin/sh") { Arguments = "-c \"read line\"" };
        startInfo.RedirectStandardInput = true;
        using var process = Start(startInfo);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await cancellation.CancelOnWorkerAsync();
        try
        {
            await Assert.That(() => (Task)ReadToExitAsync(process, cancellation.Token)).Throws<OperationCanceledException>();

            await Assert.That(process.HasExited).IsTrue();
        }
        finally
        {
            if (!process.HasExited)
            {
                _ = TestProcessTreeKill.TryKill(process);
            }
        }
    }

    /// <summary>Echoes one variable from a child shell, the way a fixture's server would read it.</summary>
    private static async Task<string> ChildSeesAsync(string name, CancellationToken cancellationToken)
    {
        var startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe") { Arguments = "/d /c echo %" + name + "%" }
            : new ProcessStartInfo("/bin/sh") { Arguments = "-c \"printf %s \\\"$" + name + "\\\"\"" };
        using var process = Start(startInfo);
        return await ReadToExitAsync(process, cancellationToken);
    }

    private static Process Start(ProcessStartInfo startInfo)
    {
        startInfo.UseShellExecute = false;
        startInfo.RedirectStandardOutput = true;
        startInfo.CreateNoWindow = true;
        return Process.Start(startInfo) ?? throw new InvalidOperationException("The child shell did not start.");
    }

    /// <summary>
    /// Reads a child's output to its exit. A cancelled read ends the child and waits for it to
    /// exit before the cancellation reaches the test, so no child outlives the test that started it.
    /// </summary>
    private static async Task<string> ReadToExitAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return output;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ = TestProcessTreeKill.TryKill(process);
            if (!await ProcessObservation.ObserveExitWithinAsync(process, ChildExitBound, CancellationToken.None))
            {
                throw new InvalidOperationException(
                    "The child shell " + process.Id.ToString(CultureInfo.InvariantCulture) + " was ended but did not exit within "
                    + ChildExitBound.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " seconds.");
            }

            throw;
        }
    }
}
