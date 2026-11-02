using System.Buffers;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Transport;

public sealed class PipelineExchangeTests
{
    [Test]
    public async Task SendThroughPoliciesAsync_Should_Send_Each_Bodys_Exact_Bytes_On_One_Exchange()
    {
        using var handler = new RecordingHttpHandler();
        using var httpClient = new HttpClient(handler);
        using var transport = new TransportPolicy(httpClient, ownsHttpClient: false);
        var exchange = CreateExchange([transport]);
        var first = new SessionPromptRequest { Text = "first" };
        var second = new SessionPromptRequest { Text = "other 😀" };
        using var firstMessage = CreateMessage(exchange, first);
        await exchange.SendThroughPoliciesAsync(firstMessage);
        using var secondMessage = CreateMessage(exchange, second);
        await exchange.SendThroughPoliciesAsync(secondMessage);

        await Assert.That(handler.Requests.Count).IsEqualTo(2);
        await Assert.That(handler.Requests[0].BodyBytes!)
            .IsEquivalentTo(JsonSerializer.SerializeToUtf8Bytes(first, OpenCodeJsonContext.Default.SessionPromptRequest), CollectionOrdering.Matching);
        await Assert.That(handler.Requests[1].BodyBytes!)
            .IsEquivalentTo(JsonSerializer.SerializeToUtf8Bytes(second, OpenCodeJsonContext.Default.SessionPromptRequest), CollectionOrdering.Matching);
    }

    [Test]
    public async Task CreateMessage_Should_Keep_Equal_Length_Bodies_Separate_Before_Either_Is_Read()
    {
        var exchange = CreateExchange([]);
        var first = new SessionPromptRequest { Text = "first" };
        var second = new SessionPromptRequest { Text = "other" };
        using var firstMessage = CreateMessage(exchange, first);
        using var secondMessage = CreateMessage(exchange, second);

        await Assert.That(firstMessage.Request.Content).IsTypeOf<PooledJsonContent>();
        await Assert.That(secondMessage.Request.Content).IsTypeOf<PooledJsonContent>();
        await Assert.That(await firstMessage.Request.Content!.ReadAsByteArrayAsync())
            .IsEquivalentTo(JsonSerializer.SerializeToUtf8Bytes(first, OpenCodeJsonContext.Default.SessionPromptRequest), CollectionOrdering.Matching);
        await Assert.That(await secondMessage.Request.Content!.ReadAsByteArrayAsync())
            .IsEquivalentTo(JsonSerializer.SerializeToUtf8Bytes(second, OpenCodeJsonContext.Default.SessionPromptRequest), CollectionOrdering.Matching);
    }

