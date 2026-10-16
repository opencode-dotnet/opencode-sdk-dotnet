using System.Text;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Transport.Support;

internal sealed class RecordingResponseAdapter : ResponseAdapter<TestResponse>
{
    private readonly Func<int, string, TestResponse> _map;

    public RecordingResponseAdapter()
        : this(static (status, _) => new TestResponse
        {
            Status = status,
        })
    {
    }

    public RecordingResponseAdapter(Func<int, string, TestResponse> map)
    {
        ArgumentNullException.ThrowIfNull(map);

        _map = map;
    }

    public int? AdaptedStatus { get; private set; }

    public override StatusVerdict Classify(int status) => status switch
    {
        200 => StatusVerdict.Success,
        >= 200 and < 300 => StatusVerdict.UndeclaredSuccess,
        _ => StatusVerdict.UndeclaredError,
    };

    public string? AdaptedRawBody { get; private set; }

    public override TestResponse AdaptSuccess(int status, ReadOnlySpan<byte> utf8Body) =>
        Adapt(status, Encoding.UTF8.GetString(utf8Body.ToArray()));

    public override TestResponse Adapt(int status, string rawBody)
    {
        AdaptedStatus = status;
        AdaptedRawBody = rawBody;
        return _map(status, rawBody);
    }
}
