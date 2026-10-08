using System.Globalization;
using Testably.Abstractions;

namespace OpenCode.Sdk.TestSupport;

/// <summary>
/// The test host's own environment, scrubbed once per test session before any fixture starts a
/// child. <c>ServerIsolation</c> sets the variables a server must read from the fixture; this
/// hook removes the ones a developer's shell could add on top — every <c>OPENCODE_*</c> variable
/// the pinned server reads except the suite's own <c>OPENCODE_SDK_TESTS_*</c> knobs, every
/// <c>OTEL_*</c> variable (an exporter endpoint and its headers would send a server's telemetry,
/// and the headers' credentials, to the developer's collector), every provider credential and
/// credential switch <see cref="ProviderCredentialVariables"/> names, the whole <c>AWS_*</c> chain,
/// and the git variables that would point a test workspace's repository somewhere else — and
/// names loopback in <c>NO_PROXY</c> when a proxy variable is present, so neither the test clients
/// nor the bun server route <c>127.0.0.1</c> through it. It then applies
/// <see cref="SessionIsolation"/>, so a child that only inherits takes opencode's own roots from
/// the session. Names are reported, never values: a value can be a secret. Nothing is removed on CI, whose environment carries none of these. The mutation is
/// process-wide, like <c>PathCommandShim</c>'s PATH, which is why it happens once, first.
/// </summary>
public static class InheritedEnvironment
{
    private const string HazardPrefix = "OPENCODE_";
    private const string SuitePrefix = "OPENCODE_SDK_TESTS_";
    private const string TelemetryPrefix = "OTEL_";
    private const string NoProxyVariable = "NO_PROXY";

    /// <summary>The git variables that address another repository, index, or search ceiling from the environment.</summary>
    private static readonly string[] GitHazards = ["GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_CEILING_DIRECTORIES"];

    /// <summary>The variables .NET's environment proxy and bun's fetch read, either spelling.</summary>
    private static readonly string[] ProxyVariables = ["HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY"];

    /// <summary>What a loopback bypass must name: the address the pinned daemon registers, its name, and IPv6.</summary>
    private static readonly string[] LoopbackEntries = ["127.0.0.1", "localhost", "::1"];

    private static readonly RealFileSystem FileSystem = new();

    /// <summary>Why the last scrub could not name the provider credentials, or null when it could.</summary>
    private static string? _credentialsUnavailable;

    [Before(TestSession)]
    public static async Task ScrubTestSessionAsync(CancellationToken cancellationToken)
    {
        var plan = await ScrubProcessAsync(cancellationToken).ConfigureAwait(false);
        if (plan.Removed.Count > 0)
        {
            Console.WriteLine("Inherited variables removed for this test session: " + string.Join(", ", plan.Removed));
        }

        if (_credentialsUnavailable is { } reason)
        {
            // Not a session failure: a run that starts no server inherits nothing a provider reads.
            Console.WriteLine(reason);
        }

        SessionIsolation.Apply();
        Console.WriteLine("Every child of this test session inherits isolated opencode roots under: " + SessionIsolation.Root);

        if (plan.NoProxy is { } noProxy)
        {
            // The bypass list is configuration, not a secret; the proxy address itself stays unprinted.
            Console.WriteLine("A proxy variable is set; " + NoProxyVariable + " for this test session is: " + noProxy);
        }
    }

    /// <summary>
    /// Plans and applies the scrub on this process; returns what it did. What
    /// <see cref="SessionIsolation"/> has set is the suite's own, and stays.
    /// </summary>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>What the scrub removed and set.</returns>
    public static async Task<EnvironmentScrubPlan> ScrubProcessAsync(CancellationToken cancellationToken)
    {
        var credentials = await ProviderCredentialVariables.FromPinnedSourceAsync(FileSystem, cancellationToken).ConfigureAwait(false);
        var plan = Plan(Snapshot(), SessionIsolation.AppliedNames, credentials.Names);
        Apply(plan);
        _credentialsUnavailable = credentials.Unavailable;
        return plan;
    }

    /// <summary>
    /// Refuses to let a server start when the scrub could not name the provider credentials. Only
    /// a server reads them, so the session itself runs without the pinned checkout, and a server
    /// start is where a missing name would leak.
    /// </summary>
    /// <exception cref="InvalidOperationException">The pinned checkout that names them is missing.</exception>
    public static void RequireProviderCredentialsScrubbed()
    {
        if (_credentialsUnavailable is { } reason)
        {
            throw new InvalidOperationException(reason);
        }
    }

