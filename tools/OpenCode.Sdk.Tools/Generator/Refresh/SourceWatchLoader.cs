using System.Globalization;
using System.IO.Abstractions;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenCode.Sdk.Tools.Generator.Refresh.Models;
using OpenCode.Sdk.Tools.Serialization;

namespace OpenCode.Sdk.Tools.Generator.Refresh;

/// <summary>
/// Loads and validates the committed source watch under <c>spec/source-watch.json</c>: every
/// entry names a submodule-relative file once, pins its lowercase SHA-256, and lists the behaviors
/// this SDK or opencode-agents-ai depends on, each with its owner and anchor. An absent file is an empty watch.
/// </summary>
internal sealed partial class SourceWatchLoader(IFileSystem fileSystem)
{
    private const int SourceWatchSchemaVersion = 2;

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

        if (source.Behaviors.Count is 0)
        {
            throw new SnapshotRefreshException($"the source watch entry '{source.Path}' must name at least one behavior");
        }

        foreach (var behavior in source.Behaviors)
        {
            ValidateBehavior(source.Path, behavior);
        }

        var duplicate = source.Behaviors.GroupBy(static behavior => behavior.Anchor.Text, StringComparer.Ordinal)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new SnapshotRefreshException($"the source watch entry '{source.Path}' names anchor '{duplicate.Key}' more than once");
        }
    }

    private static void ValidateBehavior(string path, WatchedBehavior behavior)
    {
        if (!WatchOwners.All.Contains(behavior.Owner, StringComparer.Ordinal))
        {
            throw new SnapshotRefreshException(
                $"the source watch entry '{path}' declares unsupported owner '{behavior.Owner}' for anchor '{behavior.Anchor.Text}'");
        }

        if (string.IsNullOrWhiteSpace(behavior.Behavior))
        {
            throw new SnapshotRefreshException($"the source watch entry '{path}' must state the behavior for anchor '{behavior.Anchor.Text}'");
        }

        if (!string.Equals(behavior.Anchor.Type, SourceAnchor.Contains, StringComparison.Ordinal))
        {
            throw new SnapshotRefreshException(
                $"the source watch entry '{path}' declares unsupported anchor type '{behavior.Anchor.Type}' for anchor '{behavior.Anchor.Text}'");
        }

        if (string.IsNullOrWhiteSpace(behavior.Anchor.Text))
        {
            throw new SnapshotRefreshException($"the source watch entry '{path}' declares an empty anchor");
        }
    }

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.None, matchTimeoutMilliseconds: Timeout.Infinite)]
    private static partial Regex Sha256Regex();
}
