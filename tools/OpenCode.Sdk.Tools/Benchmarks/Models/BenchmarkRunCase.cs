namespace OpenCode.Sdk.Tools.Benchmarks.Models;

/// <summary>One benchmark case of a run, projected to the comparison's axes.</summary>
internal sealed record BenchmarkRunCase
{
    public required string FullName { get; init; }

    public required string Family { get; init; }

    public required string Method { get; init; }

    public required string Parameters { get; init; }

    public required string Runtime { get; init; }

    /// <summary>Whether BenchmarkDotNet exported the case without statistics: it failed and carries no measurement.</summary>
    public bool IsNa { get; init; }

    public required long? AllocatedBytes { get; init; }

    /// <summary>The case's median timing, or <see langword="null"/> when the run yielded no positive
    /// median — a constant-folded case measures at the noise floor and carries no usable timing.</summary>
    public required double? MedianNanoseconds { get; init; }

    /// <summary>Exact wire body bytes one operation consumes, or <see langword="null"/> when the
    /// case carries no wire fixture or the run predates the wire metrics.</summary>
    public required long? WireBytes { get; init; }

    /// <summary>Payloads or frames consumed per operation; <see langword="null"/> under the same
    /// conditions as <see cref="WireBytes"/>.</summary>
    public required long? WireItems { get; init; }

    /// <summary>JSON payload bytes per item, excluding envelope and framing; <see langword="null"/>
    /// under the same conditions as <see cref="WireBytes"/>.</summary>
    public required long? PayloadBytesPerItem { get; init; }

    public string CaseLabel => Parameters.Length == 0
        ? $"{Family}/{Method}"
        : $"{Family}/{Method} [{Parameters}]";

    /// <summary>Whether the run recorded any wire figure for this case; the fixture's figures
    /// travel as one triple, so a single probe decides which leg supplies them.</summary>
    public bool HasWireMetrics => WireBytes is not null || WireItems is not null || PayloadBytesPerItem is not null;
}
