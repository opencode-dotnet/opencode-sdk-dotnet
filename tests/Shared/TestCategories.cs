namespace OpenCode.Sdk.TestSupport;

/// <summary>TUnit <c>[Category]</c> names the documented test passes select on.</summary>
internal static class TestCategories
{
    /// <summary>
    /// Background-service elections. Each ten-caller election starts some twenty to thirty-four
    /// source-run servers, which use every CPU of a runner for ten to twenty seconds, and a test
    /// in another test host that ran in those seconds missed its bound — on the three-vCPU macOS
    /// runner and on Windows alike, every failure lined up with an election in another host and
    /// no run without that overlap failed. TUnit's keys order tests inside one host only, so the
    /// documented gate runs these classes in a pass of their own, after every other test
    /// (<c>docs/engineering/quality-gates.md</c>).
    /// </summary>
    public const string ServiceElection = "ServiceElection";

    /// <summary>
    /// The packed-consumer native AOT publish. Its ILC compile loads every core of the machine for
    /// about a minute, which the timing-sensitive launcher tests cannot run beside, so the
    /// documented gate runs these tests in a pass of their own, after every other test
    /// (<c>docs/engineering/quality-gates.md</c>).
    /// </summary>
    public const string NativeAot = "NativeAot";
}
