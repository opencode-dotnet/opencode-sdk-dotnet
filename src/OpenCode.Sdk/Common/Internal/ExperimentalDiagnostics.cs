namespace OpenCode.Sdk.Internal;

/// <summary>
/// The diagnostic the hand-written persistent-PTY doors carry, the same one the generator gives
/// the family's generated members; a test holds the two equal. The link opens the guide's section
/// for the id, which says why the family is experimental and how to opt in.
/// </summary>
internal static class ExperimentalDiagnostics
{
    public const string PersistentPty = "OPENCODE002";

    // The published guide's address is the value the attribute carries, not a resource the SDK
    // reaches; the attribute needs it as a constant. The contract wins over S1075.
#pragma warning disable S1075
    public const string PersistentPtyUrl =
        "https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/experimental.md#opencode002";
#pragma warning restore S1075
}
