#if !NETSTANDARD2_0
using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace OpenCode.Sdk.Internal;

/// <summary>Serializes a request body into an array rented from a pool, with a writer one caller reuses at a time.</summary>
/// <remarks>
/// The writer options match what <see cref="JsonSerializer.SerializeToUtf8Bytes(object?, JsonTypeInfo)"/> uses, so the
/// bytes are the same. Knowledge source: System.Text.Json 10.0.13, JsonSerializerOptions.GetWriterOptions, decompiled.
/// </remarks>
internal sealed class JsonBodyWriter
{
    private const int DefaultMaxDepth = 64;
    private const int InitialBytes = 256;

    private readonly Sink _sink;
    private readonly Utf8JsonWriter _writer;

    private JsonBodyWriter(JsonSerializerOptions options, Sink sink, Utf8JsonWriter writer)
    {
        Options = options;
        _sink = sink;
        _writer = writer;
    }

    public JsonSerializerOptions Options { get; }

    /// <summary>Creates a writer that's never disposed: it holds no unmanaged resource, and each use resets it.</summary>
    public static JsonBodyWriter Create(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var sink = new Sink();
        var writer = new Utf8JsonWriter(sink, new JsonWriterOptions
        {
            Encoder = options.Encoder,
            Indented = options.WriteIndented,
            IndentCharacter = options.IndentCharacter,
            IndentSize = options.IndentSize,
            MaxDepth = options.MaxDepth == 0 ? DefaultMaxDepth : options.MaxDepth,
            NewLine = options.NewLine,
            SkipValidation = true,
        });
        return new JsonBodyWriter(options, sink, writer);
    }

    public PooledJsonContent Write(object? body, JsonTypeInfo typeInfo, ArrayPool<byte> pool)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        ArgumentNullException.ThrowIfNull(pool);

        _sink.Begin(pool);
        try
        {
            _writer.Reset(_sink);
            JsonSerializer.Serialize(_writer, body, typeInfo);
            return _sink.Take();
        }
        catch
        {
            _sink.Abandon();
            throw;
        }
    }

    private sealed class Sink : IBufferWriter<byte>
    {
        private ArrayPool<byte> _pool = ArrayPool<byte>.Shared;
        private byte[] _buffer = [];
        private int _length;

        public void Begin(ArrayPool<byte> pool)
        {
            _pool = pool;
            _buffer = pool.Rent(InitialBytes);
            _length = 0;
        }

        public PooledJsonContent Take()
        {
            var content = new PooledJsonContent(_pool, _buffer, _length);
            _buffer = [];
            return content;
        }

        public void Abandon()
        {
            // The writer may have written past the committed length, so the whole array is cleared.
            _buffer.AsSpan().Clear();
            _pool.Return(_buffer);
            _buffer = [];
        }

        public void Advance(int count) => _length += count;

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            Reserve(sizeHint);
            return _buffer.AsMemory(_length);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            Reserve(sizeHint);
            return _buffer.AsSpan(_length);
        }

        private void Reserve(int sizeHint)
        {
            var needed = _length + Math.Max(sizeHint, 1);
            if (needed <= _buffer.Length)
            {
                return;
            }

            var grown = _pool.Rent(Math.Max(needed, _buffer.Length * 2));
            _buffer.AsSpan(0, _length).CopyTo(grown);
            _buffer.AsSpan(0, _length).Clear();
            _pool.Return(_buffer);
            _buffer = grown;
        }
    }
}
#endif
