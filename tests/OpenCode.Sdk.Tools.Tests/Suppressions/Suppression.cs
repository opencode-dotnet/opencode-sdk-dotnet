namespace OpenCode.Sdk.Tools.Tests.Suppressions;

/// <summary>
/// One suppression as the ledger counts it: its channel, the repository-relative file that declares
/// it, the scope it reaches beyond that file (the section glob of an analyzer-config entry, or the
/// file a slopwatch baseline entry names; empty for every other channel), the rule ID or setting key
/// it lowers, and the value it sets (empty when the channel carries none).
/// </summary>
internal sealed record Suppression(SuppressionChannel Channel, string Origin, string Scope, string Rule, string Value);
