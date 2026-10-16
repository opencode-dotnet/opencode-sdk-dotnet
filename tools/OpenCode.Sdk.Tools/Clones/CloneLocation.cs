namespace OpenCode.Sdk.Tools.Clones;

/// <summary>
/// One clone a scope's baseline does not hold: its two fragments as repository-relative
/// <c>file:first-last</c> line ranges, and its size in tokens.
/// </summary>
internal sealed record CloneLocation(string First, string Second, int Tokens);
