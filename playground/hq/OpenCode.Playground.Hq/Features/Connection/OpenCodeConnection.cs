using OpenCode.Sdk;

namespace OpenCode.Playground.Hq.Features.Connection;

/// <summary>Builds HQ's client from the AppHost configuration.</summary>
internal static class OpenCodeConnection
{
    public static OpenCodeClient Connect(IConfiguration configuration)
    {
        if (!Uri.TryCreate(configuration["OpenCode:Endpoint"], UriKind.Absolute, out var endpoint))
        {
            throw new InvalidOperationException("OpenCode:Endpoint is missing or invalid. Start HQ through its AppHost.");
        }

        return new OpenCodeClient(new OpenCodeClientOptions { Endpoint = endpoint, Password = configuration["OpenCode:Password"] });
    }
}
