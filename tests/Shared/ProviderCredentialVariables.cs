using System.IO.Abstractions;
using System.Text.Json;

namespace OpenCode.Sdk.TestSupport;

/// <summary>
/// The variables through which the pinned server takes a provider credential, or the switch that
/// turns one on, from its environment instead of from its database. A server that inherits one
/// enables that provider with the developer's own account, and a prompt a test sends could then
/// reach the real provider with the real key. The names come from the pinned checkout itself, so a
/// provider added at a later pin is covered without an edit: every name a provider in the bundled
/// model catalog lists under <c>env</c>, and every variable name the provider and plugin sources
/// spell or read, which takes in every <c>type: "env"</c> method's names, every name a provider
/// hands its auth helper, and every direct <c>process.env</c> read. Two short hand lists remain:
/// the names an auth library or tool reads whether or not the pinned source spells them, and the
/// names those sources read that are not provider configuration at all.
/// </summary>
internal static class ProviderCredentialVariables
{
    /// <summary>
    /// The AWS SDK's credential chain. The Bedrock provider authenticates through it whenever any
    /// of its variables is set, which is more than the catalog lists, and every one of them carries
    /// this prefix.
    /// </summary>
    private const string AwsChainPrefix = "AWS_";

    /// <summary>The object every direct read in the pinned sources goes through.</summary>
    private const string EnvironmentObject = "process.env";

    /// <summary>The source trees, under the upstream checkout, where providers and plugins read their credentials.</summary>
    private static readonly string[][] SourceRoots =
        [["packages", "core", "src", "plugin"], ["packages", "ai", "src", "providers"]];

    /// <summary>
    /// What a provider's auth tool reads whether or not the pinned source names it: Vertex's
    /// Google auth library takes a service account key file and the gcloud configuration directory
    /// that holds the developer's application default credentials, and the Azure CLI, which the
    /// Azure provider runs with the server's whole environment, takes the directory that holds the
    /// developer's <c>az login</c> session.
    /// </summary>
    private static readonly string[] LibraryReads = ["GOOGLE_APPLICATION_CREDENTIALS", "CLOUDSDK_CONFIG", "AZURE_CONFIG_DIR"];

    /// <summary>
    /// What the scanned sources read that is not provider configuration: the terminal and shell a
    /// skill's command runs under, the proxy routing the scrub handles on its own terms, and Windows'
    /// roaming profile root, which a provider reads to find its stores and which the isolation map
    /// moves into the run root for every owned server.
    /// </summary>
    private static readonly string[] NotCredentials =
        ["TERM", "TERM_PROGRAM", "COLORTERM", "SHELL", "ComSpec", "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY", "APPDATA"];

