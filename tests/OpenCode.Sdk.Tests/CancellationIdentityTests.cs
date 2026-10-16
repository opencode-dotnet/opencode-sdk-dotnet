using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Support;
using Testably.Abstractions;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests;

/// <summary>One caller identity through each public cancellation path, not every operation.</summary>
[NotInParallel]
public sealed class CancellationIdentityTests
{
    private const string SessionId = "ses_9";
    private const string PtyId = "pty_persistent_7";
    private const string Input = "echo identity";
    private const string HandoffPath = "/api/experimental/persistent-pty/handoff";
    private const string ShutdownPath = "/api/experimental/persistent-pty/shutdown";
    private const string ServicePassword = "synthetic-identity-password";
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(1);

    public static IEnumerable<Func<(string Row, string Variant)>> CancellationCases() =>
        Cases().Select(static entry => (Func<(string, string)>)(() => (entry.Row, entry.Variant)));

    private static IEnumerable<DoorCase> Cases()
    {
        yield return new("http", "headers", caller => AtHeadersAsync(
            static (client, token) => client.Server.GetInfoAsync(cancellationToken: token), caller));
        yield return new("http", "buffered-body", caller => AtBodyAsync(upload: false, caller));
        yield return new("http", "raw-upload", caller => AtBodyAsync(upload: true, caller));
        yield return new("pty-resize-http", "headers", caller => AtHeadersAsync(
            static (client, token) => client.Ptys.GetPtyClient(PtyId).UpdateAsync(
                new PtyUpdateRequest { Size = new PtySize { Cols = 100, Rows = 30 } }, cancellationToken: token), caller));
        yield return new("pty-connect", "upgrade", caller => AtHeadersAsync((client, token) => ConnectAndDisposeAsync(client, persistent: false, token), caller));
        yield return new("persistent-connect", "upgrade", caller => AtHeadersAsync((client, token) => ConnectAndDisposeAsync(client, persistent: true, token), caller));
        yield return new("persistent-connect", "awaiting-attached", AtAttachAsync);
        yield return new("start", "child-before-readiness", AtStartAsync);
        yield return new("discover", "authenticated-probe", caller => AtServiceRequestAsync("discover", "probe", caller));
        yield return new("ensure", "handoff-ticket", caller => AtServiceRequestAsync("ensure", "ticket", caller));
        yield return new("ensure", "shutdown-fallback", caller => AtServiceRequestAsync("ensure", "fallback", caller));
        yield return new("stop", "daemon-shutdown", caller => AtServiceRequestAsync("stop", "shutdown", caller));

        foreach (var entry in new[] { "method", "enumeration", "both" })
        {
            yield return new("events", "first-open/" + entry, caller => AtEventWaitAsync("open", entry, caller));
            yield return new("events", "live-read/" + entry, caller => AtStreamReadAsync(events: true, entry, caller));
            yield return new("events", "reconnect-delay/" + entry, caller => AtEventWaitAsync("delay", entry, caller));
            yield return new("other-sse", "headers/" + entry, caller => AtHeadersAsync(
                (client, token) => PullAsync(client.Sessions.GetSessionClient(SessionId).ReadLogAsync(
                    cancellationToken: MethodToken(entry, token)), EnumerationToken(entry, token)), caller));
            yield return new("other-sse", "live-read/" + entry, caller => AtStreamReadAsync(events: false, entry, caller));
            yield return new("cursor-items", "page-request/" + entry, caller => AtCursorRequestAsync(pages: false, entry, caller));
            yield return new("cursor-items", "buffered-item/" + entry, caller => BetweenItemsAsync(entry, caller));
            yield return new("cursor-pages", "page-request/" + entry, caller => AtCursorRequestAsync(pages: true, entry, caller));
            yield return new("pty-read", entry, caller => AtTerminalReadAsync(persistent: false, entry, caller));
            yield return new("persistent-read", entry, caller => AtTerminalReadAsync(persistent: true, entry, caller));
        }

        foreach (var row in new[] { "pty-write", "pty-submit", "persistent-write", "persistent-submit", "persistent-resize" })
        {
            yield return new(row, "physical-send", caller => AtTerminalSendAsync(row, queued: false, caller));
            yield return new(row, "queued-admission", caller => AtTerminalSendAsync(row, queued: true, caller));
        }
    }

