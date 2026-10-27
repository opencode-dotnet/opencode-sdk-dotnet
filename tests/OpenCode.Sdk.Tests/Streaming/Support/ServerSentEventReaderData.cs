namespace OpenCode.Sdk.Tests.Streaming.Support;

internal static class ServerSentEventReaderData
{
    public const string MalformedUtf8Message = "The opencode event stream contains malformed UTF-8.";
    public const string SizeLimitMessage = "The opencode event stream produced a frame beyond the size this client accepts.";
    public const string TruncationMessage = "The opencode event stream ended in the middle of an event.";
    public const string KeptFrame = "data: kept\n\n";
    public const string KeptPayload = "kept";
    public const string FirstPayload = "first";
    public const string SecondPayload = "second";
    public const string JsonFirstPayload = "{\"a\":1}";
    public const string JsonSecondPayload = "{\"a\":2}";
    public const string JoinedFirstAndSecond = "first\nsecond";
    public const string EmptyThenSecond = "\nsecond";
    public const string DataPrefix = "data: ";
    public const string EventPrefix = "event: ";
    public const string CommentPrefix = ": ";
    public const string UnknownPrefix = "unknown: ";
    public const string FirstFrame = "data: first\n\n";
    public const string AFrame = "data: a\n\n";
    public const string ACarriageReturnLine = "data: a\r";
    public const string LeadingBomAFrame = "\uFEFFdata: a\n\n";
    public const string ByteOrderMark = "\uFEFF";
    public const string APayload = "a";
    public const string BPayload = "b";
    public const string JoinedAB = "a\nb";
    public const string TurkishCharacter = "ş";
    public const string EuroCharacter = "€";
    public const string EmojiCharacter = "😀";
    public const string PaddedPayload = " padded";
    public const string EventXLine = "event: x\n";
    public const string EventXEmptyFrame = "event: x\n\n\n";
    public const string UnfinishedData = "data: cut";
    public const string FrameDelimiter = "\n\n";
}
