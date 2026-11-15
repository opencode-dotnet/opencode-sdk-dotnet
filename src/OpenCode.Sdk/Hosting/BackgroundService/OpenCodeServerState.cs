namespace OpenCode.Sdk;

/// <summary>
/// The state a registered background service reports on its info route, as opencode's service
/// probe classifies the answer.
/// </summary>
public enum OpenCodeServerState
{
    /// <summary>A 2xx answer: the service is ready.</summary>
    Ready = 0,

    /// <summary>Any other answer identifying the service, such as 503 while it starts or stops.</summary>
    Waiting = 1,

    /// <summary>A 500 answer: the service failed to start.</summary>
    Failed = 2,
}
