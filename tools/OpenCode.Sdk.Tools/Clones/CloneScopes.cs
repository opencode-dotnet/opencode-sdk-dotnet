namespace OpenCode.Sdk.Tools.Clones;

/// <summary>
/// The clone gate's fixed scopes. Hand-written SDK code and the tool are gated against committed
/// baselines, the tool also against a 3% ceiling; tests and generated output are reported only,
/// because a test's arrange and an emitter's template repeat on purpose.
/// </summary>
internal static class CloneScopes
{
    /// <summary>The folder of the pinned clone detector and its baselines, relative to the repository root.</summary>
    public const string DetectorFolder = "tools/jscpd";

    /// <summary>Gets every scope, in the order the gate runs them.</summary>
    public static IReadOnlyList<CloneScope> All { get; } =
    [
        new("src", "src", CloneScopeFilter.Generated, [], DetectorFolder + "/src.baseline.json", Threshold: null),
        new("tools", "tools", CloneScopeFilter.None, [DetectorFolder + "/**"], DetectorFolder + "/tools.baseline.json", Threshold: 3),
        new("tests", "tests", CloneScopeFilter.None, [], Baseline: null, Threshold: null),
        new("generated", "src", CloneScopeFilter.HandWritten, [], Baseline: null, Threshold: null),
    ];
}
