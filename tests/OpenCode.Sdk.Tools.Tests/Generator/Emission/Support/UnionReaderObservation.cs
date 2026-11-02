using System.Text.Json;

namespace OpenCode.Sdk.Tools.Tests.Generator.Emission.Support;

internal sealed record UnionReaderObservation(
    string Phase, string? Type, byte[] Bytes, string? ExceptionType, string? Path, string? Message,
    long? Line, long? BytePosition, long Consumed, JsonTokenType Token, int Depth, long TokenStart, long SequenceOffset, int? Sentinel);
