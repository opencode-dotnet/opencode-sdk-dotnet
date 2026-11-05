using System.Globalization;
using OpenCode.Sdk;
using OpenCode.Sdk.Models;

// Inspects a repository: the folder given, or the current one.
var directory = args.Length > 0 ? args[0] : Environment.CurrentDirectory;
try
{
    directory = Path.GetFullPath(directory);
}
catch (ArgumentException)
{
    await Console.Error.WriteLineAsync($"AotCli: '{directory}' is not a folder.");
    return 2;
}
if (!Directory.Exists(directory))
{
    await Console.Error.WriteLineAsync($"AotCli: '{directory}' is not a folder.");
    return 2;
}

using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
try
{
    // A private server that opens the repository. It stops when this block ends.
    await using var server = await OpenCodeServer.StartAsync(new OpenCodeServerOptions { WorkingDirectory = directory }, deadline.Token);
    using var client = server.CreateClient();

    // The server reads the branch in the background after it opens the folder, and announces it on the event bus.
    using var listening = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
    await using var events = client.Events.SubscribeAsync(cancellationToken: listening.Token).GetAsyncEnumerator();
    try
    {
        // The first event is always server.connected: the subscription is live, so nothing published after it is missed.
        await events.MoveNextAsync();
        var vcs = await client.Vcs.GetVcsAsync(cancellationToken: deadline.Token);
        var branch = vcs.Vcs.Branch.Current ?? await AnnouncedBranchAsync(events, client.Vcs, vcs.Location.Directory, listening, deadline.Token);
        Console.WriteLine($"repository: {vcs.Location.Directory}");
        Console.WriteLine($"branch: {branch ?? "unavailable"}");
    }
    finally
    {
        // Cancelling releases the live connection now; every pull above is awaited,
        // so none is pending when the enumerator is disposed.
        await listening.CancelAsync();
    }

    var status = await client.Vcs.GetStatusAsync(cancellationToken: deadline.Token);
    Console.WriteLine($"changes: {status.Changes.Count}");
    foreach (var change in status.Changes.Take(20))
    {
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {change.Status.Value,-8} +{change.Additions} -{change.Deletions}  {change.File}"));
    }

    var entries = await client.FileSystem.ListEntriesAsync(cancellationToken: deadline.Token);
    Console.WriteLine($"top level: {entries.Entries.Count(static e => e.Type == FileSystemEntryType.Directory)} folders, {entries.Entries.Count(static e => e.Type == FileSystemEntryType.File)} files");

    var config = await client.Config.GetConfigAsync(cancellationToken: deadline.Token);
    foreach (var entry in config.Config)
    {
        var where = entry switch
        {
            ConfigDocument document => document.Path ?? "(no file)",
            ConfigDirectory folder => folder.Path,
            _ => null,
        };
        Console.WriteLine(where is null ? $"config {entry.Type}" : $"config {entry.Type}: {where}");
    }

    Console.WriteLine("AotCli: inspected");
    return 0;
}
catch (Exception failure) when (failure is OpenCodeException or OperationCanceledException)
{
    await Console.Error.WriteLineAsync($"AotCli stopped: {failure.Message} On a first run, check the prerequisites: https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/playground/README.md#prerequisites");
    return 1;
}

// Waits a few seconds for the branch announcement; a folder that isn't a repository never sends one.
static async Task<string?> AnnouncedBranchAsync(IAsyncEnumerator<IEvent> events, VcsClient vcs, string directory, CancellationTokenSource listening, CancellationToken deadlineToken)
{
    listening.CancelAfter(TimeSpan.FromSeconds(5));
    try
    {
        while (await events.MoveNextAsync())
        {
            if (events.Current.Type is "server.connected")
            {
                return (await vcs.GetVcsAsync(cancellationToken: listening.Token)).Vcs.Branch.Current;
            }

            if (events.Current is VcsBranchUpdated updated && updated.Location?.Directory == directory)
            {
                return updated.Data.Branch;
            }
        }
    }
    catch (OperationCanceledException) when (listening.IsCancellationRequested && !deadlineToken.IsCancellationRequested)
    {
        return null; // The five-second wait expired; the caller prints branch: unavailable.
    }

    return null;
}
