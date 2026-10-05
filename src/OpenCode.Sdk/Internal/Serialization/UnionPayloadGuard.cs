using System.Text.Json;

namespace OpenCode.Sdk.Internal.Serialization;

/// <summary>Prevents a hand-constructed unknown carrier from replaying a payload with different markers.</summary>
internal sealed class UnionPayloadGuard
{
    private readonly JsonValueKind _falseToken = JsonValueKind.False;
    private readonly JsonValueKind _stringToken = JsonValueKind.String;
    private readonly JsonValueKind _trueToken = JsonValueKind.True;

    public static UnionPayloadGuard Instance { get; } = new();

    private UnionPayloadGuard()
    {
    }

    public void RequireString(JsonElement payload, string propertyName, string expected)
    {
        if (payload.ValueKind is not JsonValueKind.Object
            || !payload.TryGetProperty(propertyName, out var marker)
            || marker.ValueKind != _stringToken
            || !marker.ValueEquals(expected))
        {
            throw Mismatch(propertyName, nameof(payload));
        }
    }

    public void RequireBoolean(JsonElement payload, string propertyName, bool expected)
    {
        if (payload.ValueKind is not JsonValueKind.Object
            || !payload.TryGetProperty(propertyName, out var marker)
            || marker.ValueKind != (expected ? _trueToken : _falseToken))
        {
            throw Mismatch(propertyName, nameof(payload));
        }
    }

    private static ArgumentException Mismatch(string propertyName, string parameterName) =>
        new($"The payload must contain a '{propertyName}' marker that agrees with the constructor marker.", parameterName);
}