    /// <summary>Plans the scrub over a snapshot, with nothing set by the suite and no catalog credentials.</summary>
    public static EnvironmentScrubPlan Plan(IReadOnlyDictionary<string, string> environment) => Plan(environment, [], []);

    /// <summary>Plans the scrub over a snapshot, so the rule is testable without touching the process.</summary>
    /// <param name="environment">The variables as the process has them.</param>
    /// <param name="suiteVariables">Names the suite itself set, which are never hazards.</param>
    /// <param name="credentialVariables">Names a provider takes a credential from.</param>
    /// <returns>What the scrub removes and sets.</returns>
    public static EnvironmentScrubPlan Plan(
        IReadOnlyDictionary<string, string> environment,
        IEnumerable<string> suiteVariables,
        IEnumerable<string> credentialVariables)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(suiteVariables);
        ArgumentNullException.ThrowIfNull(credentialVariables);

        // Variable names are case-insensitive on Windows and a shell can export any spelling, so
        // the rule reads names without regard to case and removes the spelling that is there.
        var suite = new HashSet<string>(suiteVariables, StringComparer.OrdinalIgnoreCase);
        var credentials = new HashSet<string>(credentialVariables, StringComparer.OrdinalIgnoreCase);
        var removed = new List<string>();
        foreach (var name in environment.Keys)
        {
            if (!suite.Contains(name) && (IsHazard(name) || IsCredential(name, credentials)))
            {
                removed.Add(name);
            }
        }

        return new EnvironmentScrubPlan(removed, PlanNoProxy(environment));
    }

    private static bool IsHazard(string name) =>
        (name.StartsWith(HazardPrefix, StringComparison.OrdinalIgnoreCase)
            && !name.StartsWith(SuitePrefix, StringComparison.OrdinalIgnoreCase))
        || name.StartsWith(TelemetryPrefix, StringComparison.OrdinalIgnoreCase)
        || Array.Exists(GitHazards, hazard => string.Equals(hazard, name, StringComparison.OrdinalIgnoreCase));

    private static bool IsCredential(string name, HashSet<string> credentials) =>
        credentials.Contains(name) || ProviderCredentialVariables.IsAwsChain(name);

    /// <summary>
    /// With a proxy variable present, the complete <c>NO_PROXY</c> to set: the existing entries as
    /// they are, then whichever loopback names they lack. Null when no proxy is set, so a plain
    /// <c>NO_PROXY</c> is never touched.
    /// </summary>
    private static string? PlanNoProxy(IReadOnlyDictionary<string, string> environment)
    {
        var proxied = false;
        foreach (var name in environment.Keys)
        {
            if (Array.Exists(ProxyVariables, proxy => string.Equals(proxy, name, StringComparison.OrdinalIgnoreCase)))
            {
                proxied = true;
                break;
            }
        }

        if (!proxied)
        {
            return null;
        }

        var existing = string.Empty;
        foreach (var pair in environment)
        {
            if (string.Equals(pair.Key, NoProxyVariable, StringComparison.OrdinalIgnoreCase))
            {
                existing = pair.Value;
                break;
            }
        }

        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in existing.Split(','))
        {
            _ = present.Add(entry.Trim());
        }

        var value = existing;
        foreach (var loopback in LoopbackEntries)
        {
            if (present.Add(loopback))
            {
                value = value.Length is 0 ? loopback : value + "," + loopback;
            }
        }

        return value;
    }

    private static void Apply(EnvironmentScrubPlan plan)
    {
        foreach (var name in plan.Removed)
        {
            Environment.SetEnvironmentVariable(name, null);
        }

        if (plan.NoProxy is { } noProxy)
        {
            Environment.SetEnvironmentVariable(NoProxyVariable, noProxy);
            if (!OperatingSystem.IsWindows())
            {
                // Names are case-sensitive here and bun's fetch reads the lowercase spelling first.
                Environment.SetEnvironmentVariable(NoProxyVariable.ToLower(CultureInfo.InvariantCulture), noProxy);
            }
        }
    }

    private static Dictionary<string, string> Snapshot()
    {
        var snapshot = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name && entry.Value is string value)
            {
                snapshot[name] = value;
            }
        }

        return snapshot;
    }
}
