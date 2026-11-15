namespace OpenCode.Sdk.Tests.Support;

/// <summary>The explicitly named service-config doors, composed over their public read entry point.</summary>
internal sealed class ServiceConfigCancellationDoors(Func<CancellationToken, Task> read)
{
    public IReadOnlyList<ServiceConfigCancellationDoor> Rows { get; } =
        [new("service-config-read", [nameof(OpenCodeServiceConfiguration.ReadAsync)], read)];

    public string? RowFor(string name) => Rows.SingleOrDefault(door => door.MemberNames.Contains(name, StringComparer.Ordinal))?.Row;
}
