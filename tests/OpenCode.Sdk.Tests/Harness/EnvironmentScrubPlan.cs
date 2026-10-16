namespace OpenCode.Sdk.Tests.Harness;

/// <summary>
/// What <see cref="InheritedEnvironment"/> decided for one environment: the variable names to
/// remove, and the <c>NO_PROXY</c> value to set when a proxy variable is present (null otherwise).
/// Names only, never values.
/// </summary>
/// <param name="Removed">The names the scrub removes.</param>
/// <param name="NoProxy">The complete <c>NO_PROXY</c> value to set, or null to leave it as it is.</param>
public sealed record EnvironmentScrubPlan(IReadOnlyList<string> Removed, string? NoProxy);
