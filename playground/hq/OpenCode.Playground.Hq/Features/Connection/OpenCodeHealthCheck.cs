using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenCode.Sdk;

namespace OpenCode.Playground.Hq.Features.Connection;

/// <summary>Reports whether HQ's configured opencode server is reachable.</summary>
/// <param name="client">HQ's client.</param>
internal sealed class OpenCodeHealthCheck(OpenCodeClient client) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            _ = await client.Server.GetInfoAsync(cancellationToken: deadline.Token);
            return HealthCheckResult.Healthy();
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("HQ's opencode server is unavailable.", exception);
        }
    }
}
