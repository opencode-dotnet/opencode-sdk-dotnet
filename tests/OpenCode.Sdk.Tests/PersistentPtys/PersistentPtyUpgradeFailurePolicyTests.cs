using System.Globalization;
using System.Net.WebSockets;
using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk.Tests.PersistentPtys;

public sealed class PersistentPtyUpgradeFailurePolicyTests
{
    private const string PtyId = "pty_persistent_7";

    public static IEnumerable<Func<int>> AuthenticationStatuses() =>
    [
        static () => 401,
        static () => 403,
    ];

    [Test]
    public async Task Map_Should_Name_The_Connect_Query_On_A_400()
    {
        var fault = new WebSocketException("upgrade refused");

        var mapped = PersistentPtyUpgradeFailurePolicy.Instance.Map(fault, 400, PtyId);

        await Assert.That(mapped.Message).Contains(PtyId);
        await Assert.That(mapped.Message).Contains("400");
        await Assert.That(mapped.Message).Contains("the cursor must be a safe integer at or above zero");
        await Assert.That(mapped.InnerException).IsSameReferenceAs(fault);
    }

    [Test]
    [MethodDataSource(nameof(AuthenticationStatuses))]
    public async Task Map_Should_Name_The_Auth_Cause_On_A_Refused_Upgrade(int status)
    {
        var fault = new WebSocketException("upgrade refused");

        var mapped = PersistentPtyUpgradeFailurePolicy.Instance.Map(fault, status, PtyId);

        await Assert.That(mapped.Message).Contains(status.ToString(CultureInfo.InvariantCulture));
        await Assert.That(mapped.Message).Contains("credential or origin");
        await Assert.That(mapped.InnerException).IsSameReferenceAs(fault);
    }

    /// <summary>
    /// The absent 404 arm is a protocol decision, not an omission: this family upgrades before it
    /// checks that the terminal exists, so a missing terminal closes 4404 after the upgrade
    /// (<see cref="PersistentPtyClosePolicy"/> owns that wording). A 404 reaching here is an
    /// ordinary unexpected status and must not claim the terminal does not exist.
    /// </summary>
    [Test]
    public async Task Map_Should_Report_A_404_As_An_Unexpected_Status_Rather_Than_A_Missing_Terminal()
    {
        var fault = new WebSocketException("upgrade refused");

        var mapped = PersistentPtyUpgradeFailurePolicy.Instance.Map(fault, 404, PtyId);

        await Assert.That(mapped.Message).Contains("404");
        await Assert.That(mapped.Message).Contains("instead of completing the protocol upgrade");
        await Assert.That(mapped.Message).DoesNotContain("does not exist", StringComparison.Ordinal);
        await Assert.That(mapped.InnerException).IsSameReferenceAs(fault);
    }

    [Test]
    public async Task Map_Should_Report_Any_Other_Status_As_A_Failed_Upgrade()
    {
        var fault = new WebSocketException("upgrade refused");

        var mapped = PersistentPtyUpgradeFailurePolicy.Instance.Map(fault, 500, PtyId);

        await Assert.That(mapped.Message).Contains("500");
        await Assert.That(mapped.Message).Contains("instead of completing the protocol upgrade");
        await Assert.That(mapped.InnerException).IsSameReferenceAs(fault);
    }

    [Test]
    public async Task Map_Should_Report_An_Unavailable_Status_With_Connect_Context()
    {
        var fault = new WebSocketException("upgrade refused");

        var mapped = PersistentPtyUpgradeFailurePolicy.Instance.Map(fault, status: null, PtyId);

        await Assert.That(mapped.Message).Contains(PtyId);
        await Assert.That(mapped.Message).Contains("before the connection was established");
        await Assert.That(mapped.InnerException).IsSameReferenceAs(fault);
    }

    [Test]
    public async Task Map_Should_Refuse_A_Null_Exception()
    {
        _ = Assert.Throws<ArgumentNullException>(
            () => _ = PersistentPtyUpgradeFailurePolicy.Instance.Map(null!, 400, PtyId));

        await Task.CompletedTask;
    }
}
