using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk.Tests.Common;

public sealed class CursorPaginatorTests
{
    [Test]
    public async Task EnumerateAsync_Should_Pass_The_Options_To_The_First_And_Every_Continuation_Request()
    {
        var received = new List<OpenCodeRequestOptions?>();
        var options = new OpenCodeRequestOptions
        {
            Location = new LocationSelector { Directory = "/per-call" },
            NetworkTimeout = TimeSpan.FromSeconds(3),
        };
        var sequence = CursorPaginator.EnumerateAsync(
            (_, requestOptions, _) =>
            {
                received.Add(requestOptions);
                return Task.FromResult(new SessionMessageListResponse
                {
                    Status = 200,
                    Messages = [],
                    Cursor = new ListCursor { Next = received.Count < 3 ? "next" : null },
                });
            },
            new SessionMessageListRequest(), SessionMessageListResponseAdapter.Instance, options, CancellationToken.None);

        var pageCount = 0;
        await foreach (var _ in sequence.Pages.WithCancellation(CancellationToken.None))
        {
            pageCount++;
        }

        await Assert.That(pageCount).IsEqualTo(3);
        await Assert.That(received.Count).IsEqualTo(3);
        await Assert.That(received[0]).IsSameReferenceAs(options);
        await Assert.That(received[1]).IsSameReferenceAs(options);
        await Assert.That(received[2]).IsSameReferenceAs(options);
    }

    [Test]
    public async Task EnumerateAsync_Should_Refuse_NoThrow_At_The_Call()
    {
        var exception = await RefusePaginatorOptionsAsync<ArgumentException>(OpenCodeRequestOptions.NoThrow);

        await Assert.That(exception.GetType()).IsEqualTo(typeof(ArgumentException));
        await Assert.That(exception.ParamName).IsEqualTo("requestOptions");
    }

    [Test]
    public async Task EnumerateAsync_Should_Refuse_An_Undefined_ErrorBehavior_At_The_Call()
    {
        var options = new OpenCodeRequestOptions { ErrorBehavior = (ErrorBehavior)42 };

        var exception = await RefusePaginatorOptionsAsync<ArgumentOutOfRangeException>(options);

        await Assert.That(exception.ParamName).IsEqualTo("requestOptions");
        await Assert.That(exception.ActualValue).IsEqualTo(options.ErrorBehavior);
    }

    private static async Task<TException> RefusePaginatorOptionsAsync<TException>(OpenCodeRequestOptions options)
        where TException : ArgumentException
    {
        var requests = 0;

        var exception = Assert.Throws<TException>(() => _ = CursorPaginator.EnumerateAsync(
            (_, _, _) =>
            {
                requests++;
                return Task.FromResult(new SessionMessageListResponse { Status = 200, Messages = [], Cursor = new ListCursor() });
            },
            new SessionMessageListRequest(), SessionMessageListResponseAdapter.Instance, options, CancellationToken.None));

        await Assert.That(requests).IsEqualTo(0);
        return exception;
    }
}
