using OpenCode.Sdk.Tests.Harness;

namespace OpenCode.Sdk.Tests.Sessions.Support;

internal sealed class OwnedDriveInvocation
{
    private readonly Func<string, Task> _disconnect;
    private readonly Func<string, Task> _finish;
    private readonly Func<string, string, Task> _chunkText;

    public OwnedDriveInvocation(DriveController controller, DriveInvocation invocation)
        : this(
            invocation,
            (id, text) => controller.ChunkTextAsync(id, text),
            id => controller.FinishAsync(id),
            controller.DisconnectAsync)
    {
        ArgumentNullException.ThrowIfNull(controller);
    }

    internal OwnedDriveInvocation(
        DriveInvocation invocation,
        Func<string, string, Task> chunkText,
        Func<string, Task> finish,
        Func<string, Task> disconnect)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(chunkText);
        ArgumentNullException.ThrowIfNull(finish);
        ArgumentNullException.ThrowIfNull(disconnect);
        Invocation = invocation;
        _chunkText = chunkText;
        _finish = finish;
        _disconnect = disconnect;
    }

    public DriveInvocation Invocation { get; }

    public bool IsFinished { get; private set; }

    public Task ChunkTextAsync(string text) =>
        _chunkText(Invocation.Id, text);

    public async Task FinishAsync()
    {
        await _finish(Invocation.Id);
        IsFinished = true;
    }

    public async Task DisconnectAsync(CancellationToken _)
    {
        if (IsFinished)
        {
            return;
        }

        await _disconnect(Invocation.Id);
        IsFinished = true;
    }
}
