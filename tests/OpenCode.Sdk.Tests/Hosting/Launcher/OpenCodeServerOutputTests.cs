using System.Globalization;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Hosting.Launcher;

/// <summary>
/// The collector's retention contract in isolation: both bounds, the sticky truncation flags,
/// snapshot stability, single binding, and the closed state. Real-process delivery is proven
/// by <see cref="OpenCodeServerLifecycleTests"/>.
/// </summary>
public sealed class OpenCodeServerOutputTests
{
    [Test]
    public async Task GetSnapshot_Should_Be_Empty_And_Untruncated_Before_Any_Output()
    {
        var output = new OpenCodeServerOutput();

        var snapshot = output.GetSnapshot();

        await Assert.That(snapshot.StandardOutput).IsEmpty();
        await Assert.That(snapshot.StandardError).IsEmpty();
        await Assert.That(snapshot.StandardOutputTruncated).IsFalse();
        await Assert.That(snapshot.StandardErrorTruncated).IsFalse();
    }

    [Test]
    public async Task GetSnapshot_Should_Preserve_Order_And_Empty_Lines_Per_Stream()
    {
        var output = new OpenCodeServerOutput();
        output.AppendStandardOutput("{\"url\":\"http://127.0.0.1:1\"}");
        output.AppendStandardOutput("");
        output.AppendStandardError("warn");
        output.AppendStandardOutput("later");

        var snapshot = output.GetSnapshot();

        await Assert.That(snapshot.StandardOutput).IsEquivalentTo(["{\"url\":\"http://127.0.0.1:1\"}", "", "later"], CollectionOrdering.Matching);
        await Assert.That(snapshot.StandardError).IsEquivalentTo(["warn"], CollectionOrdering.Matching);
        await Assert.That(snapshot.StandardOutputTruncated).IsFalse();
        await Assert.That(snapshot.StandardErrorTruncated).IsFalse();
    }

    [Test]
    public async Task Append_Should_Evict_The_Oldest_Line_Beyond_The_Line_Bound_And_Flag_That_Stream_Only()
    {
        var output = new OpenCodeServerOutput();
        for (var index = 0; index <= OpenCodeServerOutput.RetainedLines; index++)
        {
            output.AppendStandardOutput("line-" + index.ToString(CultureInfo.InvariantCulture));
        }

        output.AppendStandardError("only");

        var snapshot = output.GetSnapshot();

        await Assert.That(snapshot.StandardOutput.Count).IsEqualTo(OpenCodeServerOutput.RetainedLines);
        await Assert.That(snapshot.StandardOutput[0]).IsEqualTo("line-1");
        await Assert.That(snapshot.StandardOutput[^1]).IsEqualTo("line-" + OpenCodeServerOutput.RetainedLines.ToString(CultureInfo.InvariantCulture));
        await Assert.That(snapshot.StandardOutputTruncated).IsTrue();
        await Assert.That(snapshot.StandardErrorTruncated).IsFalse();
    }

    [Test]
    public async Task Append_Should_Evict_Oldest_Lines_To_Satisfy_The_Character_Bound()
    {
        var output = new OpenCodeServerOutput();
        var half = new string('a', (OpenCodeServerOutput.RetainedCharacters / 2) + 1);
        output.AppendStandardError(half);
        output.AppendStandardError("middle");
        output.AppendStandardError(half);

        var snapshot = output.GetSnapshot();

        // The first half-plus line has to go for the second to fit; "middle" survives.
        await Assert.That(snapshot.StandardError).IsEquivalentTo(["middle", half], CollectionOrdering.Matching);
        await Assert.That(snapshot.StandardErrorTruncated).IsTrue();
        await Assert.That(snapshot.StandardOutputTruncated).IsFalse();
    }

    [Test]
    public async Task Append_Should_Keep_The_Suffix_Of_An_Oversized_Line()
    {
        var output = new OpenCodeServerOutput();
        var oversized = new string('x', 8) + new string('y', OpenCodeServerOutput.RetainedCharacters);
        output.AppendStandardOutput(oversized);

        var snapshot = output.GetSnapshot();

        await Assert.That(snapshot.StandardOutput.Count).IsEqualTo(1);
        await Assert.That(snapshot.StandardOutput[0].Length).IsEqualTo(OpenCodeServerOutput.RetainedCharacters);
        await Assert.That(snapshot.StandardOutput[0]).IsEqualTo(new string('y', OpenCodeServerOutput.RetainedCharacters));
        await Assert.That(snapshot.StandardOutputTruncated).IsTrue();
    }

    [Test]
    public async Task Append_Should_Count_An_Evicted_Empty_Line_As_Truncation()
    {
        var output = new OpenCodeServerOutput();
        for (var index = 0; index <= OpenCodeServerOutput.RetainedLines; index++)
        {
            output.AppendStandardError("");
        }

        var snapshot = output.GetSnapshot();

        await Assert.That(snapshot.StandardError.Count).IsEqualTo(OpenCodeServerOutput.RetainedLines);
        await Assert.That(snapshot.StandardErrorTruncated).IsTrue();
    }

    [Test]
    public async Task GetSnapshot_Should_Stay_Stable_When_Output_Keeps_Arriving()
    {
        var output = new OpenCodeServerOutput();
        output.AppendStandardOutput("first");

        var earlier = output.GetSnapshot();
        output.AppendStandardOutput("second");
        var later = output.GetSnapshot();

        await Assert.That(earlier.StandardOutput).IsEquivalentTo(["first"], CollectionOrdering.Matching);
        await Assert.That(later.StandardOutput).IsEquivalentTo(["first", "second"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task TryBind_Should_Succeed_Exactly_Once()
    {
        var output = new OpenCodeServerOutput();

        await Assert.That(output.TryBind()).IsTrue();
        await Assert.That(output.TryBind()).IsFalse();
    }

    [Test]
    public async Task Append_Should_Be_Ignored_Once_The_Collection_Is_Complete()
    {
        var output = new OpenCodeServerOutput();
        output.AppendStandardOutput("kept");
        output.AppendStandardError("kept");
        output.Complete();
        output.AppendStandardOutput("late");
        output.AppendStandardError("late");

        var snapshot = output.GetSnapshot();

        await Assert.That(snapshot.StandardOutput).IsEquivalentTo(["kept"], CollectionOrdering.Matching);
        await Assert.That(snapshot.StandardError).IsEquivalentTo(["kept"], CollectionOrdering.Matching);
    }
}
