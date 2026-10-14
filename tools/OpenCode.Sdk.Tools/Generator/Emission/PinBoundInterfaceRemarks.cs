namespace OpenCode.Sdk.Tools.Generator.Emission;

/// <summary>
/// The remark every generated union and hoisted interface carries: it is a contract to consume,
/// not a seam to implement, because its members follow the pinned document and move with it.
/// </summary>
internal static class PinBoundInterfaceRemarks
{
    public static IReadOnlyList<DocumentationRun> Runs { get; } =
    [
        DocumentationRun.Prose(
            "Consume this interface; do not implement it outside tests. Its members follow the pinned protocol document and change when the SDK moves to a newer pin, which a minor version may do."),
    ];
}