    [Test]
    [MethodDataSource(nameof(CancellationCases))]
    public async Task PublicDoor_Should_Preserve_The_Caller_Token_When_Canceled_Mid_Call(string row, string variant)
    {
        using var caller = new CancellationTokenSource();
        var entry = Cases().Single(entry => entry.Row == row && entry.Variant == variant);

        var failure = await entry.Invoke(caller);

        // Compare identity directly: formatting a mismatched, disposed BCL-linked token can itself
        // throw from CancellationToken.WaitHandle and hide the public-door contract failure.
        await Assert.That(failure.CancellationToken == caller.Token).IsTrue()
            .Because($"{row}/{variant} must carry the one caller token, not a linked I/O token");
    }

    private static CancellationToken MethodToken(string entry, CancellationToken token) =>
        entry == "enumeration" ? CancellationToken.None : token;

    private static CancellationToken EnumerationToken(string entry, CancellationToken token) =>
        entry == "method" ? CancellationToken.None : token;

    private static async Task PullAsync<T>(IAsyncEnumerable<T> stream, CancellationToken enumerationToken)
    {
        await using var enumerator = stream.GetAsyncEnumerator(enumerationToken);
        _ = await enumerator.MoveNextAsync();
    }

    /// <summary>
    /// Observes the caller's cancellation. A read is awaited as the ValueTask a consumer's await foreach
    /// awaits: on .NET Framework, AsTask() rethrows its cancellation as a new TaskCanceledException
    /// without the caller's token.
    /// </summary>
    private static async Task<OperationCanceledException> CancellationAsync(Func<Task> operation)
    {
        var failure = await Assert.That(operation().WaitAsync(HangGuard.Bound)).Throws<OperationCanceledException>();
        return failure!;
    }

    private static async Task<OperationCanceledException> AtHeadersAsync(
        Func<OpenCodeClient, CancellationToken, Task> invoke, CancellationTokenSource caller)
    {
        using var listener = new HeldHeadersListener();
        var observed = CancellationAsync(async () =>
        {
            using var client = new OpenCodeClient(new OpenCodeClientOptions { Endpoint = listener.Endpoint });
            await invoke(client, caller.Token);
        });
        try
        {
            using var peer = await listener.AcceptAsync().WaitAsync(HangGuard.Bound, CancellationToken.None);
            using var reader = new StreamReader(peer.GetStream(), Encoding.ASCII, detectEncodingFromByteOrderMarks: false);
            var firstLine = await reader.ReadLineAsync().WaitAsync(HangGuard.Bound, CancellationToken.None);
            await Assert.That(firstLine).IsNotNull();
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync().WaitAsync(HangGuard.Bound, CancellationToken.None)))
            {
                // Complete request headers witnessed; the peer deliberately sends no response.
            }

