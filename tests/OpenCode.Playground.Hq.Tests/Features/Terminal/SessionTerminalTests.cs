using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCode.Playground.Hq.Features.Terminal;
using OpenCode.Sdk;
using OpenCode.Sdk.Models;
using TUnit.Assertions.Enums;

namespace OpenCode.Playground.Hq.Tests.Features.Terminal;

public sealed class SessionTerminalTests
{
    private const string Directory = "C:/work/app";

    [Test]
    public async Task OpenAsync_Should_Open_In_The_Sessions_Folder_And_Show_Its_Output()
    {
        await using var scenario = new TerminalScenario();
        await scenario.Pty.Session.Frames.Writer.WriteAsync(new PtyOutputFrame("hello"));
        await scenario.Terminal.OpenAsync("ses_1", CancellationToken.None);

        await Assert.That(scenario.SessionCalls.Requested).IsEqualTo("ses_1");
        await Assert.That(scenario.PtyCalls.Created?.Location?.Directory).IsEqualTo(Directory);
        await Assert.That(scenario.PtyCalls.Created?.Title).IsEqualTo("HQ");
        await Assert.That(scenario.PtyCalls.ClientFor).IsEqualTo("pty_1");
        await Assert.That(scenario.Pty.Connected?.Location?.Directory).IsEqualTo(Directory);
        await Assert.That(scenario.Terminal.Output).IsEqualTo("hello");
        await Assert.That(scenario.Terminal.IsOpen).IsTrue();
        await Assert.That(scenario.Changes).IsEqualTo(2);
    }

    [Test]
    public async Task CloseAsync_Should_End_The_Connection_And_Remove_The_Pty_Once_In_Its_Folder()
    {
        await using var scenario = new TerminalScenario();
        await scenario.Terminal.OpenAsync("ses_1", CancellationToken.None);
        scenario.Pty.RemoveRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);

        var closing = scenario.Terminal.CloseAsync();
        try
        {
            await Assert.That(scenario.Pty.Removes).IsEqualTo(1);
            await Assert.That(closing.IsCompleted).IsFalse();
            await scenario.Terminal.CloseAsync();
        }
        finally
        {
            scenario.Pty.RemoveRelease.SetResult();
            await closing;
        }

