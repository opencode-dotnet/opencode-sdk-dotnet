using System.Text.Json.Serialization;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// The string-enum converter for every generated enum: wire values are strings by contract,
/// so a numeric token is a malformed body, not an undefined enum member. The parameterless
/// shape keeps the attribute reference AOT-safe.
/// </summary>
/// <typeparam name="TEnum">The generated enum type.</typeparam>
internal sealed class StrictJsonStringEnumConverter<TEnum> : JsonStringEnumConverter<TEnum>
    where TEnum : struct, Enum
{
    public StrictJsonStringEnumConverter()
        : base(namingPolicy: null, allowIntegerValues: false)
    {
    }
}
