using System.Collections.Frozen;
using OpenCode.Sdk.Models;

namespace OpenCode.Sdk.Internal;

/// <summary>The owned, ordinal selection captured when the public door is called.</summary>
internal sealed class EventSelection
{
    private readonly FrozenSet<string>? _types;

    private EventSelection(FrozenSet<string>? types) => _types = types;

    public static EventSelection Capture(IReadOnlyList<EventType>? types, string parameterName = "request") =>
        new(types is null || types.Count == 0 ? null : types.Select(value => CaptureValue(value, parameterName)).ToFrozenSet(StringComparer.Ordinal));

    public bool Includes(string type) =>
        _types is null || string.Equals(type, StreamReconnect.ConnectedEventType, StringComparison.Ordinal) || _types.Contains(type);

    private static string CaptureValue(EventType value, string parameterName) => value.Equals(default)
        ? throw new ArgumentException("A default EventType has no wire value.", parameterName)
        : value.ToString();
}
