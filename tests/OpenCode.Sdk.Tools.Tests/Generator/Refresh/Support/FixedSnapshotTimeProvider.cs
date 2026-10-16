namespace OpenCode.Sdk.Tools.Tests.Generator.Refresh.Support;

/// <summary>The provider's offset date is February 4; its UTC date is February 3.</summary>
internal sealed class FixedSnapshotTimeProvider : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => new(2031, 2, 4, 0, 15, 0, TimeSpan.FromHours(2));
}
