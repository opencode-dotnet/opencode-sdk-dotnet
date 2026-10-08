using System.Text;
using OpenCode.Sdk.TestSupport;
using Testably.Abstractions;
using Testably.Abstractions.Testing;
using Testably.Abstractions.Testing.Initializer;

namespace OpenCode.Sdk.Tests.Support;

public sealed class ProviderCredentialVariablesTests
{
    private const string EnvMethod = "type: \"env\"";
    private const string Names = "names:";
    private const string ComputedRead = "process.env[";

    private static readonly RealFileSystem FileSystem = new();

    [Test]
    public async Task ParseCatalogAsync_Should_Collect_Every_Env_Name_And_Skip_Anything_Else(CancellationToken cancellationToken)
    {
        const string catalog = """
            {
              "one": { "id": "one", "env": ["ONE_API_KEY", "ONE_REGION"] },
              "two": { "id": "two", "env": ["one_api_key", 3, ""] },
              "three": { "id": "three" },
              "four": { "id": "four", "env": "NOT_AN_ARRAY" },
              "five": ["not", "a", "provider"]
            }
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(catalog));

        var names = await ProviderCredentialVariables.ParseCatalogAsync(stream, cancellationToken);

        string[] expected = ["ONE_API_KEY", "ONE_REGION"];
        await Assert.That(names.OrderBy(static name => name, StringComparer.Ordinal)).IsEquivalentTo(expected);
        await Assert.That(names.Contains("One_Api_Key")).IsTrue();
    }

    [Test]
    public async Task ParseSource_Should_Name_Every_Spelled_Or_Read_Variable_But_The_Terminal_Shell_And_Proxy()
    {
        const string source = """
            method: { type: "env", names: ["EXA_API_KEY"] },
            auth: AuthOptions.bearer(input, ["TOGETHER_API_KEY", "TOGETHER_AI_API_KEY"]),
            const project = process.env.GCP_PROJECT ?? process.env["GCLOUD_PROJECT"]
            const shell = process.env.SHELL ?? process.env.ComSpec
            const term = process.env.TERM_PROGRAM
            const proxy = process.env.HTTPS_PROXY ?? "NO_PROXY"
            const label = "not a name"
            """;

        var names = ProviderCredentialVariables.ParseSource(source).Distinct(StringComparer.Ordinal).OrderBy(static name => name, StringComparer.Ordinal);

        string[] expected = ["EXA_API_KEY", "GCLOUD_PROJECT", "GCP_PROJECT", "TOGETHER_AI_API_KEY", "TOGETHER_API_KEY"];
        await Assert.That(names).IsEquivalentTo(expected);
    }

    /// <summary>
    /// The names each kind of read takes at the pin: the catalog's, an env method a plugin
    /// declares, a key an auth helper takes, a direct read, a credential switch, and what the
    /// Google auth library reads that the source never spells.
    /// </summary>
    [Test]
    public async Task FromPinnedSourceAsync_Should_Name_Every_Kind_Of_Credential_The_Pin_Reads(CancellationToken cancellationToken)
    {
        var credentials = await ProviderCredentialVariables.FromPinnedSourceAsync(FileSystem, cancellationToken);

        await Assert.That(credentials.Unavailable).IsNull();
        foreach (var name in new[]
                 {
                     "ANTHROPIC_API_KEY", "OPENAI_API_KEY", "AWS_SECRET_ACCESS_KEY",
                     "SNOWFLAKE_CORTEX_TOKEN", "EXA_API_KEY", "FIRECRAWL_API_KEY", "PARALLEL_API_KEY", "TAVILY_API_KEY", "TINYFISH_API_KEY",
                     "CF_AIG_TOKEN", "VERCEL_OIDC_TOKEN", "CLOUDFLARE_WORKERS_AI_TOKEN",
                     "GOOGLE_VERTEX_API_KEY", "MODAL_PROXY_TOKEN", "AICORE_SERVICE_KEY",
                     "GOOGLE_VERTEX_PROJECT", "GOOGLE_CLOUD_PROJECT", "GCP_PROJECT", "GCLOUD_PROJECT",
                     "GOOGLE_APPLICATION_CREDENTIALS", "CLOUDSDK_CONFIG",
                 })
        {
            await Assert.That(credentials.Names).Contains(name);
        }

        foreach (var name in new[] { "TERM", "SHELL", "COMSPEC", "PATH", "HOME" })
        {
            await Assert.That(credentials.Names).DoesNotContain(name);
        }
    }

    /// <summary>
    /// Fails when the pin gains an env method the scrub would not remove, read with a parser of
    /// its own rather than the scan's. A method whose names are not a literal list is computed;
    /// the pin has one, the catalog's own, whose names are the catalog's plus literals the scan
    /// reads, and a new one fails here until it is read.
    /// </summary>
    [Test]
    public async Task FromPinnedSourceAsync_Should_Cover_Every_Env_Method_The_Pinned_Source_Declares(CancellationToken cancellationToken)
    {
        var credentials = await ProviderCredentialVariables.FromPinnedSourceAsync(FileSystem, cancellationToken);
        var declared = new SortedSet<string>(StringComparer.Ordinal);
        var computed = new List<string>();
        foreach (var file in ProviderCredentialVariables.PinnedSourceFiles(FileSystem))
        {
            var source = await ProviderCredentialVariables.ReadSourceAsync(FileSystem, file, cancellationToken);
            for (var method = source.IndexOf(EnvMethod, StringComparison.Ordinal);
                 method >= 0;
                 method = source.IndexOf(EnvMethod, method + 1, StringComparison.Ordinal))
            {
                var names = source.IndexOf(Names, method, StringComparison.Ordinal) + Names.Length;
                var list = source.AsSpan(names).TrimStart();
                if (list is not ['[', ..])
                {
                    computed.Add(FileSystem.Path.GetFileName(file));
                    continue;
                }

                foreach (var entry in list[1..list.IndexOf(']')].ToString().Split(','))
                {
                    if (entry.Trim().Trim('"') is { Length: > 0 } name)
                    {
                        _ = declared.Add(name);
                    }
                }
            }
        }

        string[] expectedComputed = ["models-dev.ts"];
        await Assert.That(computed).IsEquivalentTo(expectedComputed);
        await Assert.That(declared).Contains("SNOWFLAKE_CORTEX_PAT");
        await Assert.That(declared.Where(name => !credentials.Names.Contains(name))).IsEmpty();
    }

    /// <summary>
    /// Every name the scan leaves to the environment is one no provider declares a credential
    /// under, so the hand-kept exceptions can never hide one.
    /// </summary>
    [Test]
    public async Task IsNotCredential_Should_Never_Name_A_Catalog_Credential(CancellationToken cancellationToken)
    {
        var checkout = FileSystem.Path.Combine(new PinnedServerCommand(FileSystem).RepositoryRoot, "external", "opencode");
        using var stream = FileSystem.File.OpenRead(FileSystem.Path.Combine(checkout, "packages", "core", "src", "models-dev", "snapshot.txt"));
        var catalog = await ProviderCredentialVariables.ParseCatalogAsync(stream, cancellationToken);

        await Assert.That(catalog.Where(ProviderCredentialVariables.IsNotCredential)).IsEmpty();
    }

    /// <summary>
    /// The scan sees a literal index and a member read; an index computed at run time names
    /// nothing it can see. The pin has one, Bedrock walking its own list of AWS chain literals,
    /// which the scan and the <c>AWS_</c> prefix both cover. A new one fails here until it is read.
    /// </summary>
    [Test]
    public async Task PinnedSourceFiles_Should_Read_A_Computed_Variable_Name_Only_Where_It_Is_Covered(CancellationToken cancellationToken)
    {
        var computed = new List<string>();
        foreach (var file in ProviderCredentialVariables.PinnedSourceFiles(FileSystem))
        {
            var source = await ProviderCredentialVariables.ReadSourceAsync(FileSystem, file, cancellationToken);
            for (var read = source.IndexOf(ComputedRead, StringComparison.Ordinal);
                 read >= 0;
                 read = source.IndexOf(ComputedRead, read + 1, StringComparison.Ordinal))
            {
                if (source.AsSpan(read + ComputedRead.Length).TrimStart() is not ['"', ..])
                {
                    computed.Add(FileSystem.Path.GetFileName(file));
                }
            }
        }

        string[] expected = ["amazon-bedrock.ts"];
        await Assert.That(computed).IsEquivalentTo(expected);
    }

    [Test]
    public async Task FromPinnedSourceAsync_Should_Say_Why_When_The_Checkout_Is_Missing(CancellationToken cancellationToken)
    {
        var fileSystem = new MockFileSystem();
        var repositoryRoot = new PinnedServerCommand(FileSystem).RepositoryRoot;
        _ = fileSystem.Directory.CreateDirectory(AppContext.BaseDirectory);
        _ = fileSystem.Initialize().With(new FileDescription(fileSystem.Path.Combine(repositoryRoot, "OpenCode.slnx"), string.Empty));

        var credentials = await ProviderCredentialVariables.FromPinnedSourceAsync(fileSystem, cancellationToken);

        await Assert.That(credentials.Unavailable).Contains("git submodule update --init external/opencode");
        await Assert.That(credentials.Names).Contains("GOOGLE_APPLICATION_CREDENTIALS");
    }

    [Test]
    [Arguments("AWS_PROFILE", true)]
    [Arguments("aws_web_identity_token_file", true)]
    [Arguments("AWSOME", false)]
    public async Task IsAwsChain_Should_Match_The_Prefix_In_Any_Case(string name, bool expected)
    {
        ArgumentNullException.ThrowIfNull(name);

        await Assert.That(ProviderCredentialVariables.IsAwsChain(name)).IsEqualTo(expected);
    }
}
