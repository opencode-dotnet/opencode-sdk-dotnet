namespace OpenCode.Sdk.Tools.Clones;

/// <summary>
/// The clone gate's fixed scopes. Hand-written SDK code, the tool and the tests are gated against
/// committed baselines, the tool also against a 3% ceiling and the tests against a 5.5% ceiling that
/// only falls; generated output is reported only, because an emitter's template repeats on purpose.
/// </summary>
internal static class CloneScopes
{
    /// <summary>The folder of the pinned clone detector and its baselines, relative to the repository root.</summary>
    public const string DetectorFolder = "tools/jscpd";

    /// <summary>Gets every scope, in the order the gate runs them.</summary>
    public static IReadOnlyList<CloneScope> All { get; } =
    [
        new("src", "src", CloneScopeFilter.Generated, [], DetectorFolder + "/src.baseline.json", Threshold: null),
        new("tools", "tools", CloneScopeFilter.None, [DetectorFolder + "/**"], DetectorFolder + "/tools.baseline.json", Threshold: 3m),
        new("tests", "tests", CloneScopeFilter.None, [], DetectorFolder + "/tests.baseline.json", Threshold: 5.5m),
        new("generated", "src", CloneScopeFilter.HandWritten, [], Baseline: null, Threshold: null),
    ];
}
