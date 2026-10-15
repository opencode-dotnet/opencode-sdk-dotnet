namespace OpenCode.Sdk.Internal;

/// <summary>
/// The daemon's application state as the info route's response code reports it (server
/// <c>service-status.ts</c>): a 2xx answer is ready, 500 is failed, every other decodable answer
/// (503 while starting or stopping) is waiting.
/// </summary>
internal enum ServiceState
{
    Ready,
    Waiting,
    Failed,
}
