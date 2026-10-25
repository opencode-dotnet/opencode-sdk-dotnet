using System.Text.Json.Serialization.Metadata;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;

namespace OpenCode.Sdk.Tests.Support;

/// <summary>A stream contract over the test payload, reading errors against declared tags.</summary>
internal sealed class TestStreamAdapter : IStreamAdapter<TestBody, TestStreamFailureCause[]>
{
    /// <summary>The name the pinned contract gives a mid-stream failure frame.</summary>
    public const string StreamFailureEventName = "effect/http-api/stream/failure";

    private readonly string[]? _allowedTags;

    public TestStreamAdapter(params string[] allowedTags)
    {
        _allowedTags = allowedTags.Length is 0 ? null : allowedTags;
    }

    public StatusVerdict Classify(int status) => status switch
    {
        200 => StatusVerdict.Success,
        >= 200 and < 300 => StatusVerdict.UndeclaredSuccess,
        _ => StatusVerdict.UndeclaredError,
    };

    public string FailureEventName => StreamFailureEventName;

    public JsonTypeInfo<TestBody> PayloadTypeInfo => TestBodyJsonContext.Default.TestBody;

    public JsonTypeInfo? UnknownPayloadTypeInfo => null;

    public JsonTypeInfo<TestStreamFailureCause[]> CauseTypeInfo => TestBodyJsonContext.Default.TestStreamFailureCauseArray;

    public IOpenCodeError? ReadError(int status, string rawBody) => OpenCodeErrorReader.Read(rawBody, _allowedTags);
}
