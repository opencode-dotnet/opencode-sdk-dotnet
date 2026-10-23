using Testably.Abstractions.Testing;

namespace OpenCode.Sdk.Tests.Harness;

/// <summary>
/// The session summary's file is a report, not a check: written where CI keeps it, and an I/O
/// failure is reported instead of failing the session.
/// </summary>
public sealed class SamplingGapSummaryFileTests
{
    [Test]
    public async Task WriteAsync_Should_Write_The_Summary_Named_By_The_Host_Pid()
    {
        var fileSystem = new MockFileSystem();
        var directory = fileSystem.Path.GetFullPath("results");
        using var error = new StringWriter();

        var written = await SamplingGapSummaryFile.WriteAsync(fileSystem, directory, 1234, "sampling-gap summary: count 0", error);

        await Assert.That(written).IsTrue();
        var path = fileSystem.Path.Combine(directory, "sampling-gap-summary-1234.txt");
#if NET
        var content = await fileSystem.File.ReadAllTextAsync(path);
#else
        var content = fileSystem.File.ReadAllText(path);
#endif
        await Assert.That(content).IsEqualTo("sampling-gap summary: count 0");
        await Assert.That(error.ToString()).IsEmpty();
    }

    /// <summary>A results path taken by a file cannot become a directory; the failure is reported and the call returns.</summary>
    [Test]
    public async Task WriteAsync_Should_Report_An_Io_Failure_Instead_Of_Throwing()
    {
        var fileSystem = new MockFileSystem();
        var directory = fileSystem.Path.GetFullPath("results");
#if NET
        await fileSystem.File.WriteAllTextAsync(directory, "a file where the results directory should be");
#else
        fileSystem.File.WriteAllText(directory, "a file where the results directory should be");
#endif
        using var error = new StringWriter();

        var written = await SamplingGapSummaryFile.WriteAsync(fileSystem, directory, 1234, "sampling-gap summary: count 0", error);

        await Assert.That(written).IsFalse();
        await Assert.That(error.ToString()).StartsWith("sampling-gap: could not write the session summary to '");
    }
}
