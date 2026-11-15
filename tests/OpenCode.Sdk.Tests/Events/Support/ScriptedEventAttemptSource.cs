using System.Text;
using System.Threading.Channels;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Events.Support;

/// <summary>One controllable queue per physical attempt, acknowledging publication after the runner advances.</summary>
internal sealed class ScriptedEventAttemptSource : IEventAttemptSource
{
    private readonly Channel<Attempt> _opened = Channel.CreateUnbounded<Attempt>();
    private readonly List<Attempt> _attempts = [];
    private readonly Lock _gate = new();

    public IAsyncEnumerable<IEvent> OpenAsync(StreamAttempt attempt, CancellationToken cancellationToken)
    {
        var opened = new Attempt(attempt, cancellationToken);
        lock (_gate)
        {
            _attempts.Add(opened);
        }

        _ = _opened.Writer.TryWrite(opened);
        return opened;
    }

    public Task<Attempt> NextOpenAsync() => _opened.Reader.ReadAsync().AsTask().WaitAsync(HangGuard.Bound);

    public int OpenCount
    {
        get
        {
            lock (_gate)
            {
                return _attempts.Count;
            }
        }
    }

    public void ReleaseCleanup()
    {
        lock (_gate)
        {
            foreach (var attempt in _attempts)
            {
                attempt.ReleaseCleanup();
            }
        }
    }

    internal sealed class Attempt(StreamAttempt attempt, CancellationToken token) : IAsyncEnumerable<IEvent>, IAsyncEnumerator<IEvent>
    {
        private readonly Channel<bool> _cleanupRelease = Channel.CreateUnbounded<bool>();
        private Publication? _previous;
        internal Channel<Publication> Items { get; } = Channel.CreateUnbounded<Publication>();
        internal TaskCompletionSource CleanupEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CleanupReleased { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token { get; } = token;
        public Exception? CleanupFailure { get; set; }
        public IEvent Current { get; private set; } = null!;

        public IAsyncEnumerator<IEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;

        public async ValueTask<bool> MoveNextAsync()
        {
            _previous?.Published.SetResult();
            _previous = null;
            while (await Items.Reader.WaitToReadAsync(Token))
            {
                if (Items.Reader.TryRead(out var publication))
                {
                    attempt.OnFrame(new ServerSentEvent(ServerSentEvent.DefaultName, Encoding.UTF8.GetBytes(ScriptedEventServer.Connected)));
                    Current = publication.Event;
                    _previous = publication;
                    return true;
                }
            }

            return false;
        }

        public void ReleaseCleanup() => _ = _cleanupRelease.Writer.TryWrite(true);

        public async ValueTask DisposeAsync()
        {
            CleanupEntered.SetResult();
            _ = await _cleanupRelease.Reader.ReadAsync(CancellationToken.None);
            CleanupReleased.SetResult();
            if (CleanupFailure is { } failure)
            {
                throw failure;
            }
        }

        public async Task PublishAsync(IEvent item)
        {
            var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var publication = new Publication(item, published);
            await Items.Writer.WriteAsync(publication, CancellationToken.None);
            await published.Task.WaitAsync(HangGuard.Bound, CancellationToken.None);
        }
    }

    internal sealed class Publication(IEvent item, TaskCompletionSource published)
    {
        public IEvent Event { get; } = item;
        public TaskCompletionSource Published { get; } = published;
    }
}
