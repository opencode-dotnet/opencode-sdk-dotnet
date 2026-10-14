namespace OpenCode.Sdk.Tools.TestRoster.Models;

/// <summary>One verified roster entry with the executed-test count its TRX reported.</summary>
internal sealed record TestRosterRow(string Module, int Executed);