    /// <summary>Whether a variable belongs to the AWS credential chain.</summary>
    /// <param name="name">The variable name, in any case.</param>
    /// <returns>True for an <c>AWS_</c> variable.</returns>
    public static bool IsAwsChain(string name) => name.StartsWith(AwsChainPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Gets whether a name the sources read is deliberately left to the environment.</summary>
    /// <param name="name">The variable name, in any case.</param>
    /// <returns>True for a terminal, shell, proxy or Windows profile-root variable.</returns>
    public static bool IsNotCredential(string name) =>
        Array.Exists(NotCredentials, kept => string.Equals(kept, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Reads every credential variable name from the pinned checkout: the model catalog and the
    /// provider and plugin sources.
    /// </summary>
    /// <param name="fileSystem">The filesystem the checkout is read through.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>The names, compared without regard to case, or the reason they cannot be read.</returns>
    public static async Task<ProviderCredentials> FromPinnedSourceAsync(IFileSystem fileSystem, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        var checkout = Checkout(fileSystem);
        var catalog = fileSystem.Path.Combine(checkout, "packages", "core", "src", "models-dev", "snapshot.txt");
        var missing = new List<string>();
        if (!fileSystem.File.Exists(catalog))
        {
            missing.Add(catalog);
        }

        var roots = Roots(fileSystem, checkout);
        missing.AddRange(roots.Where(root => !fileSystem.Directory.Exists(root)));
        if (missing.Count > 0)
        {
            return new ProviderCredentials(
                new HashSet<string>(LibraryReads, StringComparer.OrdinalIgnoreCase),
                "The pinned checkout that names the provider credentials a server would inherit is missing ('"
                + string.Join("', '", missing) + "'), so those credentials cannot be scrubbed and no server may start. "
                + "Run: git submodule update --init external/opencode");
        }

        HashSet<string> names;
        using (var stream = fileSystem.File.OpenRead(catalog))
        {
            names = await ParseCatalogAsync(stream, cancellationToken).ConfigureAwait(false);
        }

        foreach (var file in SourceFiles(fileSystem, roots))
        {
            names.UnionWith(ParseSource(await ReadSourceAsync(fileSystem, file, cancellationToken).ConfigureAwait(false)));
        }

        names.UnionWith(LibraryReads);
        return new ProviderCredentials(names, null);
    }

    /// <summary>Lists the pinned provider and plugin sources the names are read from.</summary>
    /// <param name="fileSystem">The filesystem the checkout is read through.</param>
    /// <returns>Every TypeScript file under the scanned trees.</returns>
    public static IEnumerable<string> PinnedSourceFiles(IFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        return SourceFiles(fileSystem, Roots(fileSystem, Checkout(fileSystem)));
    }

    /// <summary>Reads one pinned source file.</summary>
    /// <param name="fileSystem">The filesystem the checkout is read through.</param>
    /// <param name="file">The file's path.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>The file's text.</returns>
    public static async Task<string> ReadSourceAsync(IFileSystem fileSystem, string file, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        // IFile has no asynchronous text read on the net472 leg; a reader over the stream does.
        using var stream = fileSystem.File.OpenRead(file);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Collects every string in each provider's <c>env</c> array.</summary>
    /// <param name="catalog">The catalog: one JSON object of providers keyed by id.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>The names, compared without regard to case.</returns>
    public static async Task<HashSet<string>> ParseCatalogAsync(Stream catalog, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var document = await JsonDocument.ParseAsync(catalog, cancellationToken: cancellationToken).ConfigureAwait(false);
        foreach (var provider in document.RootElement.EnumerateObject())
        {
            if (provider.Value.ValueKind is not JsonValueKind.Object
                || !provider.Value.TryGetProperty("env", out var variables)
                || variables.ValueKind is not JsonValueKind.Array)
            {
                continue;
            }

            foreach (var variable in variables.EnumerateArray())
            {
                if (variable.ValueKind is JsonValueKind.String && variable.GetString() is { Length: > 0 } name)
                {
                    _ = names.Add(name);
                }
            }
        }

        return names;
    }

    /// <summary>
    /// Collects the variable names one source file spells or reads: every double-quoted literal
    /// shaped like an environment variable name (upper-case letters and digits in runs joined by
    /// single underscores, at least two runs, a letter first), which is how a <c>type: "env"</c>
    /// method, an auth helper and a list a computed read walks all name theirs, and every
    /// <c>process.env</c> read by member or by a literal index. A shaped literal that names no
    /// variable costs nothing to remove; the terminal, shell and proxy names are left out.
    /// </summary>
    /// <param name="source">One TypeScript file's text.</param>
    /// <returns>The names, in source order, possibly repeated.</returns>
    public static IReadOnlyList<string> ParseSource(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var names = new List<string>();
        var quote = IndexOfQuote(source, 0);
        while (quote >= 0)
        {
            var end = quote + 1;
            while (end < source.Length && (IsUpperOrDigit(source[end]) || source[end] is '_'))
            {
                end++;
            }

            var shaped = end < source.Length && source[end] is '"' && IsVariableShaped(source[(quote + 1)..end]);
            if (shaped)
            {
                names.Add(source[(quote + 1)..end]);
            }

            // A shaped literal's closing quote opens nothing; any other quote may open the next literal.
            quote = IndexOfQuote(source, shaped ? end + 1 : quote + 1);
        }

        for (var read = source.IndexOf(EnvironmentObject, StringComparison.Ordinal);
             read >= 0;
             read = source.IndexOf(EnvironmentObject, read + 1, StringComparison.Ordinal))
        {
            if (ReadName(source, read + EnvironmentObject.Length) is { } name)
            {
                names.Add(name);
            }
        }

        return names.FindAll(static name => !IsNotCredential(name));
    }

    /// <summary>The name a <c>process.env</c> access at <paramref name="at"/> reads, or null when it is computed.</summary>
    private static string? ReadName(string source, int at)
    {
        if (at < source.Length && source[at] is '.')
        {
            var end = at + 1;
            while (end < source.Length && (char.IsLetterOrDigit(source[end]) || source[end] is '_'))
            {
                end++;
            }

            return end > at + 1 ? source[(at + 1)..end] : null;
        }

        if (at < source.Length && source[at] is '[')
        {
            var open = at + 1;
            while (open < source.Length && char.IsWhiteSpace(source[open]))
            {
                open++;
            }

            var close = open < source.Length && source[open] is '"' ? IndexOfQuote(source, open + 1) : -1;
            return close > open + 1 ? source[(open + 1)..close] : null;
        }

        return null;
    }

    private static int IndexOfQuote(string source, int start)
    {
        var found = source.AsSpan(start).IndexOf('"');
        return found < 0 ? -1 : start + found;
    }

    private static bool IsUpperOrDigit(char character) => character is (>= 'A' and <= 'Z') or (>= '0' and <= '9');

    private static bool IsVariableShaped(string literal)
    {
        var runs = literal.Split('_');
        return runs.Length > 1
            && literal[0] is >= 'A' and <= 'Z'
            && Array.TrueForAll(runs, static run => run.Length > 0);
    }

    private static string Checkout(IFileSystem fileSystem) =>
        fileSystem.Path.Combine(new PinnedServerCommand(fileSystem).RepositoryRoot, "external", "opencode");

    private static string[] Roots(IFileSystem fileSystem, string checkout) =>
        Array.ConvertAll(SourceRoots, segments => fileSystem.Path.Combine([checkout, .. segments]));

    private static IEnumerable<string> SourceFiles(IFileSystem fileSystem, string[] roots) =>
        roots.SelectMany(root => fileSystem.Directory.EnumerateFiles(root, "*.ts", SearchOption.AllDirectories))
            .OrderBy(static file => file, StringComparer.Ordinal);
}