    [Test]
    public async Task CreateMessage_Should_Keep_Concurrent_Serializations_Separate()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new JsonSerializerOptions { TypeInfoResolver = OpenCodeJsonContext.Default };
        var typeInfo = JsonMetadataServices.CreateValueInfo<string>(options, new BlockingConverter(entered, release));
        var exchange = CreateExchange([]);
        using var warm = CreateMessage(exchange, "warm", typeInfo);
        var first = Task.Run(() => CreateMessage(exchange, "first", typeInfo));
        try
        {
            _ = await Task.WhenAny(first, entered.Task);
            await Assert.That(entered.Task.IsCompleted).IsTrue();
            using var second = CreateMessage(exchange, "second", typeInfo);
            await AssertBodyAsync(second, "second", typeInfo);
            await Assert.That(first.IsCompleted).IsFalse();
        }
        finally
        {
            release.Set();
            using var firstMessage = await first;
            await AssertBodyAsync(firstMessage, "first", typeInfo);
        }
    }

    [Test]
    public async Task CreateMessage_Should_Use_Each_Bodys_Own_Serializer_Options()
    {
        var exchange = CreateExchange([]);
        var body = new SessionPromptRequest { Text = "options" };
        var options = new JsonSerializerOptions { TypeInfoResolver = OpenCodeJsonContext.Default, WriteIndented = true };
        var indented = new OpenCodeJsonContext(options).SessionPromptRequest;
        using var first = CreateMessage(exchange, body);
        using var second = CreateMessage(exchange, body, indented);

        await AssertBodyAsync(first, body, OpenCodeJsonContext.Default.SessionPromptRequest);
        await AssertBodyAsync(second, body, indented);
    }

    [Test]
    public async Task SendThroughPoliciesAsync_Should_Leave_An_Unfinished_Writes_Array_Out_Of_The_Pool()
    {
        var body = new SessionPromptRequest { Text = "in flight" };
        var expected = JsonSerializer.SerializeToUtf8Bytes(body, OpenCodeJsonContext.Default.SessionPromptRequest);
        var pool = new TrackingByteArrayPool();
        using var stream = new WriteStream(wait: true);
        using var handler = new CopyingHandler(stream, expected, duplex: true);
        using var http = new HttpClient(handler);
        using var transport = new TransportPolicy(http, ownsHttpClient: false);
        var exchange = CreateExchange([transport], pool);
        using var message = CreateMessage(exchange, body);
        try
        {
            await Assert.That(pool.RentCount).IsGreaterThan(0);
            await exchange.SendThroughPoliciesAsync(message);
            await Assert.That(handler.Copying.IsCompleted).IsFalse();
            message.Dispose();
            await Assert.That(pool.ReturnCount).IsEqualTo(0);
        }
        finally
        {
            stream.Release();
            await handler.Copying;
        }

        await Assert.That(stream.Bytes).IsEquivalentTo(expected, CollectionOrdering.Matching);
        await Assert.That(pool.ReturnCount).IsEqualTo(0);
    }

    [Test]
    public async Task SendThroughPoliciesAsync_Should_Return_Cleared_Request_Storage_After_A_Completed_Write()
    {
        var body = new SessionPromptRequest { Text = "completed" };
        var expected = JsonSerializer.SerializeToUtf8Bytes(body, OpenCodeJsonContext.Default.SessionPromptRequest);
        var returned = new List<byte[]>();
        var pool = new TrackingByteArrayPool(onReturn: buffer => returned.Add(buffer.AsSpan(0, expected.Length).ToArray()));
        using var stream = new WriteStream(wait: false);
        using var handler = new CopyingHandler(stream, expected, duplex: false);
        using var http = new HttpClient(handler);
        using var transport = new TransportPolicy(http, ownsHttpClient: false);
        var exchange = CreateExchange([transport], pool);
        using var message = CreateMessage(exchange, body);

        await exchange.SendThroughPoliciesAsync(message);
        message.Dispose();

        await Assert.That(pool.RentCount).IsGreaterThan(0);
        await Assert.That(pool.ReturnCount).IsEqualTo(pool.RentCount);
        await Assert.That(pool.OutstandingCount).IsEqualTo(0);
        foreach (var prefix in returned)
        {
            await Assert.That(prefix).IsEquivalentTo(new byte[expected.Length], CollectionOrdering.Matching);
        }
    }

    private static PipelineExchange CreateExchange(PipelinePolicy[] policies, ArrayPool<byte>? pool = null) =>
        new(new ConnectionSnapshot("https://test.invalid", authorization: null, location: null, TimeProvider.System),
            clientNetworkTimeout: null, policies, CancellationToken.None)
        { BodyPool = pool ?? ResponseBufferingPolicy.DefaultPool };

    private static PipelineMessage CreateMessage(PipelineExchange exchange, SessionPromptRequest body) =>
        CreateMessage(exchange, body, OpenCodeJsonContext.Default.SessionPromptRequest);

    private static PipelineMessage CreateMessage<T>(PipelineExchange exchange, T body, JsonTypeInfo<T> typeInfo) where T : class =>
        exchange.CreateMessage(OperationRequest.Describe("test.operation", HttpMethod.Post, "/api/session/prompt", body,
            typeInfo, content: null, options: null, declaredHeaders: null),
            bufferBody: true, perCallNetworkTimeout: null, CancellationToken.None);

    private static async Task AssertBodyAsync(PipelineMessage message, object body, JsonTypeInfo typeInfo) =>
        await Assert.That(await message.Request.Content!.ReadAsByteArrayAsync(message.CancellationToken))
            .IsEquivalentTo(JsonSerializer.SerializeToUtf8Bytes(body, typeInfo), CollectionOrdering.Matching);

    private sealed class BlockingConverter(TaskCompletionSource<bool> entered, ManualResetEventSlim release) : JsonConverter<string>
    {
        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            writer.WriteStringValue(value);
            writer.Flush();
            if (value == "first")
            {
                _ = entered.TrySetResult(true);
                release.Wait();
            }

            writer.WriteEndArray();
        }
    }

    private sealed class CopyingHandler(WriteStream stream, byte[] expected, bool duplex) : HttpMessageHandler
    {
        public Task Copying { get; private set; } = Task.CompletedTask;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Every operation in these tests has a JSON body. This handler owns and exposes the copy task.
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            stream.Entered = entered;
            // A duplex transport does not pass the caller token to the body write.
            var copying = request.Content!.CopyToAsync(stream, CancellationToken.None);
            Copying = copying;
            _ = await Task.WhenAny(copying, entered.Task);
            await Assert.That(entered.Task.IsCompleted).IsTrue();
            if (!duplex)
            {
                await copying;
                await Assert.That(stream.Bytes).IsEquivalentTo(expected, CollectionOrdering.Matching);
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class WriteStream(bool wait) : MemoryStream
    {
        private TaskCompletionSource<bool>? _release;

        public TaskCompletionSource<bool>? Entered { get; set; }

        public byte[] Bytes { get; private set; } = [];

        public void Release() => _ = _release?.TrySetResult(true);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteCoreAsync(buffer.AsMemory(offset, count));

#if NET
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(WriteCoreAsync(buffer));
#endif

        private async Task WriteCoreAsync(ReadOnlyMemory<byte> buffer)
        {
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _release = release;
            // The handler attaches its entry signal before starting the copy.
            _ = Entered!.TrySetResult(true);
            if (wait)
            {
                _ = await release.Task;
            }

            Bytes = buffer.ToArray();
        }
    }
}
