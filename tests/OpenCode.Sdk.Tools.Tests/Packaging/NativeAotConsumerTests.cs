using System.Text.RegularExpressions;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tools.Generator.Refresh;
using Testably.Abstractions;

namespace OpenCode.Sdk.Tools.Tests.Packaging;

/// <summary>
/// The packed packages publish a native AOT consumer cleanly, and the published consumer runs:
/// one real consumer of both packages, compiled by ILC, proves the trim and AOT annotations the
/// packages carry — the configure-action registration, the source-generated serialization — hold
/// for the shape a native consumer actually builds. The publish loads every core of the machine
/// for about a minute, so the class carries the <see cref="TestCategories.NativeAot"/> category
/// and runs in a pass of its own, away from the timing-sensitive launcher tests; the three tests
/// share one publish through the session-shared fixture. No skip: a machine without the native
/// toolchain fails with the compiler's own message.
/// </summary>
[Category(TestCategories.NativeAot)]
[ClassDataSource<PackedConsumerFeed>(Shared = SharedType.PerTestSession)]
public sealed partial class NativeAotConsumerTests(PackedConsumerFeed feed)
{
    private static readonly RealFileSystem FileSystem = new();

    private readonly CliWrapProcessRunner _runner = new();

    [Test]
    [Timeout(600_000)]
    public async Task Publishing_A_Native_Aot_Consumer_Should_Exit_Zero(CancellationToken cancellationToken)
    {
        var publish = await feed.PublishConsumerAsync(cancellationToken);

        await Assert.That(publish.ExitCode).IsEqualTo(0).Because(publish.Output);
    }

    [Test]
    [Timeout(600_000)]
    public async Task Publishing_A_Native_Aot_Consumer_Should_Report_No_Trimming_Or_Aot_Warnings(CancellationToken cancellationToken)
    {
        var publish = await feed.PublishConsumerAsync(cancellationToken);
        var warnings = AotWarningCodes()
            .Matches(publish.Output)
            .Select(static match => match.Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        await Assert.That(warnings).IsEmpty().Because("the publish reported " + string.Join(", ", warnings));
    }

    [Test]
    [Timeout(600_000)]
    public async Task The_Published_Native_Aot_Consumer_Should_Print_Its_Marker_And_Exit_Zero(CancellationToken cancellationToken)
    {
        var publish = await feed.PublishConsumerAsync(cancellationToken);
        await Assert.That(publish.Executable).Satisfies(FileSystem.File.Exists)
            .Because("the publish produced no native executable:\n" + publish.Output);

        var run = await _runner.RunAsync(
            publish.Executable,
            [],
            FileSystem.Path.GetDirectoryName(publish.Executable) ?? publish.Executable,
            cancellationToken);

        await Assert.That(run.ExitCode).IsEqualTo(0).Because(run.StandardError + run.StandardOutputText);
        await Assert.That(run.StandardOutputText.Replace("\r\n", "\n", StringComparison.Ordinal))
            .IsEqualTo(AotConsumerData.Marker + "\n")
            .Because("standard output was: " + run.StandardOutputText);
    }

    /// <summary>The warning codes the trim analyzer (<c>IL2xxx</c>) and the AOT compiler (<c>IL3xxx</c>) emit.</summary>
    [GeneratedRegex(@"\bIL[23]\d{3}\b", RegexOptions.None, Timeout.Infinite)]
    private static partial Regex AotWarningCodes();
}
