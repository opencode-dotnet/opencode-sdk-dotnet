using System.Globalization;
using System.IO.Abstractions;
using System.Xml;
using System.Xml.Linq;
using OpenCode.Sdk.Tools.TestRoster.Models;

namespace OpenCode.Sdk.Tools.TestRoster;

/// <summary>Reads the TRX files directly in a test pass's results directory into run summaries;
/// subfolders such as the platform's <c>merged/</c> are ignored.</summary>
internal sealed class TrxRunReader(IFileSystem fileSystem)
{
    private const string TrxPattern = "*.trx";

    private static readonly XNamespace TeamTestNamespace = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    private readonly IFileSystem _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));

    public async Task<IReadOnlyList<TestRunSummary>> ReadAsync(string resultsDirectory, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resultsDirectory);

        if (!_fileSystem.Directory.Exists(resultsDirectory))
        {
            throw new TestRosterException($"Results directory '{resultsDirectory}' does not exist.");
        }

        // GetFiles is non-recursive, and the extension re-check keeps the Windows three-letter
        // pattern quirk (matching "*.trxx" as well) from reaching the name parser.
        var trxPaths = _fileSystem.Directory.GetFiles(resultsDirectory, TrxPattern)
            .Where(path => string.Equals(_fileSystem.Path.GetExtension(path), ".trx", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToArray();

        var summaries = new List<TestRunSummary>(trxPaths.Length);
        foreach (var trxPath in trxPaths)
        {
            summaries.Add(await ReadSummaryAsync(trxPath, cancellationToken).ConfigureAwait(false));
        }

        return summaries;
    }

    private async Task<TestRunSummary> ReadSummaryAsync(string trxPath, CancellationToken cancellationToken)
    {
        var fileName = _fileSystem.Path.GetFileName(trxPath);
        var moduleName = ParseModuleName(fileName);
        var counters = await ReadCountersAsync(trxPath, fileName, cancellationToken).ConfigureAwait(false);
        return new TestRunSummary(
            fileName,
            moduleName.Assembly,
            moduleName.TargetFramework,
            moduleName.Arch,
            counters.Total,
            counters.Executed);
    }

    /// <summary>Parses "&lt;assembly&gt;_&lt;target-framework&gt;_&lt;arch&gt;.trx" from the right:
    /// assembly names may contain underscores, so the last two separators carry the arch and the
    /// target framework.</summary>
    private TrxModuleName ParseModuleName(string fileName)
    {
        var withoutExtension = _fileSystem.Path.GetFileNameWithoutExtension(fileName);
        var archSeparator = withoutExtension.AsSpan().LastIndexOf('_');
        var frameworkSeparator = archSeparator > 0
            ? withoutExtension.AsSpan(0, archSeparator).LastIndexOf('_')
            : -1;
        var parses = archSeparator >= 0
                     && frameworkSeparator > 0
                     && archSeparator - frameworkSeparator > 1
                     && archSeparator < withoutExtension.Length - 1;
        if (!parses)
        {
            throw new TestRosterException(
                $"TRX file name '{fileName}' does not follow '<assembly>_<target-framework>_<arch>.trx'.");
        }

        return new TrxModuleName(
            withoutExtension[..frameworkSeparator],
            withoutExtension[(frameworkSeparator + 1)..archSeparator],
            withoutExtension[(archSeparator + 1)..]);
    }

    private async Task<TrxCounters> ReadCountersAsync(string trxPath, string fileName, CancellationToken cancellationToken)
    {
        XDocument document;
        try
        {
            var stream = _fileSystem.File.OpenRead(trxPath);
            await using (stream.ConfigureAwait(false))
            {
                document = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is XmlException or IOException or UnauthorizedAccessException)
        {
            throw new TestRosterException($"TRX file '{fileName}' is not readable XML: {exception.Message}", exception);
        }

        var counters = document.Root?.Element(TeamTestNamespace + "ResultSummary")?.Element(TeamTestNamespace + "Counters");
        var totalText = counters?.Attribute("total")?.Value;
        var executedText = counters?.Attribute("executed")?.Value;
        if (totalText is null || executedText is null)
        {
            throw new TestRosterException(
                $"TRX file '{fileName}' carries no ResultSummary/Counters with 'total' and 'executed' attributes.");
        }

        if (!int.TryParse(totalText, NumberStyles.None, CultureInfo.InvariantCulture, out var total)
            || !int.TryParse(executedText, NumberStyles.None, CultureInfo.InvariantCulture, out var executed))
        {
            throw new TestRosterException(
                $"TRX file '{fileName}' carries non-numeric counters (total='{totalText}', executed='{executedText}').");
        }

        return new TrxCounters(total, executed);
    }

    private sealed record TrxModuleName(string Assembly, string TargetFramework, string Arch);

    private sealed record TrxCounters(int Total, int Executed);
}
