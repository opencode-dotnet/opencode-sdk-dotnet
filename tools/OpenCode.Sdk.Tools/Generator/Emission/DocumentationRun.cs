namespace OpenCode.Sdk.Tools.Generator.Emission;

/// <summary>
/// One run of documentation text: prose, code rendered in <c>&lt;c&gt;</c>, or a
/// <c>&lt;see cref&gt;</c> link. The emitter escapes every run itself, so no caller hands it markup.
/// </summary>
internal sealed record DocumentationRun(string Text, bool IsCode, string? Cref = null)
{
    public static DocumentationRun Prose(string text) => new(text, IsCode: false);

    public static DocumentationRun Code(string text) => new(text, IsCode: true);

    public static DocumentationRun Reference(string cref) => new(string.Empty, IsCode: false, cref);
}
