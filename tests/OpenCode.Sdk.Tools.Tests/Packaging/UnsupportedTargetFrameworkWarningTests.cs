using System.Reflection;
using OpenCode.Sdk.Tests.Harness;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tools.Tests.Packaging;

/// <summary>
/// The packed packages warn a consumer whose target framework resolves to the netstandard2.0
/// compatibility asset on a runtime the packages do not target, and stay silent for every target
/// they ship. NuGet's nearest-match asset selection is the subject, so the proof is a real
/// consumer of the real package. The Extensions package depends on the core package, so its
/// consumer meets both packages' warnings: each package states its own support, as the runtime's
/// own packages do.
/// </summary>
[Category(TestCategories.Packaging)]
[ClassDataSource<PackedConsumerFeed>(Shared = SharedType.PerTestSession)]
public sealed class UnsupportedTargetFrameworkWarningTests(PackedConsumerFeed feed)
{
    private const string Core = "OpenCodeDotNet.Sdk";

    private const string Extensions = "OpenCodeDotNet.Sdk.Extensions";

    /// <summary>
    /// A target framework the packages no longer ship, which falls back to the netstandard2.0
    /// asset; the test project names it, and fetches its reference pack, in its own restore.
    /// </summary>
    private static readonly string Unsupported = typeof(UnsupportedTargetFrameworkWarningTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(static attribute => attribute.Key == "PackedConsumerUnsupportedTargetFramework")
        .Value ?? throw new InvalidOperationException("The test project names no unsupported target framework.");

    [Test]
    [Timeout(300_000)]
    public async Task A_Core_Consumer_On_An_Unsupported_Runtime_Should_Get_The_Core_Warning(CancellationToken cancellationToken)
    {
        var build = await feed.BuildConsumerAsync(Core, Unsupported, suppress: false, cancellationToken);

        await Assert.That(build.ExitCode).IsEqualTo(0).Because(build.Output);
        // Diagnostic membership and multiplicity matter, not report enumeration order.
        await Assert.That(WarnedPackages(build)).IsEquivalentTo([Core], CollectionOrdering.Any).Because(build.Output);
    }

    [Test]
    [Timeout(300_000)]
    public async Task An_Extensions_Consumer_On_An_Unsupported_Runtime_Should_Get_One_Warning_Per_Package(CancellationToken cancellationToken)
    {
        var build = await feed.BuildConsumerAsync(Extensions, Unsupported, suppress: false, cancellationToken);

        await Assert.That(build.ExitCode).IsEqualTo(0).Because(build.Output);
        // Diagnostic membership and multiplicity matter, not report enumeration order.
        await Assert.That(WarnedPackages(build)).IsEquivalentTo([Core, Extensions], CollectionOrdering.Any).Because(build.Output);
    }

    [Test]
    [Timeout(300_000)]
    [Arguments(Core)]
    [Arguments(Extensions)]
    public async Task A_Consumer_That_Suppresses_The_Warning_Should_Get_None(string packageId, CancellationToken cancellationToken)
    {
        var build = await feed.BuildConsumerAsync(packageId, Unsupported, suppress: true, cancellationToken);

        await Assert.That(build.ExitCode).IsEqualTo(0).Because(build.Output);
        await Assert.That(WarnedPackages(build)).IsEmpty().Because(build.Output);
    }

    /// <summary>The shipped targets: the two supported .NET releases and .NET Framework 4.7.2.</summary>
    [Test]
    [Timeout(300_000)]
    [Arguments(Core, "net472")]
    [Arguments(Core, "net10.0")]
    [Arguments(Core, "net11.0")]
    [Arguments(Extensions, "net472")]
    [Arguments(Extensions, "net10.0")]
    [Arguments(Extensions, "net11.0")]
    public async Task A_Consumer_On_A_Shipped_Target_Should_Get_No_Warning(string packageId, string targetFramework, CancellationToken cancellationToken)
    {
        var build = await feed.BuildConsumerAsync(packageId, targetFramework, suppress: false, cancellationToken);

        await Assert.That(build.ExitCode).IsEqualTo(0).Because(build.Output);
        await Assert.That(WarnedPackages(build)).IsEmpty().Because(build.Output);
    }

    /// <summary>
    /// The package ids the build warned about, one entry per warning: a warning line reads
    /// "warning : &lt;package&gt; &lt;version&gt; does not support &lt;target&gt; ...". MSBuild repeats each
    /// warning in its closing summary, word for word, so identical lines count once.
    /// </summary>
    private List<string> WarnedPackages(ConsumerBuild build)
    {
        const string marker = "warning : ";
        var warned = new List<string>();
        foreach (var line in build.Output.Split('\n').Select(static line => line.Trim()).Distinct(StringComparer.Ordinal))
        {
            var start = line.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0)
            {
                continue;
            }

            var words = line[(start + marker.Length)..].Split(' ', 4);
            if (words.Length == 4 && words[1] == feed.Version && words[2] == "does" && words[3].StartsWith("not support", StringComparison.Ordinal))
            {
                warned.Add(words[0]);
            }
        }

        return warned;
    }
}
