using System.Runtime.CompilerServices;

namespace OpenCode.Sdk.Internal;

internal sealed class StreamPlane(PipelineExchange exchange, IEventStreamFramer framer,
    ClientLifetime lifetime, ConnectionSnapshot connection)
{
    private const string EventStreamMediaType = "text/event-stream";

    public async IAsyncEnumerable<TPayload> ExecuteStreamCoreAsync<TPayload, TCause>(OperationRequest request,
        IStreamAdapter<TPayload, TCause> adapter, StreamAttempt? attempt,
        [EnumeratorCancellation] CancellationToken cancellationToken)
        where TCause : IReadOnlyList<Models.IOpenCodeStreamFailureCause>
    {
        lifetime.ThrowIfDisposed();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        using var message = exchange.CreateMessage(request, bufferBody: false,
            attempt is null ? request.Options?.NetworkTimeout : Timeout.InfiniteTimeSpan, cancellationToken);
        await SendStreamThroughPoliciesAsync(message, attempt).ConfigureAwait(false);
        var response = message.Response!;
        var status = (int)response.StatusCode;
        switch (adapter.Classify(status))
        {
            case StatusVerdict.Success:
                break;
            case StatusVerdict.UndeclaredSuccess:
                throw StatusVerdictFailures.UndeclaredSuccess(status);
            case StatusVerdict.DeclaredError:
            case StatusVerdict.UndeclaredError:
                var rawBody = ResponseMaterializer.ReadErrorBody(message);
                throw OpenCodeErrorReader.CreateApiException(
                    status, adapter.ReadError(status, rawBody), rawBody, connection.Authorization is not null);
            default:
                // A stream contract has no no-content success; any other verdict is a
                // generator defect, refused rather than guessed around.
                throw new InvalidOperationException("The stream adapter produced a verdict its contract cannot declare.");
        }

        // A success that is not an event stream cannot be framed, so it fails as a
        // protocol error rather than being read as one frame of garbage. Media types are
        // case-insensitive, so a proxy that rewrites the casing still matches.
        if (!string.Equals(response.Content?.Headers.ContentType?.MediaType, EventStreamMediaType, StringComparison.OrdinalIgnoreCase))
        {
            throw FailureClassification.EventStreamBodyRequired();
        }

        // A yield suspends the ownership chain; cancellation must release the response even
        // while no read is running and the consumer has not asked for another frame.
        // API-shape divergence: a registration has no DisposeAsync downlevel.
#if NET
        await using var cancellation = RegisterStreamCancellation(response, linked.Token).ConfigureAwait(false);
#else
        using var cancellation = RegisterStreamCancellation(response, linked.Token);
#endif

        // The body is read through a view that reports each read bringing bytes to the reconnect
        // loop's idle window; disposing the view disposes the body a moment before the message would.
        // API-shape divergence, not algorithm: a stream has no DisposeAsync downlevel.
        var body = new ActivityStream(await ReadBodyStreamAsync(message, linked.Token).ConfigureAwait(false), attempt is null ? null : attempt.OnActivity);
#if NET
        await using var observation = body.ConfigureAwait(false);
#else
        using var observation = body;
#endif
        var frames = framer.ReadAsync(body, linked.Token).GetAsyncEnumerator(linked.Token);
        await using var enumeration = frames.ConfigureAwait(false);
        while (true)
        {
            // A yield cannot sit inside a try with a catch, so the read is guarded on its
            // own: a connection dying mid-stream is a transport failure here exactly as it
            // is on the one-shot path.
            try
            {
                var moved = await frames.MoveNextAsync().ConfigureAwait(false);
                if (!moved)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    break;
                }
            }
            catch (Exception exception) when (FailureClassification.Handles(exception, FailurePhase.EventStreamRead))
            {
                throw FailureClassification.Map(exception, FailurePhase.EventStreamRead, cancellationToken, lifetime.Token);
            }
            catch (Exception exception) when (linked.IsCancellationRequested)
            {
                throw FailureClassification.MapFault(exception, FailurePhase.EventStreamRead, cancellationToken, lifetime.Token);
            }

            attempt?.OnFrame(frames.Current);
            yield return FrameDispatch.ReadPayload(frames.Current, adapter);
        }
    }

    private async ValueTask SendStreamThroughPoliciesAsync(PipelineMessage message, StreamAttempt? attempt)
    {
        try
        {
            await exchange.SendThroughPoliciesAsync(message).ConfigureAwait(false);
        }
        finally
        {
            if (message.Response is not null)
            {
                attempt?.OnResponse();
            }
        }
    }

    private static async Task<Stream> ReadBodyStreamAsync(PipelineMessage message, CancellationToken streamToken)
    {
        try
        {
            return await message.Response!.Content.ReadAsStreamAsync(streamToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (FailureClassification.Handles(exception, FailurePhase.EventStreamRead))
        {
            throw FailureClassification.Map(exception, FailurePhase.EventStreamRead, message.CancellationToken, message.LifetimeToken);
        }
        catch (Exception exception) when (streamToken.IsCancellationRequested)
        {
            throw FailureClassification.MapFault(exception, FailurePhase.EventStreamRead, message.CancellationToken, message.LifetimeToken);
        }
    }

    private static CancellationTokenRegistration RegisterStreamCancellation(
        HttpResponseMessage response, CancellationToken cancellationToken) =>
        cancellationToken.UnsafeRegister(
            // The registration always supplies its non-null response as state.
            static state => ((HttpResponseMessage)state!).Dispose(),
            response);
}
