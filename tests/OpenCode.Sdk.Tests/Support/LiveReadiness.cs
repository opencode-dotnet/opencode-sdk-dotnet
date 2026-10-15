using System.Diagnostics;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;

namespace OpenCode.Sdk.Tests.Support;

internal static class LiveReadiness
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    public static Task<T> WaitAsync<T>(
        Func<CancellationToken, Task<T>> observe,
        Func<T, bool> ready,
        string description,
        CancellationToken cancellationToken) =>
        WaitAsync(observe, ready, description, describe: null, cancellationToken);

    /// <summary>
    /// The readiness poll, naming what it last observed when it gives up: a timeout that only says
    /// "not ready" cannot tell a slow server from a wrong answer. A declared
    /// <see cref="ServiceUnavailableError"/> is the server saying "not available yet" (the model and
    /// provider routes declare it while their catalog warms up), so it reads as not ready and the
    /// poll goes on; every other error ends the wait at once.
    /// </summary>
    [SlopwatchSuppress(
        "SW004",
        "Readiness poll of another process: plugin activation and VCS provider detection settle on the server's own schedule after the request that triggers them has answered, no wire signal is awaited for either (the event bus has no replay, so a subscription opened after the trigger can miss it), and the poll is bounded by a thirty-second deadline that also cancels with the caller.")]
    public static Task<T> WaitAsync<T>(
        Func<CancellationToken, Task<T>> observe,
        Func<T, bool> ready,
        string description,
        Func<T, string>? describe,
        CancellationToken cancellationToken) =>
        WaitAsync(observe, ready, description, describe, Budget, cancellationToken);

    /// <summary>The readiness poll with its budget named, so a test can prove what the end of the budget reports.</summary>
    internal static async Task<T> WaitAsync<T>(
        Func<CancellationToken, Task<T>> observe,
        Func<T, bool> ready,
        string description,
        Func<T, string>? describe,
        TimeSpan budget,
        CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        var last = default(T);
        var observedOnce = false;
        OpenCodeApiException? unavailable = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(budget);
        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                try
                {
                    var observed = await observe(deadline.Token);
                    last = observed;
                    observedOnce = true;
                    unavailable = null;
                    if (ready(observed))
                    {
                        return observed;
                    }
                }
                catch (OpenCodeApiException exception) when (exception.Error is ServiceUnavailableError)
                {
                    // Not ready yet, in the server's own words; the timeout below names it if it lasts.
                    unavailable = exception;
                }

                await Task.Delay(PollInterval, deadline.Token);
            }
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            var seen = unavailable is not null
                ? $"; the last answer was {unavailable.Status.ToString(System.Globalization.CultureInfo.InvariantCulture)} ServiceUnavailableError"
                : Seen(describe, observedOnce, last);
            throw new TimeoutException($"Timed out waiting for {description} after {elapsed.Elapsed}{seen}.", unavailable ?? (Exception)exception);
        }
    }

    public static Task<PluginListResponse> PluginAsync(
        OpenCodeClient client, string id, CancellationToken cancellationToken) =>
        WaitAsync(
            token => client.Plugins.ListPluginsAsync(cancellationToken: token),
            response => IsActive(response.Plugins, id),
            "active plugin " + id,
            response => DescribeInventory(response.Plugins),
            cancellationToken);

    public static Task<VcsResponse> GitAsync(
        OpenCodeClient client, LocationSelector location, CancellationToken cancellationToken) =>
        WaitAsync(
            token => client.Vcs.GetVcsAsync(new VcsRequest { Location = location }, cancellationToken: token),
            response => response.Vcs.Provider == "git",
            "Git provider at " + location.Directory,
            cancellationToken);

    private static bool IsActive(IReadOnlyList<PluginInfo> plugins, string id)
    {
        // A configured module that fails to load is listed without an id (the pinned server's
        // plugin supervisor), so it would read as merely absent until the budget ran out. The
        // isolated fixture configures only the suite's own plugins, so any such entry is ours.
        if (plugins.FirstOrDefault(candidate => candidate.Id is null && candidate.State is PluginStateFailed) is { State: PluginStateFailed unloaded })
        {
            throw new InvalidOperationException("A plugin module failed to load before " + id + " was active: " + unloaded.Error + " (ref " + unloaded.Ref + ")");
        }

        var plugin = plugins.SingleOrDefault(candidate => candidate.Id == id);
        if (plugin?.State is PluginStateFailed failed)
        {
            throw new InvalidOperationException("Plugin " + id + " failed: " + failed.Error);
        }

        return plugin?.State is PluginStateActive;
    }

    /// <summary>What the poll last saw, for the timeout message; empty when the caller names nothing to describe.</summary>
    private static string Seen<T>(Func<T, string>? describe, bool observedOnce, T? last)
    {
        if (describe is null)
        {
            return string.Empty;
        }

        return observedOnce ? "; last observed: " + describe(last!) : "; nothing answered";
    }

    /// <summary>The inventory as one line: each entry's id and state, so a timeout shows what the server did list.</summary>
    private static string DescribeInventory(IReadOnlyList<PluginInfo> plugins) =>
        plugins.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " entries: " + string.Join(
            ", ",
            plugins.Select(static plugin => (plugin.Id ?? "<no id>") + "=" + plugin.State switch
            {
                PluginStateActive => "active",
                PluginStateFailed failed => "failed(" + failed.Error + ")",
                _ => plugin.State.GetType().Name,
            }));
}
