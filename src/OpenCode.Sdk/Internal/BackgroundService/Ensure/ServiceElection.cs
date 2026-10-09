using OpenCode.Sdk.Internal.BackgroundService.Abstractions;
using OpenCode.Sdk.Internal.BackgroundService.Discovery;
using OpenCode.Sdk.Internal.BackgroundService.Registration;
using OpenCode.Sdk.Internal.Diagnostics;

namespace OpenCode.Sdk.Internal.BackgroundService.Ensure;

/// <summary>
/// One Ensure election, the pinned client's <c>ensure</c> loop (<c>promise/service.ts</c>)
/// read statement for statement: each round reads the registration and probes it, counts
/// consecutive timeouts on one identity and recovers after the third, reuses a ready compatible
/// service, fails on a failed one or on one whose health protocol is incompatible while its
/// version matches, replaces one whose version does not match, and otherwise harvests finished
/// contenders and keeps at most two live, until a service wins or the wall-clock bound expires.
/// The first contender failure is held until a recovery or replacement drops it: while held, no
/// contender is recruited, and the bound expires with it rather than with the timeout.
/// Its fields are upstream's loop locals; one instance serves one call.
/// </summary>
internal sealed class ServiceElection(ServiceElectionSeams seams, EnsureRequest request, ServicePaths paths)
{
    private static readonly ServiceProbeResult NoService = new(State: null, Version: null, TimedOut: false, Compatible: true);

    private readonly List<IServiceContender> _contenders = [];
    private ServiceRegistrationIdentity? _timeoutIdentity;
    private int _timeoutCount;
    private bool _announced;
    private DateTimeOffset? _lastSpawn;
    private TimeSpan _spawnDelay = seams.Timing.SpawnDelay;
    private OpenCodeServerException? _lastReplaceFailure;
    private OpenCodeServerException? _failure;

    /// <summary>Runs the election until a ready compatible service is registered.</summary>
    /// <param name="cancellationToken">The caller's token; it propagates from every wait.</param>
    /// <returns>The winning registration, which always carries a password.</returns>
    /// <exception cref="OpenCodeServerException">The bound expired, the service failed, its health protocol is incompatible at a matching version, a contender failed with none live, or a spawn failed.</exception>
    public async Task<ServiceRegistration> RunAsync(CancellationToken cancellationToken)
    {
        var deadline = seams.Clock.UtcNow + seams.Timing.PromiseTimeout;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (seams.Clock.UtcNow >= deadline)
                {
                    throw _failure ?? (_lastReplaceFailure is null
                        ? new OpenCodeServerException(ServiceEnsurer.TimeoutMessage)
                        : new OpenCodeServerException(ServiceEnsurer.TimeoutMessage, _lastReplaceFailure));
                }

                var registration = await ServiceRegistrationReader
                    .TryReadAsync(seams.FileSystem, paths.RegistrationFile, cancellationToken)
                    .ConfigureAwait(false);
                var answer = registration is null
                    ? NoService
                    : await seams.Probe.ProbeAsync(registration, cancellationToken).ConfigureAwait(false);
                await RecoverUnresponsiveAsync(registration, answer, cancellationToken).ConfigureAwait(false);

                // A registration without a password never wins: the handle's credential is
                // non-null by contract, the same rule Discover applies.
                if (registration is { Password: not null } && answer.IsService)
                {
                    if (await SettleRegisteredAsync(registration, answer, cancellationToken).ConfigureAwait(false))
                    {
                        return registration;
                    }
                }
                else
                {
                    await HarvestAndMaybeSpawnAsync(registration is not null, cancellationToken).ConfigureAwait(false);
                }

                await Task.Delay(seams.Timing.PollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            foreach (var contender in _contenders)
            {
                contender.Release();
            }
        }
    }

    /// <summary>
    /// The timeout branch: consecutive timeouts on one identity; at the third, the daemon is taken as
    /// unresponsive — its persistent terminals cannot be handed off — and ended, the contenders it
    /// made moot are released, and the spawn delay is treated as already elapsed.
    /// </summary>
    private async Task RecoverUnresponsiveAsync(ServiceRegistration? registration, ServiceProbeResult answer, CancellationToken cancellationToken)
    {
        if (!answer.TimedOut || registration is null)
        {
            _timeoutIdentity = null;
            _timeoutCount = 0;
            return;
        }

        var identity = ServiceRegistrationIdentity.Of(registration);
        _timeoutCount = _timeoutIdentity == identity ? _timeoutCount + 1 : 1;
        _timeoutIdentity = identity;
        if (_timeoutCount < 3)
        {
            return;
        }

        Announce(OpenCodeServerEnsureReason.Missing, previousVersion: null);
        await seams.Handoff.ClearAsync(paths.RegistrationFile, cancellationToken).ConfigureAwait(false);
        await seams.Terminator.TerminateAsync(registration, paths.RegistrationFile, cancellationToken).ConfigureAwait(false);
        ReleaseStoppedContenders(registration.ProcessId);
        _timeoutIdentity = null;
        _timeoutCount = 0;
        _lastSpawn = seams.Clock.UtcNow - _spawnDelay;
    }

