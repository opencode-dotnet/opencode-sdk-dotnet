namespace OpenCode.Sdk.Tools.Tests.CommentResidue;

/// <summary>One line of text read from a file (a comment line, or an identifier), with its 1-based line number.</summary>
internal sealed record SourceLine(int Line, string Text);