            await caller.CancelOnWorkerAsync();
            return await observed;
        }
        finally
        {
            await caller.CancelOnWorkerAsync();
            _ = await observed;
        }
    }

    private static async Task<OperationCanceledException> AtBodyAsync(bool upload, CancellationTokenSource caller)
    {
        using var body = new GatedStream(Encoding.UTF8.GetBytes(Input), chunkCount: 1);
        using var handler = new RecordingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(body),
        });
        using var http = new HttpClient(handler);
        using var client = new OpenCodeClient(http, new OpenCodeClientOptions { Endpoint = PipelineFactory.Endpoint });
        var observed = CancellationAsync(async () =>
        {
            if (upload)
            {
                _ = await client.Experimental.WriteFileAsync(new ExperimentalFsWriteRequest { Path = "/identity.txt" }, body, cancellationToken: caller.Token);
            }
            else
            {
                _ = await client.Server.GetInfoAsync(cancellationToken: caller.Token);
            }
        });
        try
        {
            await body.WaitForReadAsync(HangGuard.Bound);
        }
        finally
        {
            await caller.CancelOnWorkerAsync();
            _ = await observed;
        }

        return await observed;
    }

    private static async Task<OperationCanceledException> AtEventWaitAsync(string phase, string entry, CancellationTokenSource caller)
    {
        using var server = new ScriptedEventServer(_ => phase == "open"
            ? ScriptedEventServer.Silence
            : ScriptedEventServer.StreamOf(WireBodyData.Frames(ScriptedEventServer.Connected), ends: true));
        await using var events = server.Events.SubscribeAsync(MethodToken(entry, caller.Token))
            .GetAsyncEnumerator(EnumerationToken(entry, caller.Token));
        if (phase == "delay")
        {
            await Assert.That(await PullAsync().WaitAsync(HangGuard.Bound, CancellationToken.None)).IsTrue();
        }

        var observed = CancellationAsync(async () => _ = await events.MoveNextAsync());
        try
        {
            if (phase == "open")
            {
                await server.WaitForRequestAsync(1, HangGuard.Bound);
            }
            else
            {
                await server.Time.WaitForArmedAsync(ReconnectDelay, HangGuard.Bound);
            }
        }
        finally
        {
            await caller.CancelOnWorkerAsync();
            _ = await observed;
        }

        return await observed;

        async Task<bool> PullAsync() => await events.MoveNextAsync();
    }

    private static async Task<OperationCanceledException> AtStreamReadAsync(bool events, string entry, CancellationTokenSource caller)
    {
        var frame = WireBodyData.Frames(events ? ScriptedEventServer.Connected : WireBodyData.LogSyncedEvent);
        using var body = new GatedStream(Encoding.UTF8.GetBytes(frame + frame), chunkCount: 2);
        using var handler = new RecordingHttpHandler(_ => StreamResponse(body));
        using var http = new HttpClient(handler);
        using var pipeline = PipelineFactory.Create(http, time: new ManualTimeProvider());
        // These are the actual public clients; only their network and clock seams are substituted.
        if (events)
        {
            return await AtSecondReadAsync(new EventsClient(pipeline).SubscribeAsync(MethodToken(entry, caller.Token)), entry, body, caller);
        }

        return await AtSecondReadAsync(new SessionClient(pipeline, SessionId).ReadLogAsync(
            cancellationToken: MethodToken(entry, caller.Token)), entry, body, caller);
    }

    private static HttpResponseMessage StreamResponse(Stream body)
    {
        var content = new StreamContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static async Task<OperationCanceledException> AtSecondReadAsync<T>(
        IAsyncEnumerable<T> stream, string entry, GatedStream body, CancellationTokenSource caller)
    {
        await using var enumerator = stream.GetAsyncEnumerator(EnumerationToken(entry, caller.Token));
        var first = PendingNext(enumerator);
        bool hasFirst;
        try
        {
            await body.WaitForReadAsync(HangGuard.Bound);
        }
        finally
        {
            body.Release();
            hasFirst = await first.WaitAsync(HangGuard.Bound, CancellationToken.None);
        }

        await Assert.That(hasFirst).IsTrue();
        var observed = CancellationAsync(async () => _ = await enumerator.MoveNextAsync());
        try
        {
            await body.WaitForReadAsync(HangGuard.Bound);
        }
        finally
        {
            await caller.CancelOnWorkerAsync();
            _ = await observed;
        }

        return await observed;
    }

    private static async Task<bool> PendingNext<T>(IAsyncEnumerator<T> enumerator) => await enumerator.MoveNextAsync();

    private static Task<OperationCanceledException> AtCursorRequestAsync(bool pages, string entry, CancellationTokenSource caller) =>
        AtHeadersAsync((client, token) =>
        {
            var sequence = client.Sessions.GetSessionClient(SessionId).EnumerateMessagesAsync(cancellationToken: MethodToken(entry, token));
            return pages
                ? PullAsync(sequence.Pages, EnumerationToken(entry, token))
                : PullAsync(sequence, EnumerationToken(entry, token));
        }, caller);

    private static async Task<OperationCanceledException> BetweenItemsAsync(string entry, CancellationTokenSource caller)
    {
        var page = new FixtureLoader().LoadJson("Serialization.stream-message-list-page.json");
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, page);
        await using var items = scenario.Client.Sessions.GetSessionClient(SessionId)
            .EnumerateMessagesAsync(cancellationToken: MethodToken(entry, caller.Token))
            .GetAsyncEnumerator(EnumerationToken(entry, caller.Token));
        try
        {
            await Assert.That(await items.MoveNextAsync()).IsTrue();
            await Assert.That(scenario.Requests.Count).IsEqualTo(1);
        }
        finally
        {
            await caller.CancelOnWorkerAsync();
        }

        // A yielded first item witnesses the real page traversal. The second buffered item must
        // not hide cancellation, and no continuation request is needed to observe it.
        var failure = await CancellationAsync(async () => _ = await items.MoveNextAsync());
        await Assert.That(scenario.Requests.Count).IsEqualTo(1);
        return failure;
    }

    /// <summary>
    /// Cancels the attachment PersistentPtyClient.ConnectAsync runs with the caller's token once the
    /// upgrade completes; the parked receive witnesses that it is waiting for its frame.
    /// </summary>
    private static async Task<OperationCanceledException> AtAttachAsync(CancellationTokenSource caller)
    {
        using var socket = new ScriptedTerminalWebSocket().Parking();
        return await CancelAtParkedReadAsync(async () =>
        {
            await using var session = await PersistentPtySession.AttachAsync(socket, PtyId, caller.Token);
        }, socket, caller);
    }

    private static async Task ConnectAndDisposeAsync(OpenCodeClient client, bool persistent, CancellationToken token)
    {
        if (persistent)
        {
            await using var session = await client.PersistentPtys.GetPersistentPtyClient(PtyId).ConnectAsync(cancellationToken: token);
        }
        else
        {
            await using var session = await client.Ptys.GetPtyClient(PtyId).ConnectAsync(cancellationToken: token);
        }
    }

    private static async Task<OperationCanceledException> AtTerminalReadAsync(bool persistent, string entry, CancellationTokenSource caller)
    {
        using var socket = new ScriptedTerminalWebSocket();
        if (persistent)
        {
            _ = socket.Text(PersistentPtyFrameData.AttachedJson).Parking();
            await using var session = await PersistentPtySession.AttachAsync(socket, PtyId, CancellationToken.None);
            return await AtParkedReadAsync(session.ReadAsync(MethodToken(entry, caller.Token)), entry, socket, caller);
        }

        _ = socket.Parking();
        await using var normal = new PtySession(socket);
        return await AtParkedReadAsync(normal.ReadAsync(MethodToken(entry, caller.Token)), entry, socket, caller);
    }

    private static async Task<OperationCanceledException> AtParkedReadAsync<T>(
        IAsyncEnumerable<T> stream, string entry, ScriptedTerminalWebSocket socket, CancellationTokenSource caller)
    {
        await using var enumerator = stream.GetAsyncEnumerator(EnumerationToken(entry, caller.Token));
        return await CancelAtParkedReadAsync(async () => _ = await enumerator.MoveNextAsync(), socket, caller);
    }

    private static async Task<OperationCanceledException> CancelAtParkedReadAsync(
        Func<Task> operation, ScriptedTerminalWebSocket socket, CancellationTokenSource caller)
    {
        var observed = CancellationAsync(operation);
        try
        {
            Task[] signals = [socket.Parked];
            await Task.WhenAll(signals).WaitAsync(HangGuard.Bound, CancellationToken.None);
        }
        finally
        {
            await caller.CancelOnWorkerAsync();
            _ = await observed;
        }

        return await observed;
    }

    private static async Task<OperationCanceledException> AtTerminalSendAsync(string row, bool queued, CancellationTokenSource caller)
    {
        using var socket = new ScriptedTerminalWebSocket().GatingSends();
        if (row.StartsWith("persistent-", StringComparison.Ordinal))
        {
            _ = socket.Text(PersistentPtyFrameData.AttachedJson);
            await using var session = await PersistentPtySession.AttachAsync(socket, PtyId, CancellationToken.None);
            var failure = await AtSendGateAsync(token => row switch
            {
                "persistent-write" => session.WriteAsync(PersistentPtyFrameData.Output(Input), token),
                "persistent-submit" => session.SubmitAsync(Input, token),
                "persistent-resize" => session.ResizeAsync(100, 30, token),
                _ => throw new InvalidOperationException(row),
            }, queued ? () => session.WriteAsync(PersistentPtyFrameData.Output(Input), CancellationToken.None) : null, socket, caller);
            if (row == "persistent-resize" && queued)
            {
                await session.WriteAsync(PersistentPtyFrameData.Output(Input), CancellationToken.None);
                await Assert.That(socket.SentMessages[^1]).IsEquivalentTo(
                    PersistentPtyFrameData.Framed(1, 80, 24, PersistentPtyFrameData.Output(Input)), CollectionOrdering.Matching);
            }

            return failure;
        }

        await using var normal = new PtySession(socket);
        return await AtSendGateAsync(token => row == "pty-write"
            ? normal.WriteAsync(Input, token) : normal.SubmitAsync(Input, token),
            queued ? () => normal.WriteAsync(Input, CancellationToken.None) : null, socket, caller);
    }

    private static async Task<OperationCanceledException> AtSendGateAsync(
        Func<CancellationToken, Task> send, Func<Task>? occupy, ScriptedTerminalWebSocket socket, CancellationTokenSource caller)
    {
        var blocker = Task.CompletedTask;
        Task<OperationCanceledException>? observed = null;
        try
        {
            if (occupy is not null)
            {
                blocker = occupy();
                Task[] occupied = [socket.SendEntered];
                await Task.WhenAll(occupied).WaitAsync(HangGuard.Bound, CancellationToken.None);
            }

            observed = CancellationAsync(() => send(caller.Token));
            Task[] signals = [socket.SendEntered];
            await Task.WhenAll(signals).WaitAsync(HangGuard.Bound, CancellationToken.None);
            await Assert.That(socket.SentMessages.Count).IsEqualTo(1);
            await caller.CancelOnWorkerAsync();
            return await observed;
        }
        finally
        {
            await caller.CancelOnWorkerAsync();
            socket.ReleaseSends();
            await blocker.WaitAsync(HangGuard.Bound, CancellationToken.None);
            if (observed is not null)
            {
                _ = await observed;
            }
        }
    }

    private static async Task<OperationCanceledException> AtStartAsync(CancellationTokenSource caller)
    {
        var fileSystem = new RealFileSystem();
        using var root = new TestRunRoot(fileSystem);
        var isolation = ServerIsolation.For(fileSystem, root.Path);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture);
        var output = new OpenCodeServerOutput();
        var observed = CancellationAsync(async () =>
        {
            await using var server = await OpenCodeServer.StartAsync(new OpenCodeServerOptions
            {
                // A socket line witnesses actual child execution without polling its output or
                // waiting on a clock. The child never writes a readiness line to stdout.
                Command = ["bun", "-e", "require('net').connect(" + port + ", '127.0.0.1', function() { this.write(process.pid + '\\n'); }); process.stdin.resume();"],
                WorkingDirectory = root.Path,
                Environment = isolation.Environment,
                ReadinessTimeout = Timeout.InfiniteTimeSpan,
                GracefulShutdownTimeout = TimeSpan.Zero,
                Output = output,
            }, caller.Token);
        });
        var processId = 0;
        try
        {
            using var peer = await listener.AcceptTcpClientAsync().WaitAsync(HangGuard.Bound, CancellationToken.None);
            using var reader = new StreamReader(peer.GetStream(), Encoding.ASCII);
            var line = await reader.ReadLineAsync().WaitAsync(HangGuard.Bound, CancellationToken.None)
                ?? throw new InvalidOperationException("The child closed its witness socket without reporting its pid.");
            processId = int.Parse(line, CultureInfo.InvariantCulture);
            await Assert.That(ProcessObservation.IsRunning(processId)).IsTrue();
            await caller.CancelOnWorkerAsync();
            var failure = await observed;
            await Assert.That(ProcessObservation.IsRunning(processId)).IsFalse();
            return failure;
        }
        finally
        {
            await caller.CancelOnWorkerAsync();
            try
            {
                _ = await observed;
            }
            finally
            {
                listener.Stop();
#if NET
                listener.Dispose();
#endif
                ProcessObservation.KillIfRunning(processId);
            }
        }
    }

    private static async Task<OperationCanceledException> AtServiceRequestAsync(string row, string stage, CancellationTokenSource caller)
    {
        var fileSystem = new RealFileSystem();
        using var root = new TestRunRoot(fileSystem);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var heldPath = (row, stage) switch
        {
            ("discover", _) => "/api/info",
            (_, "ticket") => HandoffPath,
            _ => ShutdownPath,
        };
        await using var daemon = LoopbackHttpServer.Start(path =>
        {
            if (path == heldPath)
            {
                _ = entered.TrySetResult(true);
                return new LoopbackHttpResponse
                {
                    StatusCode = HttpStatusCode.OK,
                    ContentType = "application/json",
                    Body = ServiceInfoBodyData.Ready,
                    KeepOpen = true,
                };
            }

            return new LoopbackHttpResponse
            {
                StatusCode = path == HandoffPath ? HttpStatusCode.NotFound : HttpStatusCode.OK,
                ContentType = "application/json",
                Body = ServiceInfoBodyData.Ready,
            };
        });
        var registrationPath = fileSystem.Path.Combine(root.Path, "service.json");
        var document = ServiceRegistrationDocument.Compose("srv_identity", ServiceInfoBodyData.Version,
            daemon.Endpoint, ServiceInfoBodyData.Pid, ServicePassword);
        var registrationFiles = new TestablyServiceFileSystem(fileSystem);
        await Assert.That(await registrationFiles.TryCreateExclusiveAsync(
            registrationPath, Encoding.UTF8.GetBytes(document), CancellationToken.None)).IsTrue();
        var observed = CancellationAsync(() => InvokeServiceAsync(row, registrationPath, caller.Token));
        try
        {
            Task[] signals = [entered.Task];
            await Task.WhenAll(signals).WaitAsync(HangGuard.Bound, CancellationToken.None);
            await caller.CancelOnWorkerAsync();
            var failure = await observed;
            var request = daemon.Requests.Single(request => request.Path == heldPath);
            await Assert.That(request.Headers["Authorization"]).IsEqualTo(
                "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("opencode:" + ServicePassword)));
            string[] expectedPaths = (row, stage) switch
            {
                ("discover", _) => ["/api/info"],
                (_, "fallback") => ["/api/info", HandoffPath, ShutdownPath],
                _ => ["/api/info", heldPath],
            };
            await Assert.That(daemon.RequestPaths).IsEquivalentTo(expectedPaths, CollectionOrdering.Matching);
            var retained = await registrationFiles.TryReadAllBytesAsync(registrationPath, CancellationToken.None);
            await Assert.That(retained).IsEquivalentTo(Encoding.UTF8.GetBytes(document), CollectionOrdering.Matching);
            await Assert.That(fileSystem.Directory.GetFiles(root.Path, "*" + ServicePtyHandoff.SidecarSuffix)).IsEmpty();
            return failure;
        }
        finally
        {
            // The response stays held until cancellation settled: Ensure cannot reach termination
            // or recruit a contender, and Stop cannot reach its first process signal.
            await caller.CancelOnWorkerAsync();
            try
            {
                _ = await observed;
            }
            finally
            {
                daemon.ReleaseResponses();
            }
        }
    }

    private static async Task InvokeServiceAsync(string row, string registrationPath, CancellationToken caller)
    {
        if (row == "stop")
        {
            await OpenCodeServer.StopAsync(new OpenCodeServerStopOptions { RegistrationFilePath = registrationPath }, caller);
        }
        else if (row == "discover")
        {
            await using var server = await OpenCodeServer.DiscoverAsync(
                new OpenCodeServerDiscoverOptions { RegistrationFilePath = registrationPath }, caller);
        }
        else
        {
            await using var server = await OpenCodeServer.EnsureAsync(new OpenCodeServerEnsureOptions
            {
                RegistrationFilePath = registrationPath,
                ExpectedVersion = "0.0.0-other",
                VersionPolicy = OpenCodeServerVersionPolicy.Replace,
                Command = ["bun", "--version"],
            }, caller);
        }
    }

    [Test]
    public async Task PublicAsyncMembers_Should_Map_To_A_Cancellation_Identity_Row()
    {
        var rows = Cases().Select(static entry => entry.Row).ToHashSet(StringComparer.Ordinal);
        var members = typeof(OpenCodeClient).Assembly.GetExportedTypes()
            .SelectMany(static type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Select(MemberSignature.From)
            .Where(CancellationDoorClassifier.Selects)
            .ToArray();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in members)
        {
            var row = CancellationDoorClassifier.RequireRow(member, rows);
            _ = used.Add(row);
            if (row == "cursor-items")
            {
                // Pages has no token-taking public member; every cursor factory requires this edge.
                await Assert.That(rows).Contains("cursor-pages");
                _ = used.Add("cursor-pages");
            }
        }

        await Assert.That(members).IsNotEmpty();
        await Assert.That(rows.Except(used, StringComparer.Ordinal).ToArray()).IsEmpty();
        TestContext.Current!.Output.WriteLine($"G1: {members.Length} signatures classified across {rows.Count} executable row kinds; no deferrals.");
    }

    [Test]
    [Arguments("unknown-return")]
    [Arguments("unknown-family")]
    [Arguments("missing-row")]
    public async Task CancellationDoorClassifier_Should_Reject_An_Unmapped_Signature_Or_A_Missing_Row(string challenge)
    {
        var known = MemberSignature.From(typeof(ServerClient).GetMethod(nameof(ServerClient.GetInfoAsync))!);
        var member = challenge switch
        {
            "unknown-return" => known with { ReturnType = typeof(Task<string>) },
            "unknown-family" => known with { DeclaringType = typeof(string) },
            "missing-row" => known,
            _ => throw new InvalidOperationException(challenge),
        };
        var rows = challenge == "missing-row" ? new HashSet<string>(StringComparer.Ordinal)
            : Cases().Select(static entry => entry.Row).ToHashSet(StringComparer.Ordinal);

        var failure = Assert.Throws<InvalidOperationException>(() => _ = CancellationDoorClassifier.RequireRow(member, rows));

        await Assert.That(failure.Message).Contains(member.ToString());
    }

    private sealed record DoorCase(string Row, string Variant, Func<CancellationTokenSource, Task<OperationCanceledException>> Invoke);

    private sealed record MemberSignature(Type DeclaringType, string Name, Type ReturnType, IReadOnlyList<Type> Parameters, bool IsStatic)
    {
        public static MemberSignature From(MethodInfo method) => new(method.DeclaringType!, method.Name, method.ReturnType,
            [.. method.GetParameters().Select(static parameter => parameter.ParameterType)], method.IsStatic);

        public bool Is(Type owner, string name, Type result, params Type[] parameters) =>
            DeclaringType == owner && Name == name && ReturnType == result &&
            IsStatic == (owner == typeof(OpenCodeServer)) && Parameters.SequenceEqual(parameters);

        public override string ToString() =>
            $"{ReturnType} {DeclaringType}.{Name}({string.Join(", ", Parameters.Select(static type => type.ToString()))})";
    }

    private static class CancellationDoorClassifier
    {
        private static readonly HashSet<Type> ClientFamilies =
        [
            typeof(OpenCodeClient),
            typeof(AgentsClient), typeof(CommandsClient), typeof(ConfigClient), typeof(CredentialsClient),
            typeof(DebugClient), typeof(EventsClient), typeof(ExperimentalClient), typeof(FileSystemClient),
            typeof(FormsClient), typeof(IntegrationClient), typeof(IntegrationsClient), typeof(LanguageModelsClient),
            typeof(McpServersClient), typeof(PermissionsClient), typeof(PersistentPtyClient), typeof(PersistentPtysClient),
            typeof(PluginsClient), typeof(ProjectsClient), typeof(ProvidersClient), typeof(PtyClient), typeof(PtysClient),
            typeof(ReferencesClient), typeof(RpcClient), typeof(ServerClient), typeof(SessionClient), typeof(SessionsClient),
            typeof(ShellClient), typeof(ShellsClient), typeof(SkillsClient), typeof(VcsClient), typeof(WebsearchClient), typeof(WorktreesClient),
        ];

        public static bool Selects(MemberSignature member) => member.Parameters.Contains(typeof(CancellationToken)) &&
            (IsAsyncShape(member.ReturnType) || member.Name.EndsWith("Async", StringComparison.Ordinal));

        public static string RequireRow(MemberSignature member, IReadOnlyCollection<string> rows)
        {
            var row = Map(member) ?? throw new InvalidOperationException($"Unclassified cancellation signature: {member}");
            if (!rows.Contains(row, StringComparer.Ordinal))
            {
                throw new InvalidOperationException($"Missing executable row '{row}' for {member}");
            }

            return row;
        }

        private static string? Map(MemberSignature member) => member switch
        {
            _ when member.Is(typeof(OpenCodeServer), nameof(OpenCodeServer.StartAsync), typeof(Task<OpenCodeServer>),
                typeof(OpenCodeServerOptions), typeof(CancellationToken)) => "start",
            _ when member.Is(typeof(OpenCodeServer), nameof(OpenCodeServer.DiscoverAsync), typeof(Task<OpenCodeServer>),
                typeof(OpenCodeServerDiscoverOptions), typeof(CancellationToken)) => "discover",
            _ when member.Is(typeof(OpenCodeServer), nameof(OpenCodeServer.EnsureAsync), typeof(Task<OpenCodeServer>),
                typeof(OpenCodeServerEnsureOptions), typeof(CancellationToken)) => "ensure",
            _ when member.Is(typeof(OpenCodeServer), nameof(OpenCodeServer.StopAsync), typeof(Task),
                typeof(OpenCodeServerStopOptions), typeof(CancellationToken)) => "stop",
            _ when member.Is(typeof(PtyClient), nameof(PtyClient.ConnectAsync), typeof(Task<PtySession>),
                typeof(PtyConnectOptions), typeof(CancellationToken)) => "pty-connect",
            _ when member.Is(typeof(PersistentPtyClient), nameof(PersistentPtyClient.ConnectAsync), typeof(Task<PersistentPtySession>),
                typeof(PersistentPtyConnectOptions), typeof(CancellationToken)) => "persistent-connect",
            _ when member.Is(typeof(PtySession), nameof(PtySession.ReadAsync), typeof(IAsyncEnumerable<PtyFrame>), typeof(CancellationToken)) => "pty-read",
            _ when member.Is(typeof(PersistentPtySession), nameof(PersistentPtySession.ReadAsync), typeof(IAsyncEnumerable<PersistentPtyFrame>),
                typeof(CancellationToken)) => "persistent-read",
            _ when member.Is(typeof(PtySession), nameof(PtySession.WriteAsync), typeof(Task), typeof(string), typeof(CancellationToken)) => "pty-write",
            _ when member.Is(typeof(PtySession), nameof(PtySession.SubmitAsync), typeof(Task), typeof(string), typeof(CancellationToken)) => "pty-submit",
            _ when member.Is(typeof(PersistentPtySession), nameof(PersistentPtySession.WriteAsync), typeof(Task),
                typeof(ReadOnlyMemory<byte>), typeof(CancellationToken)) => "persistent-write",
            _ when member.Is(typeof(PersistentPtySession), nameof(PersistentPtySession.SubmitAsync), typeof(Task),
                typeof(string), typeof(CancellationToken)) => "persistent-submit",
            _ when member.Is(typeof(PersistentPtySession), nameof(PersistentPtySession.ResizeAsync), typeof(Task),
                typeof(int), typeof(int), typeof(CancellationToken)) => "persistent-resize",
            _ when IsCursorEnumerator(member) => "cursor-items",
            _ when ClientFamilies.Contains(member.DeclaringType) && IsGeneric(member.ReturnType, typeof(CursorSequence<,>)) => "cursor-items",
            _ when member.Is(typeof(EventsClient), nameof(EventsClient.SubscribeAsync), typeof(IAsyncEnumerable<IEvent>), typeof(CancellationToken)) => "events",
            _ when ClientFamilies.Contains(member.DeclaringType) && IsGeneric(member.ReturnType, typeof(IAsyncEnumerable<>)) => "other-sse",
            _ when member.Is(typeof(PtyClient), nameof(PtyClient.UpdateAsync), typeof(Task<PtyUpdateResponse>),
                typeof(PtyUpdateRequest), typeof(OpenCodeRequestOptions), typeof(CancellationToken)) => "pty-resize-http",
            _ when ClientFamilies.Contains(member.DeclaringType) && IsGeneric(member.ReturnType, typeof(Task<>)) &&
                typeof(OpenCodeResponse).IsAssignableFrom(member.ReturnType.GetGenericArguments()[0]) => "http",
            _ => null,
        };

        private static bool IsCursorEnumerator(MemberSignature member) =>
            IsGeneric(member.DeclaringType, typeof(CursorSequence<,>)) && !member.IsStatic &&
            member.Name == nameof(IAsyncEnumerable<int>.GetAsyncEnumerator) &&
            IsGeneric(member.ReturnType, typeof(IAsyncEnumerator<>)) &&
            member.ReturnType.GetGenericArguments()[0] == member.DeclaringType.GetGenericArguments()[1] &&
            member.Parameters.SequenceEqual([typeof(CancellationToken)]);

        private static bool IsGeneric(Type type, Type definition) => type.IsGenericType && type.GetGenericTypeDefinition() == definition;

        private static bool IsAsyncShape(Type type) => type == typeof(Task) || type == typeof(ValueTask) ||
            (type.IsGenericType && new[]
            {
                typeof(Task<>), typeof(ValueTask<>), typeof(IAsyncEnumerable<>), typeof(CursorSequence<,>), typeof(IAsyncEnumerator<>),
            }.Contains(type.GetGenericTypeDefinition()));
    }
}
