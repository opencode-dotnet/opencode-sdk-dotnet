using System.Text.Json;
using OpenCode.Sdk.Internal.BackgroundService.Abstractions;
using OpenCode.Sdk.Internal.BackgroundService.Registration;
using OpenCode.Sdk.Internal.Diagnostics;

namespace OpenCode.Sdk.Internal.BackgroundService.Handoff;

/// <summary>
/// The pinned client's persistent-terminal handoff sidecar (<c>pty-handoff.ts</c>) over the
/// background-service filesystem seam. The sidecar sits beside the registration at
/// <c>&lt;registration&gt;.pty-handoff</c>; publication goes through an exclusive owner-only
/// temporary file and a replace-on-success rename in the same directory, and a JSON-null handoff
/// carries a 30-second fallback expiry. The ticket is requested through the SDK's own request
/// pipeline and kept raw, so it crosses to the replacement daemon exactly as the route answered it;
/// the shutdown fallback goes through the Stop lifecycle's <see cref="IServicePtyShutdown"/> seam.
/// Preparation never fails the replacement: persistent terminals are best-effort, so any failure
/// but the caller's cancellation replaces the service without a handoff.
/// Time comes from the injected clock so every expiry rule is testable without waiting.
/// </summary>
internal sealed class ServicePtyHandoff(IServiceFileSystem fileSystem, IServiceClock clock, IServicePtyShutdown ptyShutdown)
    : IServicePtyHandoff
{
    /// <summary>The sidecar suffix the pinned client keeps beside the registration.</summary>
    internal const string SidecarSuffix = ".pty-handoff";

    /// <summary>The publish failure the preparation drops, the way the pinned client only warns about it.</summary>
    internal const string PublishFailedMessage = "Failed to publish persistent terminal handoff.";

    /// <summary>The pinned client's <c>Invalid persistent terminal handoff response</c>: the body has no handoff member.</summary>
    internal const string InvalidResponseMessage = "Invalid persistent terminal handoff response.";

    /// <summary>The pinned client's <c>Invalid or expired persistent terminal handoff</c>.</summary>
    internal const string InvalidHandoffMessage = "Invalid or expired persistent terminal handoff.";

    /// <summary>The environment variable a replacement contender reads its ticket from.</summary>
    private const string HandoffVariable = "OPENCODE_PTY_HANDOFF";

    /// <summary>Upstream's fallback expiry for a sidecar that carries no ticket (<c>publish</c> in <c>pty-handoff.ts</c>).</summary>
    private static readonly TimeSpan NullSidecarLifetime = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    public async Task PrepareAsync(
        string registrationFile,
        ServiceRegistration registration,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registrationFile);
        ArgumentNullException.ThrowIfNull(registration);
        cancellationToken.ThrowIfCancellationRequested();

        var sidecarPath = registrationFile + SidecarSuffix;
        if (await IsFreshMatchAsync(sidecarPath, registration, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        PtyHandoffSidecar sidecar;
        try
        {
            var answer = await RequestTicketAsync(registration, timeout, cancellationToken).ConfigureAwait(false);
            sidecar = Parse(answer, registration);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception failure) when (failure is OpenCodeException or OperationCanceledException)
        {
            // Another caller may already have prepared and stopped this server; otherwise the
            // daemon's terminals are shut down and the replacement starts without a handoff.
            if (await IsFreshMatchAsync(sidecarPath, registration, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            await ShutdownAsync(registration, timeout, cancellationToken).ConfigureAwait(false);
            sidecar = NewSidecar(registration, handoff: null, NowMilliseconds() + NullSidecarLifetime.TotalMilliseconds);
        }

        await TryPublishAsync(sidecarPath, sidecar, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, string?>> EnvironmentAsync(
        string registrationFile,
        IReadOnlyDictionary<string, string>? callerEnvironment,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registrationFile);
        cancellationToken.ThrowIfCancellationRequested();

        var overlay = new Dictionary<string, string?>(callerEnvironment?.Count ?? 0, StringComparer.Ordinal);
        if (callerEnvironment is not null)
        {
            foreach (var entry in callerEnvironment)
            {
                overlay[entry.Key] = entry.Value;
            }
        }

        overlay[HandoffVariable] = await AdoptAsync(registrationFile, cancellationToken).ConfigureAwait(false);
        return overlay;
    }

    /// <inheritdoc />
    public async Task CompleteAsync(
        string registrationFile,
        ServiceRegistration registration,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registrationFile);
        ArgumentNullException.ThrowIfNull(registration);
        cancellationToken.ThrowIfCancellationRequested();

        var sidecar = await ReadSidecarAsync(registrationFile + SidecarSuffix, cancellationToken).ConfigureAwait(false);
        if (sidecar is not null && !Matches(sidecar, registration))
        {
            await ClearAsync(registrationFile, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public Task ClearAsync(string registrationFile, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registrationFile);
        cancellationToken.ThrowIfCancellationRequested();

        var sidecarPath = registrationFile + SidecarSuffix;
        try
        {
            _ = fileSystem.TryDelete(sidecarPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new OpenCodeServerException($"The persistent-terminal handoff sidecar '{sidecarPath}' could not be removed.", exception);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// The handoff route through the SDK's own request pipeline — the transport, credential, and
    /// status mapping every generated door uses — with the answer kept raw, under the request bound.
    /// </summary>
    private static async Task<ServiceHandoffTicketResponse> RequestTicketAsync(
        ServiceRegistration registration,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var pipeline = Pipeline.Create(new OpenCodeClientOptions
        {
            Endpoint = registration.Endpoint,
            Password = registration.Password,
        });
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bound.CancelAfter(timeout);
        return await pipeline
            .ExecuteAsync(HttpMethod.Post, OpenCodeRoutes.PersistentPtys.Handoff, ServiceHandoffTicketAdapter.Instance, options: null, bound.Token)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// <c>parse</c>: no handoff member refuses the answer; a null ticket becomes a null sidecar with
    /// the fallback expiry; a ticket must carry the members the pin knows and not be expired, and is
    /// kept whole. A refusal takes the same fallback as a failed request.
    /// </summary>
    private PtyHandoffSidecar Parse(ServiceHandoffTicketResponse answer, ServiceRegistration registration)
    {
        if (!answer.HasHandoffMember)
        {
            throw new OpenCodeServerException(InvalidResponseMessage);
        }

        if (answer.Handoff is not { } ticket)
        {
            return NewSidecar(registration, handoff: null, NowMilliseconds() + NullSidecarLifetime.TotalMilliseconds);
        }

        if (!PtyHandoffSidecar.IsHandoff(ticket) ||
            !PtyHandoffSidecar.TryGetFinite(ticket, "expiresAt", out var expiresAt) ||
            expiresAt <= NowMilliseconds())
        {
            throw new OpenCodeServerException(InvalidHandoffMessage);
        }

        return NewSidecar(registration, ticket, expiresAt);
    }

    /// <summary>
    /// <c>prepare</c>'s fallback: shut the daemon's terminals down under the request bound before
    /// it is replaced, ignoring every failure but the caller's cancellation.
    /// </summary>
    [SlopwatchSuppress(
        "SW003",
        "The pinned client's shutdown .catch(() => {}) ignores every error (prepare in pty-handoff.ts): terminals are best-effort, and the daemon is replaced next either way.")]
    private async Task ShutdownAsync(ServiceRegistration registration, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bound.CancelAfter(timeout);
        try
        {
            await ptyShutdown.ShutdownAsync(registration, bound.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception failure) when (failure is OpenCodeException or OperationCanceledException)
        {
            // Best-effort: the replacement starts without these terminals.
        }
    }

    /// <summary>
    /// <c>publish(...).catch(warn)</c>: a sidecar that cannot be written leaves the replacement
    /// without a handoff; it never fails the preparation.
    /// </summary>
    [SlopwatchSuppress(
        "SW003",
        "The pinned client only warns when publishing the handoff fails (prepare in pty-handoff.ts): the replacement then starts without the terminals, which is the best-effort contract.")]
    private async Task TryPublishAsync(string sidecarPath, PtyHandoffSidecar sidecar, CancellationToken cancellationToken)
    {
        try
        {
            await PublishAsync(sidecarPath, sidecar, cancellationToken).ConfigureAwait(false);
        }
        catch (OpenCodeServerException)
        {
            // Best-effort: no sidecar means no handoff.
        }
    }

    private async Task<bool> IsFreshMatchAsync(string sidecarPath, ServiceRegistration registration, CancellationToken cancellationToken)
    {
        var sidecar = await ReadSidecarAsync(sidecarPath, cancellationToken).ConfigureAwait(false);
        return sidecar is not null && sidecar.ExpiresAt > NowMilliseconds() && Matches(sidecar, registration);
    }

    /// <summary>
    /// <c>environment</c>: an unexpired sidecar is adopted when the registration is gone or still names the
    /// sidecar's source. The SDK reads an undecodable registration as absent, which adopts, where the
    /// pinned client compares whatever <c>JSON.parse</c> returned; its own discovery reads such a
    /// file as absent too, and the ticket is validated by the daemon that receives it.
    /// </summary>
    private async Task<string?> AdoptAsync(string registrationFile, CancellationToken cancellationToken)
    {
        var sidecar = await ReadSidecarAsync(registrationFile + SidecarSuffix, cancellationToken).ConfigureAwait(false);
        if (sidecar is null || sidecar.ExpiresAt <= NowMilliseconds())
        {
            return null;
        }

        var current = await ServiceRegistrationReader.TryReadAsync(fileSystem, registrationFile, cancellationToken).ConfigureAwait(false);
        return current is null || Matches(sidecar, current) ? sidecar.Handoff?.GetRawText() : null;
    }

    private async Task<PtyHandoffSidecar?> ReadSidecarAsync(string sidecarPath, CancellationToken cancellationToken)
    {
        var bytes = await fileSystem.TryReadAllBytesAsync(sidecarPath, cancellationToken).ConfigureAwait(false);
        return bytes is null ? null : PtyHandoffSidecar.TryRead(bytes);
    }

    /// <summary>
    /// <c>publish</c>: an exclusive owner-only temporary, renamed over the sidecar. A filesystem
    /// failure surfaces as <see cref="PublishFailedMessage"/>, which the preparation drops.
    /// </summary>
    private async Task PublishAsync(string sidecarPath, PtyHandoffSidecar sidecar, CancellationToken cancellationToken)
    {
        var temporary = sidecarPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (!await fileSystem.TryCreateExclusiveAsync(temporary, sidecar.ToUtf8Json(), cancellationToken).ConfigureAwait(false))
            {
                throw new OpenCodeServerException($"The temporary persistent-terminal handoff sidecar '{temporary}' already exists.");
            }

            try
            {
                fileSystem.Rename(temporary, sidecarPath);
            }
            finally
            {
                RemoveTemporary(temporary);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new OpenCodeServerException(PublishFailedMessage, exception);
        }
    }

    /// <summary>The pinned client's <c>finally(() =&gt; rm(temporary, { force: true }))</c>.</summary>
    [SlopwatchSuppress(
        "SW003",
        "The pinned client removes the temporary with rm({ force: true }) in publish's finally (pty-handoff.ts): a stray exists only when the publish already failed, and that failure is the one worth reporting.")]
    private void RemoveTemporary(string temporary)
    {
        try
        {
            _ = fileSystem.TryDelete(temporary);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The publish outcome is the one reported.
        }
    }

    private static PtyHandoffSidecar NewSidecar(ServiceRegistration registration, JsonElement? handoff, double expiresAt) =>
        new()
        {
            SourceId = registration.Id,
            SourcePid = registration.ProcessId,
            SourceUrl = registration.Url,
            Handoff = handoff,
            ExpiresAt = expiresAt,
        };

    /// <summary><c>same</c>: the source identity is id, pid, and url; the version is not part of it.</summary>
    private static bool Matches(PtyHandoffSidecar sidecar, ServiceRegistration registration) =>
        string.Equals(sidecar.SourceId, registration.Id, StringComparison.Ordinal) &&
        sidecar.SourcePid == registration.ProcessId &&
        string.Equals(sidecar.SourceUrl, registration.Url, StringComparison.Ordinal);

    private double NowMilliseconds() => clock.UtcNow.ToUnixTimeMilliseconds();
}
