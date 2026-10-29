using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// A union's declared tags keyed by their UTF-8 bytes, so a known marker dispatches straight from
/// the reader's buffer on every target framework. Tags are bucketed by byte length, and a lookup
/// compares only the tags of its own length.
/// </summary>
internal sealed class UnionTagTable<TValue>
{
    private readonly Entry[][] _entriesByLength;

    public UnionTagTable(IEnumerable<KeyValuePair<string, TValue>> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var buckets = new Dictionary<int, List<Entry>>();
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Key))
            {
                throw new ArgumentException("A union tag must be a non-empty, non-whitespace string.", nameof(entries));
            }

            var utf8 = Encoding.UTF8.GetBytes(entry.Key);
            if (!buckets.TryGetValue(utf8.Length, out var bucket))
            {
                bucket = [];
                buckets.Add(utf8.Length, bucket);
            }

            // Distinct strings can share UTF-8 bytes (lone surrogates encode alike), so the
            // duplicate check runs on the bytes the lookup compares.
            if (bucket.Exists(existing => existing.Utf8.AsSpan().SequenceEqual(utf8)))
            {
                throw new ArgumentException($"The union tag '{entry.Key}' is declared twice.", nameof(entries));
            }

            bucket.Add(new Entry(utf8, entry.Value));
        }

        _entriesByLength = new Entry[buckets.Count is 0 ? 0 : buckets.Keys.Max() + 1][];
        for (var length = 0; length < _entriesByLength.Length; length++)
        {
            _entriesByLength[length] = buckets.TryGetValue(length, out var bucket) ? [.. bucket] : [];
        }
    }

    /// <summary>Looks up an unescaped UTF-8 tag.</summary>
    public bool TryGetValue(ReadOnlySpan<byte> utf8Tag, [MaybeNullWhen(false)] out TValue value)
    {
        if (utf8Tag.Length < _entriesByLength.Length)
        {
            foreach (var entry in _entriesByLength[utf8Tag.Length])
            {
                if (utf8Tag.SequenceEqual(entry.Utf8))
                {
                    value = entry.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    /// <summary>Looks up a decoded tag; it encodes, so it serves only markers too long to copy.</summary>
    public bool TryGetValue(string tag, [MaybeNullWhen(false)] out TValue value)
    {
        ArgumentNullException.ThrowIfNull(tag);
        return TryGetValue(Encoding.UTF8.GetBytes(tag), out value);
    }

    private sealed record Entry(byte[] Utf8, TValue Value);
}

/// <summary>Builds a <see cref="UnionTagTable{TValue}"/> at the end of a generated table initializer.</summary>
internal static class UnionTagTable
{
    public static UnionTagTable<TValue> ToUnionTagTable<TValue>(this IEnumerable<KeyValuePair<string, TValue>> entries) => new(entries);
}
