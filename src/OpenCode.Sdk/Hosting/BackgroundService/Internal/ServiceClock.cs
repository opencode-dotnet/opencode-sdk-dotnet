namespace OpenCode.Sdk.Internal;

/// <summary>The production <see cref="IServiceClock"/> over <see cref="DateTimeOffset.UtcNow"/>.</summary>
internal sealed class ServiceClock : IServiceClock
{
    /// <inheritdoc />
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
