namespace OpenCode.Sdk.Tools.Generator.Refresh;

/// <summary>The three answers a predicate-target probe can give for a component or operation parameter.</summary>
internal enum KeywordPresence
{
    /// <summary>The target exists and lacks the required keyword or style — the repair is still needed.</summary>
    Lacks = 0,

    /// <summary>The target exists and carries the required keyword or style — the repair has landed.</summary>
    Carries = 1,

    /// <summary>The target is absent from the document — the patch needs human review.</summary>
    ComponentMissing = 2,
}
