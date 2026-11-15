using OpenCode.Sdk.Models;

namespace OpenCode.Sdk.Internal;

/// <summary>Probes the explicit server under a total bound on the probe pipeline's own clock.</summary>
internal sealed class ExplicitServerProbe(HttpClient? transport = null, TimeProvider? time = null)
{
    public async Task<ServerInfo> GetInfoAsync(Uri endpoint, string? password, CancellationToken cancellationToken)
    {
        var options = new OpenCodeClientOptions { Endpoint = endpoint, Password = password };
        using var pipeline = transport is null
            ? Pipeline.Create(options, time)
            : new Pipeline(transport, ownsHttpClient: false, options, time: time);
        using var bound = TimedCancellation.Create(TimeSpan.FromSeconds(5), pipeline.Connection.Time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, bound.Token);
        try
        {
            var response = await new ServerClient(pipeline).GetInfoAsync(cancellationToken: linked.Token).ConfigureAwait(false);
            return response.ServerInfo;
        }
        catch (Exception failure) when (failure is OpenCodeApiException or OpenCodeTransportException or OperationCanceledException)
        {
            throw ExplicitProbeFailureClassifier.Map(failure, endpoint, password is not null, cancellationToken, bound.Token);
        }
    }
}
