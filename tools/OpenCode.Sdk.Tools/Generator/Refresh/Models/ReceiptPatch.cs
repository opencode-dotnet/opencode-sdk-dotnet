using System.Text.Json.Serialization;

namespace OpenCode.Sdk.Tools.Generator.Refresh.Models;

/// <summary>One applied Restore patch as the receipt observed it.</summary>
internal sealed record ReceiptPatch
{
    /// <summary>Gets the manifest file name under <c>spec/patches/</c>.</summary>
    [JsonPropertyName("manifest")] public required string Manifest { get; init; }

    /// <summary>Gets the SHA-256 of the applied patch file.</summary>
    [JsonPropertyName("patchSha256")] public required string PatchSha256 { get; init; }

    /// <summary>Gets the hashes of every touched file as they stood before the patch applied.</summary>
    [JsonPropertyName("preimages")]
    public required IReadOnlyList<ReceiptPreimage> Preimages
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    }
}