    /// <summary>
    /// The registered-service branch: a registered service resets the spawn delay; ready and compatible wins, failed
    /// and compatible fails, an incompatible health protocol at a matching version fails, and a version mismatch is
    /// replaced.
    /// </summary>
    /// <returns>True when the service won.</returns>
    private async Task<bool> SettleRegisteredAsync(ServiceRegistration registration, ServiceProbeResult answer, CancellationToken cancellationToken)
    {
        _spawnDelay = seams.Timing.SpawnDelay;
        var versionMatches = ServiceVersionPolicy.MatchesVersion(answer.Version, request.LoopVersion);
        var compatible = answer.Compatible && versionMatches;
        if (!answer.Compatible && versionMatches)
        {
            throw new OpenCodeServerException(ServiceEnsurer.IncompatibleProtocolMessage);
        }

        if (compatible && answer.State == ServiceState.Ready)
        {
            await seams.Handoff.CompleteAsync(paths.RegistrationFile, registration, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (compatible && answer.State == ServiceState.Failed)
        {
            throw new OpenCodeServerException(ServiceEnsurer.FailedMessage);
        }

        if (!compatible)
        {
            Announce(OpenCodeServerEnsureReason.VersionMismatch, answer.Version);
            await ReplaceAsync(handOff: answer.State == ServiceState.Ready, cancellationToken).ConfigureAwait(false);
            ReleaseStoppedContenders(registration.ProcessId);
            _lastSpawn = null;
        }

        return false;
    }

    /// <summary>
    /// The replacement stop, <c>stop({ pty: ready ? "handoff" : "clear" }).catch(() =&gt; undefined)</c>:
    /// like upstream's <c>stop</c> it reads the registration afresh, prepares the handoff for the
    /// service registered now (or clears the sidecar when the probed one was not ready, or when
    /// nothing is registered any more), and terminates that service; the terminator's identity check
    /// then skips a record that changed again since. The handoff or clear is best-effort and never
    /// keeps the old service running, and a failure does not end the election, which probes again
    /// next round. The last failure is kept as the timeout's cause when no contender failure is held.
    /// </summary>
    private async Task ReplaceAsync(bool handOff, CancellationToken cancellationToken)
    {
        try
        {
            var current = await ServiceRegistrationReader
                .TryReadAsync(seams.FileSystem, paths.RegistrationFile, cancellationToken)
                .ConfigureAwait(false);
            await PrepareOrClearAsync(current, handOff, cancellationToken).ConfigureAwait(false);
            if (current is not null)
            {
                await seams.Terminator.TerminateAsync(current, paths.RegistrationFile, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OpenCodeServerException failure)
        {
            _lastReplaceFailure = failure;
        }
    }

    /// <summary>
    /// <c>stop</c>'s handoff step: a failure to prepare or clear is only warned about upstream, so
    /// the terminate that follows always runs. The caller's cancellation still propagates.
    /// </summary>
    [SlopwatchSuppress(
        "SW003",
        "The pinned client's stop catches a failed handoff or clear and only warns (service.ts): persistent terminals are best-effort and must never keep the old service running.")]
    private async Task PrepareOrClearAsync(ServiceRegistration? registration, bool handOff, CancellationToken cancellationToken)
    {
        try
        {
            if (handOff && registration is not null)
            {
                await seams.Handoff
                    .PrepareAsync(paths.RegistrationFile, registration, seams.Timing.RequestTimeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await seams.Handoff.ClearAsync(paths.RegistrationFile, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OpenCodeServerException)
        {
            // Best-effort: the old service is terminated next either way.
        }
    }

    /// <summary>
    /// After a recovery or replacement stop, the contenders it made moot (the stopped pid itself,
    /// or any that finished) are released and dropped, and a held contender failure no longer
    /// stands, so the next round may recruit again.
    /// </summary>
    private void ReleaseStoppedContenders(int stoppedProcessId)
    {
        foreach (var contender in _contenders.Where(contender => contender.ProcessId == stoppedProcessId || contender.Finished).ToList())
        {
            contender.Release();
            _ = _contenders.Remove(contender);
            contender.Dispose();
        }

        _failure = null;
    }

    /// <summary>
    /// The no-service branch: no usable service. Finished contenders are dropped (and disposed) after their
    /// failures and exit-0s are read; the first failure is held, and a held failure with none left live
    /// ends the election; an exit 0 doubles the spawn delay; while no failure is held, one more
    /// contender starts while fewer than two are live and the delay has passed.
    /// </summary>
    private async Task HarvestAndMaybeSpawnAsync(bool registered, CancellationToken cancellationToken)
    {
        if (_lastSpawn is null && registered)
        {
            _lastSpawn = seams.Clock.UtcNow;
        }

        var finished = _contenders.Where(static contender => contender.Finished).ToList();
        _failure ??= finished.Select(static contender => contender.TryGetFailure()).FirstOrDefault(static failure => failure is not null);
        if (finished.Exists(static contender => contender.ExitedZero))
        {
            var doubled = _spawnDelay + _spawnDelay;
            _spawnDelay = doubled < seams.Timing.MaxSpawnDelay ? doubled : seams.Timing.MaxSpawnDelay;
        }

        foreach (var contender in finished)
        {
            _ = _contenders.Remove(contender);
            contender.Dispose();
        }

        if (_failure is not null && _contenders.Count == 0)
        {
            throw _failure;
        }

        // One candidate plus one lock probe for pre-lock stalls. After a failure, the survivors
        // finish without replacements that could hide the error until the bound.
        if (_failure is null && _contenders.Count < 2 && (_lastSpawn is not { } spawned || seams.Clock.UtcNow - spawned >= _spawnDelay))
        {
            Announce(OpenCodeServerEnsureReason.Missing, previousVersion: null);
            _contenders.Add(await SpawnAsync(cancellationToken).ConfigureAwait(false));
            _lastSpawn = seams.Clock.UtcNow;
        }
    }

    /// <summary>
    /// <c>spawnContender</c>: the command's executable resolved the launcher's way, the channel's
    /// service-config environment under the caller's, and the handoff ticket over both. Any failure
    /// but the caller's cancellation is "failed to start".
    /// </summary>
    private async Task<IServiceContender> SpawnAsync(CancellationToken cancellationToken)
    {
        try
        {
            var overlay = await OverlayAsync(cancellationToken).ConfigureAwait(false);
            var environment = await seams.Handoff
                .EnvironmentAsync(paths.RegistrationFile, overlay, cancellationToken)
                .ConfigureAwait(false);
            return seams.Spawner.Spawn(new IServiceContenderSpawner.ContenderStartInfo(
                seams.Executables.Resolve(request.Command[0]),
                [.. request.Command.Skip(1)],
                environment));
        }
        catch (Exception cause) when (cause is not (OperationCanceledException or OpenCodeServerException))
        {
            throw new OpenCodeServerException("Failed to start the background service.", cause);
        }
    }

    /// <summary>The channel's service-config <c>env</c> (the CLI's <c>ServiceConfig.options()</c>), with the caller's entries over it.</summary>
    private async Task<IReadOnlyDictionary<string, string>?> OverlayAsync(CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, string>? configured = null;
        if (paths.ConfigFile is { } configFile &&
            await seams.FileSystem.TryReadAllBytesAsync(configFile, cancellationToken).ConfigureAwait(false) is { } bytes)
        {
            configured = ServiceConfigReader.TryReadEnvironment(bytes);
        }

        if (configured is null || request.Environment is null)
        {
            return request.Environment ?? configured;
        }

        var merged = new Dictionary<string, string>(configured.Count + request.Environment.Count, StringComparer.Ordinal);
        foreach (var entry in configured)
        {
            merged[entry.Key] = entry.Value;
        }

        foreach (var entry in request.Environment)
        {
            merged[entry.Key] = entry.Value;
        }

        return merged;
    }

    /// <summary><c>announce</c>: <c>OnStart</c> fires at most once per call.</summary>
    private void Announce(OpenCodeServerEnsureReason reason, string? previousVersion)
    {
        if (_announced)
        {
            return;
        }

        _announced = true;
        request.OnStart?.Invoke(reason, previousVersion);
    }
}
