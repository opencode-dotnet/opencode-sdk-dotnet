using System.Text.Json;

namespace OpenCode.Sdk.Tests.Serialization.Support;

/// <summary>One dictionary entry a fixture carries: its key, the kind of its value, and the value's exact wire text.</summary>
public readonly record struct ExpectedJsonValue(string Key, JsonValueKind Kind, string RawText)
{
    public override string ToString() => Key;
}
