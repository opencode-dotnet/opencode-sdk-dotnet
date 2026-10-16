using System.Globalization;
using System.IO.Abstractions;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenCode.Sdk.Tools.Generator.Refresh.Models;
using OpenCode.Sdk.Tools.Serialization;

namespace OpenCode.Sdk.Tools.Generator.Refresh;

/// <summary>
/// Loads and validates the committed source watch under <c>spec/source-watch.json</c>: every
/// entry must name a submodule-relative path once, pin a lowercase SHA-256, state the behavior
/// its door depends on, and carry a supported anchor. An absent file is an empty watch.
/// </summary>
internal sealed partial class SourceWatchLoader(IFileSystem fileSystem)
{
    private const int SourceWatchSchemaVersion = 1;

    private readonly IFileSystem _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));

    public async Task<SourceWatch> LoadAsync(CancellationToken cancellationToken)
    {
        var bytes = _fileSystem.File.Exists(SnapshotPaths.SourceWatch)
            ? await _fileSystem.File.ReadAllBytesAsync(SnapshotPaths.SourceWatch, cancellationToken).ConfigureAwait(false)
            : null;
        return Parse(bytes);
    }

    public static SourceWatch Parse(byte[]? bytes)
    {
        if (bytes is null)
        {
            return new SourceWatch { SchemaVersion = SourceWatchSchemaVersion, Sources = [] };
        }

        SourceWatch? watch;
        try
        {
            watch = JsonSerializer.Deserialize(bytes, ToolJsonContext.Default.SourceWatch);
        }
        catch (JsonException exception)
        {
            throw new SnapshotRefreshException($"the source watch '{SnapshotPaths.SourceWatch}' is invalid: {exception.Message}",
                exception);
        }

        if (watch is null)
        {
            throw new SnapshotRefreshException($"the source watch '{SnapshotPaths.SourceWatch}' cannot be JSON null");
        }

        Validate(watch);
        return watch;
    }

    private static void Validate(SourceWatch watch)
    {
        if (watch.SchemaVersion is not SourceWatchSchemaVersion)
        {
            throw new SnapshotRefreshException(
                $"the source watch declares unsupported schema version {watch.SchemaVersion.ToString(CultureInfo.InvariantCulture)}");
        }

        if (watch.Sources.Count is 0)
        {
            throw new SnapshotRefreshException("the source watch exists but names no sources; delete it or list the doors' inputs");
        }

        foreach (var source in watch.Sources)
        {
            ValidateSource(source);
        }

        var paths = watch.Sources.Select(static source => source.Path).ToArray();
        var duplicate = paths.GroupBy(static path => path, StringComparer.Ordinal).FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new SnapshotRefreshException($"the source watch names '{duplicate.Key}' more than once");
        }
    }

    private static void ValidateSource(WatchedSource source)
    {
        if (string.IsNullOrWhiteSpace(source.Path)
            || source.Path[0] is '/'
            || source.Path.Contains("..", StringComparison.Ordinal))
        {
            throw new SnapshotRefreshException($"the source watch entry '{source.Path}' is not a submodule-relative path");
        }

        if (!Sha256Regex().IsMatch(source.Sha256))
        {
            throw new SnapshotRefreshException($"the source watch entry '{source.Path}' does not pin a lowercase SHA-256");
        }

        if (string.IsNullOrWhiteSpace(source.Behavior))
        {
            throw new SnapshotRefreshException($"the source watch entry '{source.Path}' must state the behavior its door depends on");
        }

        if (!string.Equals(source.Anchor.Type, SourceAnchor.Contains, StringComparison.Ordinal))
        {
            throw new SnapshotRefreshException(
                $"the source watch entry '{source.Path}' declares unsupported anchor type '{source.Anchor.Type}'");
        }

        if (string.IsNullOrWhiteSpace(source.Anchor.Text))
        {
            throw new SnapshotRefreshException($"the source watch entry '{source.Path}' declares an empty anchor");
        }
    }

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Sha256Regex();
}
