using OpenCode.Sdk.Models;

namespace OpenCode.Sdk.Internal;

/// <summary>Opens the generated route and adapter directly on the stream plane for one runner-owned attempt.</summary>
internal sealed class EventAttemptSource(Pipeline pipeline) : IEventAttemptSource
{
    public IAsyncEnumerable<IEvent> OpenAsync(StreamAttempt attempt, CancellationToken cancellationToken)
    {
        pipeline.Lifetime.ThrowIfDisposed();
        // The operation id and method are hand-copied from the generated Events operation; route and adapter remain generated.
        var request = OperationRequest.Describe<object>("event.subscribe", HttpMethod.Get, OpenCodeRoutes.Events.Subscribe(),
            body: null, bodyTypeInfo: null, content: null, options: null, declaredHeaders: null);
        return pipeline.StreamPlane.ExecuteStreamCoreAsync(request, EventSubscribeResponseStreamAdapter.Instance, attempt, cancellationToken);
    }
}