        await Assert.That(scenario.Pty.Session.Disposed).IsTrue();
        await Assert.That(scenario.Pty.Removed?.Location?.Directory).IsEqualTo(Directory);
        await Assert.That(scenario.Pty.Removes).IsEqualTo(1);
        await Assert.That(scenario.Terminal.IsOpen).IsFalse();
    }

    [Test]
    public async Task SendAsync_Should_Submit_The_Line_And_Do_Nothing_When_Closed()
    {
        await using var scenario = new TerminalScenario();
        await Assert.That(() => scenario.Terminal.SendAsync("before", CancellationToken.None)).ThrowsNothing();
        await Assert.That(scenario.Terminal.LastError).IsNull();
        await Assert.That(scenario.Changes).IsEqualTo(0);
        await scenario.Terminal.OpenAsync("ses_1", CancellationToken.None);

        await scenario.Terminal.SendAsync("dir", CancellationToken.None);

        await Assert.That(scenario.Pty.Session.Submitted).IsEquivalentTo(["dir"], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(false, "The line was not sent: socket closed")]
    [Arguments(true, "The line was not sent: the terminal has closed.")]
    public async Task SendAsync_Should_Say_Why_A_Line_Was_Not_Sent(bool disposed, string error)
    {
        await using var scenario = new TerminalScenario();
        await scenario.Terminal.OpenAsync("ses_1", CancellationToken.None);
        scenario.Pty.Session.SubmitFailure = disposed ? new ObjectDisposedException("PtySession") : new OpenCodeTransportException("socket closed");
        var changes = scenario.Changes;

        await Assert.That(() => scenario.Terminal.SendAsync("dir", CancellationToken.None)).ThrowsNothing();

        await Assert.That(scenario.Terminal.LastError).IsEqualTo(error);
        await Assert.That(scenario.Changes).IsEqualTo(changes + 1);
    }

    [Test]
    public async Task OpenAsync_Should_Create_One_Pty_When_Opened_Twice_At_Once()
    {
        await using var scenario = new TerminalScenario();
        scenario.PtyCalls.CreateRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = scenario.Terminal.OpenAsync("ses_1", CancellationToken.None);
        var second = scenario.Terminal.OpenAsync("ses_1", CancellationToken.None);
        scenario.PtyCalls.CreateRelease.SetResult();
        await Task.WhenAll(first, second).WaitAsync(CancellationToken.None);

        await Assert.That(scenario.PtyCalls.Creates).IsEqualTo(1);
        await Assert.That(scenario.Terminal.IsOpen).IsTrue();
    }

    [Test]
    public async Task OpenAsync_Should_Remove_A_Pty_Created_After_The_Page_Left()
    {
        await using var scenario = new TerminalScenario();
        using var leaving = new CancellationTokenSource();
        scenario.PtyCalls.CreateRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // The page's close runs while the create is pending, then the server answers it.
        var opening = scenario.Terminal.OpenAsync("ses_1", leaving.Token);
        await leaving.CancelAsync();
        await scenario.Terminal.CloseAsync();
        scenario.PtyCalls.CreateRelease.SetResult();

        await Assert.That(() => opening.WaitAsync(CancellationToken.None)).ThrowsNothing();
        await Assert.That(scenario.Pty.Removes).IsEqualTo(1);
    }

    [Test]
    public async Task CloseAsync_Should_Report_A_Failed_Remove_Without_Throwing()
    {
        await using var scenario = new TerminalScenario();
        await scenario.Terminal.OpenAsync("ses_1", CancellationToken.None);
        scenario.Pty.RemoveFailure = new OpenCodeTransportException("server gone");

        await Assert.That(scenario.Terminal.CloseAsync).ThrowsNothing();

        await Assert.That(scenario.Terminal.LastError).IsEqualTo("The terminal was not removed: server gone It lives until the opencode server stops.");
    }

    [Test]
    public async Task OpenAsync_Should_Remove_The_Created_Pty_When_Connecting_Fails()
    {
        await using var scenario = new TerminalScenario();
        scenario.Pty.ConnectFailure = new OpenCodeTransportException("upgrade refused");

        await Assert.That(() => scenario.Terminal.OpenAsync("ses_1", CancellationToken.None)).ThrowsNothing();

        await Assert.That(scenario.Pty.Removes).IsEqualTo(1);
        await Assert.That(scenario.Terminal.LastError).IsEqualTo("The terminal did not open: upgrade refused");
        await Assert.That(scenario.Terminal.IsOpen).IsFalse();
        await Assert.That(scenario.Changes).IsEqualTo(1);
    }

    [Test]
    [Arguments(false, "The shell exited.")]
    [Arguments(true, "The terminal stopped: socket closed")]
    public async Task OpenAsync_Should_Remove_The_Pty_When_The_Shell_Exits_Or_Reading_Fails(bool fails, string error)
    {
        await using var scenario = new TerminalScenario();
        scenario.Pty.Session.Frames.Writer.Complete(fails ? new OpenCodeTransportException("socket closed") : null);

        await scenario.Terminal.OpenAsync("ses_1", CancellationToken.None);

        await Assert.That(scenario.Pty.Removes).IsEqualTo(1);
        await Assert.That(scenario.Terminal.LastError).IsEqualTo(error);
        await Assert.That(scenario.Changes).IsEqualTo(2);
    }

    [Test]
    public async Task OpenAsync_Should_Close_And_Say_Why_When_Reading_Faults()
    {
        await using var scenario = new TerminalScenario();
        scenario.Pty.Session.ReadFailure = new InvalidOperationException("reader broke");

        await scenario.Terminal.OpenAsync("ses_1", CancellationToken.None);

        await Assert.That(scenario.Pty.Removes).IsEqualTo(1);
        await Assert.That(scenario.Pty.Removed?.Location?.Directory).IsEqualTo(Directory);
        await Assert.That(scenario.Terminal.LastError).IsEqualTo("The terminal stopped: reader broke");
        await Assert.That(scenario.Terminal.IsOpen).IsFalse();
    }

    [Test]
    public async Task CloseAsync_Should_Remove_The_Pty_When_Disposing_Fails()
    {
        await using var scenario = new TerminalScenario();
        await scenario.Terminal.OpenAsync("ses_1", CancellationToken.None);
        scenario.Pty.Session.DisposeFailure = new InvalidOperationException("dispose broke");

        await Assert.That(scenario.Terminal.CloseAsync).Throws<InvalidOperationException>().WithMessage("dispose broke", StringComparison.Ordinal);

        await Assert.That(scenario.Pty.Removes).IsEqualTo(1);
        await Assert.That(scenario.Pty.Removed?.Location?.Directory).IsEqualTo(Directory);
    }

    [Test]
    public async Task CloseAsync_Should_Not_Report_A_Shell_Exit_It_Caused()
    {
        await using var scenario = new TerminalScenario();
        await scenario.Terminal.OpenAsync("ses_1", CancellationToken.None);

        await scenario.Terminal.CloseAsync();
        await scenario.UntilAsync(2);

        await Assert.That(scenario.Terminal.LastError).IsNull();
    }

    [Test]
    public async Task Output_Should_Keep_The_Latest_Text_Without_Control_Sequences()
    {
        await using var scenario = new TerminalScenario();
        var tail = string.Concat(Enumerable.Repeat("0123456789", 2_000));
        await scenario.Pty.Session.Frames.Writer.WriteAsync(new PtyOutputFrame("lost" + tail[..^2]));
        await scenario.Pty.Session.Frames.Writer.WriteAsync(new PtyOutputFrame("\u001b[32m8\u001b[0m\u001b]0;title\u0007\u001b]8;;\u001b\\\u001b7\u001b=\u001bc9"));

        await scenario.Terminal.OpenAsync("ses_1", CancellationToken.None);
        await scenario.UntilAsync(3);

        await Assert.That(scenario.Terminal.Output).IsEqualTo(tail);
    }

    private sealed class TerminalScenario : OpenCodeClient, IAsyncDisposable
    {
        private int _changes;

        public TerminalScenario()
        {
            Terminal = new SessionTerminal(this, NullLogger<SessionTerminal>.Instance);
            Terminal.Changed += (_, _) => Interlocked.Increment(ref _changes);
            PtyCalls = new TerminalPtys(Pty);
        }

        public SessionTerminal Terminal { get; }
        public TerminalPty Pty { get; } = new();
        public TerminalPtys PtyCalls { get; }
        public int Changes => Volatile.Read(ref _changes);
        public TerminalSessions SessionCalls { get; } = new();
        public override SessionsClient Sessions => SessionCalls;
        public override PtysClient Ptys => PtyCalls;

        public async Task UntilAsync(int changes)
        {
            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Terminal.Changed += (_, _) =>
            {
                if (Changes >= changes)
                {
                    reached.TrySetResult();
                }
            };
            if (Changes < changes)
            {
                await reached.Task;
            }
        }

        public async ValueTask DisposeAsync()
        {
            Pty.Session.Frames.Writer.TryComplete();
            await Terminal.CloseAsync();
            Dispose();
        }
    }

    private sealed class TerminalSessions : SessionsClient
    {
        public string? Requested { get; private set; }

        public override SessionClient GetSessionClient(string sessionId)
        {
            Requested = sessionId;
            return new TerminalSession();
        }
    }

    private sealed class TerminalSession : SessionClient
    {
        public override Task<SessionResponse> GetAsync(OpenCodeRequestOptions? requestOptions = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SessionResponse
            {
                Status = 200,
                Session = new SessionInfo
                {
                    Id = "ses_1",
                    ProjectId = "project",
                    Title = "app",
                    Cost = 0,
                    Tokens = new TokenUsageInfo { Input = 0, Output = 0, Reasoning = 0, Cache = new TokenUsageInfoCache { Read = 0, Write = 0 } },
                    Time = new SessionInfoTime { Created = 1, Updated = 1 },
                    Location = new LocationPublicRef { Directory = Directory },
                },
            });
    }

    private sealed class TerminalPtys(TerminalPty pty) : PtysClient
    {
        private int _creates;
        public PtyCreateRequest? Created { get; private set; }
        public int Creates => Volatile.Read(ref _creates);
        public string? ClientFor { get; private set; }
        public TaskCompletionSource? CreateRelease { get; set; }

        /// <summary>A held create completes even when cancelled meanwhile, as a server that already created the PTY does.</summary>
        public override async Task<PtyCreateResponse> CreatePtyAsync(PtyCreateRequest? request = null, OpenCodeRequestOptions? requestOptions = null, CancellationToken cancellationToken = default)
        {
            Created = request;
            _ = Interlocked.Increment(ref _creates);
            if (CreateRelease is { } release)
            {
                await release.Task.WaitAsync(CancellationToken.None);
            }

            return new PtyCreateResponse
            {
                Status = 200,
                Pty = new Pty { Id = "pty_1", Title = "HQ", Command = "pwsh", Args = [], Cwd = Directory, Status = PtyStatus.Running, Pid = 1 },
                Location = new LocationPublicRef { Directory = Directory },
            };
        }

        public override PtyClient GetPtyClient(string ptyId)
        {
            ClientFor = ptyId;
            return pty;
        }
    }

    private sealed class TerminalPty : PtyClient
    {
        private int _removes;
        public TerminalPtySession Session { get; } = new();
        public PtyConnectOptions? Connected { get; private set; }
        public PtyRemoveRequest? Removed { get; private set; }
        public int Removes => Volatile.Read(ref _removes);
        public TaskCompletionSource? RemoveRelease { get; set; }
        public Exception? RemoveFailure { get; set; }
        public Exception? ConnectFailure { get; set; }

        public override Task<PtySession> ConnectAsync(PtyConnectOptions? options = null, CancellationToken cancellationToken = default)
        {
            Connected = options;
            cancellationToken.ThrowIfCancellationRequested();
            return ConnectFailure is { } failure ? Task.FromException<PtySession>(failure) : Task.FromResult<PtySession>(Session);
        }

        public override async Task<PtyRemoveResponse> RemoveAsync(PtyRemoveRequest? request = null, OpenCodeRequestOptions? requestOptions = null, CancellationToken cancellationToken = default)
        {
            Removed = request;
            var removes = Interlocked.Increment(ref _removes);
            if (removes == 1 && RemoveRelease is { } release)
            {
                await release.Task.WaitAsync(cancellationToken);
            }

            if (RemoveFailure is { } failure)
            {
                throw failure;
            }

            return new PtyRemoveResponse { Status = 200 };
        }
    }

    private sealed class TerminalPtySession : PtySession
    {
        public Channel<PtyFrame> Frames { get; } = Channel.CreateUnbounded<PtyFrame>();
        public bool Disposed { get; private set; }
        public List<string> Submitted { get; } = [];
        public Exception? SubmitFailure { get; set; }
        public Exception? ReadFailure { get; set; }
        public Exception? DisposeFailure { get; set; }

        public override IAsyncEnumerable<PtyFrame> ReadAsync(CancellationToken cancellationToken = default) =>
            ReadFailure is { } failure ? throw failure : Frames.Reader.ReadAllAsync(cancellationToken);

        public override Task SubmitAsync(string line, CancellationToken cancellationToken = default)
        {
            Submitted.Add(line);
            return SubmitFailure is { } failure ? Task.FromException(failure) : Task.CompletedTask;
        }

        public override async ValueTask DisposeAsync()
        {
            Disposed = true;
            await base.DisposeAsync();
            // A failing disposal throws before it ends the read, so no reader wakes to remove the PTY instead.
            if (DisposeFailure is { } failure)
            {
                throw failure;
            }

            Frames.Writer.TryComplete();
        }
    }
}
