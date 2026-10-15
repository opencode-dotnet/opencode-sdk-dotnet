namespace OpenCode.Sdk.Tools.Tests.CommentResidue;

/// <summary>One finding of the comment-residue gate: the repository-relative file, the 1-based line, the rule it breaks, and the text it matched.</summary>
internal sealed record CommentResidueHit(string File, int Line, string Rule, string Match)
{
    public override string ToString() => $"{File}:{Line.ToString(System.Globalization.CultureInfo.InvariantCulture)}: {Rule}: '{Match}'";
}
