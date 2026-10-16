using System.Text.Json.Serialization;

namespace OpenCode.Sdk.Models;

/// <summary>Marks one typed reason carried by a declared stream failure.</summary>
public interface IOpenCodeStreamFailureCause
{
    /// <summary>Gets the cause's wire tag.</summary>
    [JsonPropertyName("_tag")]
    public string Tag { get; }
}
