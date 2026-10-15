using System.Globalization;
using System.Net.WebSockets;
using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk.Tests.Ptys;

public sealed class PtyUpgradeFailurePolicyTests
{
    private const string PtyId = "pty_100";

    public static IEnumerable<Func<int>> AuthenticationStatuses() =>
    [
        static () => 401,
        static () => 403,
    ];

    [Test]
    public async Task Map_Should_Name_The_Missing_Pty_On_A_404()
    {
        var fault = new WebSocketException("upgrade refused");

        var mapped = PtyUpgradeFailurePolicy.Instance.Map(fault, 404, PtyId);

        await Assert.That(mapped.Message).Contains(PtyId);
        await Assert.That(mapped.Message).Contains("404");
        await Assert.That(mapped.InnerException).IsSameReferenceAs(fault);
    }

    [Test]
    [MethodDataSource(nameof(AuthenticationStatuses))]
    public async Task Map_Should_Name_The_Auth_Cause_On_A_Refused_Upgrade(int status)
    {
        var fault = new WebSocketException("upgrade refused");

        var mapped = PtyUpgradeFailurePolicy.Instance.Map(fault, status, PtyId);

        await Assert.That(mapped.Message).Contains(status.ToString(CultureInfo.InvariantCulture));
        await Assert.That(mapped.Message).Contains("credential");
        await Assert.That(mapped.InnerException).IsSameReferenceAs(fault);
    }

    [Test]
    public async Task Map_Should_Report_Any_Other_Status_As_A_Failed_Upgrade()
    {
        var fault = new WebSocketException("upgrade refused");

        var mapped = PtyUpgradeFailurePolicy.Instance.Map(fault, 500, PtyId);

        await Assert.That(mapped.Message).Contains("500");
        await Assert.That(mapped.InnerException).IsSameReferenceAs(fault);
    }

    [Test]
    public async Task Map_Should_Report_An_Unavailable_Status_With_Connect_Context()
    {
        var fault = new WebSocketException("upgrade refused");

        var mapped = PtyUpgradeFailurePolicy.Instance.Map(fault, status: null, PtyId);

        await Assert.That(mapped.Message).Contains(PtyId);
        await Assert.That(mapped.InnerException).IsSameReferenceAs(fault);
    }
}
