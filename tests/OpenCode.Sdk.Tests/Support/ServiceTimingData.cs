using System.Globalization;
using OpenCode.Sdk.Internal.BackgroundService.Ensure;

namespace OpenCode.Sdk.Tests.Support;

/// <summary>
/// The probe bound of every background-service test about the probe's answer rather than its
/// bound. A loaded runner can miss the pinned two-second request timeout on a loopback exchange,
/// and the probe counts a failure as timed out whenever the bound has expired by the time the
/// failure reaches it, so a starved process reported a timeout where a state or a refusal was
/// expected. Thirty seconds is far past any answer or refusal a starved process still observes,
/// and far inside a test's own two-minute timeout, so a probe that waits its bound out still
/// reports a timeout before the test is cut off.
/// </summary>
internal static class ServiceTimingData
{
    public static readonly TimeSpan PatientRequestTimeout = TimeSpan.FromSeconds(30);

    public static readonly ServiceTiming Patient = ServiceTiming.Default with { RequestTimeout = PatientRequestTimeout };

    /// <summary>The patient bound as the isolated discovery executable reads its argument: whole milliseconds, invariant culture.</summary>
    public static readonly string PatientRequestTimeoutMilliseconds =
        ((long)PatientRequestTimeout.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The longest a refused loopback connect may take on Windows, measured from the connect's own
    /// start. Without the probe transport's loopback option the kernel reports the refusal only
    /// after its SYN retransmissions, about 2.03 to 2.09 seconds; with it the refusal comes in
    /// milliseconds. Load only lengthens the interval, so no load can carry a connect without the
    /// option under this line.
    /// </summary>
    public static readonly TimeSpan RefusalWithoutRetransmission = TimeSpan.FromMilliseconds(1500);
}
