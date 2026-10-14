using OpenCode.Sdk.Tools.Generator;
using OpenCode.Sdk.Tools.Generator.Binding.Models;

namespace OpenCode.Sdk.Tools.Tests.Generator;

/// <summary>
/// The reach report lists every placed model with its owner and the families that reach it, in
/// ordinal order, and counts the models whose reach is anything but their owner alone; a placed
/// model no family reaches prints an empty list and counts.
/// </summary>
public sealed class ModelReachReportTests
{
    private const string Sessions = "Sessions";
    private const string Events = "Events";
    private const string Commands = "Commands";
    private const string SessionUnreached = "SessionUnreached";

    [Test]
    public async Task Compose_Should_List_Each_Placed_Model_With_Its_Owner_And_Reaching_Families_In_Ordinal_Order()
    {
        var report = ModelReachReport.Compose(Placement(), Reach());

        await Assert.That(report.Content).IsEqualTo(
            ModelReachReport.Header
            + "CommandNotFoundError owner=Commands reached-by=Sessions\n"
            + "SessionCreated owner=Sessions reached-by=Events,Sessions\n"
            + "SessionInfo owner=Sessions reached-by=Sessions\n");
    }

    [Test]
    public async Task Compose_Should_Count_The_Models_Reached_Beyond_Their_Owner()
    {
        var report = ModelReachReport.Compose(Placement(), Reach());

        await Assert.That(report.ReachedBeyondOwner).IsEqualTo(2);
    }

    [Test]
    public async Task Compose_Should_Leave_Out_A_Reached_Type_That_Has_No_Placement()
    {
        var reach = new Dictionary<string, IReadOnlyList<string>>(Reach(), StringComparer.Ordinal) { ["ExampleStray"] = [Sessions] };

        var report = ModelReachReport.Compose(Placement(), reach);

        await Assert.That(report.Content).DoesNotContain("ExampleStray");
    }

    [Test]
    public async Task Compose_Should_List_A_Placed_Model_With_An_Empty_Reach_And_Count_It_When_No_Family_Reaches_It()
    {
        var homes = Homes();
        homes[SessionUnreached] = TypeHome.Family(Sessions);

        var report = ModelReachReport.Compose(new ModelPlacement { Homes = homes }, Reach());

        await Assert.That(report.Content).Contains($"\n{SessionUnreached} owner={Sessions} reached-by=\n");
        await Assert.That(report.ReachedBeyondOwner).IsEqualTo(3);
    }

    private static ModelPlacement Placement() => new() { Homes = Homes() };

    private static Dictionary<string, TypeHome> Homes() =>
        new(StringComparer.Ordinal)
        {
            ["SessionInfo"] = TypeHome.Family(Sessions),
            ["SessionCreated"] = new() { Slice = Sessions, Area = "Lifecycle" },
            ["CommandNotFoundError"] = TypeHome.Family(Commands),
        };

    private static Dictionary<string, IReadOnlyList<string>> Reach() =>
        new(StringComparer.Ordinal)
        {
            ["SessionInfo"] = [Sessions],
            ["SessionCreated"] = [Events, Sessions],
            ["CommandNotFoundError"] = [Sessions],
        };
}
