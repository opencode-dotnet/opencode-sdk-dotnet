using System.Globalization;
using System.Text;

namespace OpenCode.Sdk.Performance.Tests.Support;

/// <summary>
/// A config read body in the wire shape <c>GET /api/config</c> returns: a bare list holding a
/// directory entry and one document whose info carries every structural union a config can: model
/// selections as text and as objects, a pricing as one cost and as tiered costs, number and boolean
/// timeouts, a formatter map, an LSP map whose entries are a server and a disabled marker,
/// references as text, a repository and a path, plugins as text and as an entry, and a warming
/// switch. Providers and their models scale the body from a project's config to a gateway catalog;
/// each carries the open settings members (a base URL, a temperature) and the reasoning variants a
/// gateway's models declare, shaped as upstream's own provider config test shapes them.
/// </summary>
internal sealed class ConfigFixture
{
    private readonly int _providers;
    private readonly int _modelsPerProvider;

    public ConfigFixture(int providers, int modelsPerProvider)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(providers, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(modelsPerProvider, 2);
        _providers = providers;
        _modelsPerProvider = modelsPerProvider;
    }

    /// <summary>Gets how many providers the document declares.</summary>
    public int Providers => _providers;

    /// <summary>Gets how many models each provider declares.</summary>
    public int ModelsPerProvider => _modelsPerProvider;

    /// <summary>Builds the body on one line, as a server sends it.</summary>
    /// <returns>The UTF-8 body.</returns>
    public byte[] Build()
    {
        var json = new StringBuilder(4096 + (_providers * _modelsPerProvider * 400));
        _ = json.Append("[{\"type\":\"directory\",\"path\":\"/repo/.opencode\"},")
            .Append("{\"type\":\"document\",\"path\":\"/repo/opencode.json\",\"info\":{")
            .Append("\"$schema\":\"https://opencode.ai/config.json\",\"shell\":\"/bin/bash\",")
            .Append("\"model\":\"provider0/model0\",\"default_agent\":\"build\",\"update\":\"notify\",\"share\":\"manual\",")
            .Append("\"username\":\"bench\",\"permissions\":[{\"action\":\"edit\",\"resource\":\"*\",\"effect\":\"ask\"},")
            .Append("{\"action\":\"bash\",\"resource\":\"git push*\",\"effect\":\"deny\"}],")
            .Append("\"agents\":{\"build\":{\"model\":{\"providerID\":\"provider0\",\"model\":\"model0\",\"variant\":\"high\"},")
            .Append("\"description\":\"Builds and tests the change\",\"mode\":\"primary\",\"steps\":40,")
            .Append("\"permissions\":[{\"action\":\"edit\",\"resource\":\"src/**\",\"effect\":\"allow\"}]},")
            .Append("\"review\":{\"model\":\"provider0/model1\",\"description\":\"Reviews a diff\",\"mode\":\"subagent\",\"hidden\":false}},")
            .Append("\"snapshots\":true,\"watcher\":{\"ignore\":[\"node_modules/**\",\"dist/**\",\".git/**\"]},")
            .Append("\"formatter\":{\"prettier\":{\"command\":[\"npx\",\"prettier\",\"--write\",\"$FILE\"],\"extensions\":[\".ts\",\".json\"]},")
            .Append("\"dotnet\":{\"command\":[\"dotnet\",\"format\",\"--include\",\"$FILE\"],\"extensions\":[\".cs\"],\"environment\":{\"DOTNET_NOLOGO\":\"1\"}}},")
            .Append("\"lsp\":{\"typescript\":{\"command\":[\"typescript-language-server\",\"--stdio\"],\"extensions\":[\".ts\",\".tsx\"],")
            .Append("\"initialization\":{\"preferences\":{\"importModuleSpecifier\":\"relative\"}}},\"eslint\":{\"disabled\":true}},")
            .Append("\"tool_output\":{\"max_lines\":2000,\"max_bytes\":51200},\"compaction\":{\"auto\":true,\"buffer\":20000},")
            .Append("\"skills\":[\"review\",\"release\"],")
            .Append("\"commands\":{\"test\":{\"template\":\"Run the tests: $ARGUMENTS\",\"description\":\"Run the tests\",\"agent\":\"build\"},")
            .Append("\"ship\":{\"template\":\"Prepare the release notes\",\"model\":\"provider0/model1\",\"subtask\":true}},")
            .Append("\"instructions\":[\"AGENTS.md\",\"docs/engineering/*.md\"],")
            .Append("\"references\":{\"docs\":\"https://opencode.ai/docs\",")
            .Append("\"upstream\":{\"repository\":\"https://github.com/anomalyco/opencode\",\"branch\":\"v2\",\"description\":\"The server\"},")
            .Append("\"sibling\":{\"path\":\"../sibling\",\"hidden\":true}},")
            .Append("\"websearch\":{\"provider\":\"exa\"},")
            .Append("\"plugins\":[\"opencode-plugin-a\",{\"package\":\"opencode-plugin-b\",\"options\":{\"level\":2,\"tags\":[\"x\",\"y\"]}}],")
            .Append("\"worktree\":{\"directory\":\".worktrees\"},\"warming\":true,\"providers\":{");
        for (var provider = 0; provider < _providers; provider++)
        {
            if (provider > 0)
            {
                _ = json.Append(',');
            }

            AppendProvider(json, provider);
        }

        _ = json.Append("}}}]");
        return Encoding.UTF8.GetBytes(json.ToString());
    }

    private void AppendProvider(StringBuilder json, int provider)
    {
        var id = provider.ToString(CultureInfo.InvariantCulture);
        _ = json.Append("\"provider").Append(id).Append("\":{\"name\":\"Provider ").Append(id)
            .Append("\",\"env\":[\"PROVIDER").Append(id).Append("_API_KEY\"],\"package\":\"@ai-sdk/openai-compatible\",")
            .Append("\"settings\":{\"timeout\":300000,\"headerTimeout\":false,\"chunkTimeout\":30000,\"baseURL\":\"https://proxy.example/v1\"},")
            .Append("\"headers\":{\"x-team\":\"bench\"},\"body\":{\"store\":false},\"models\":{");
        for (var model = 0; model < _modelsPerProvider; model++)
        {
            if (model > 0)
            {
                _ = json.Append(',');
            }

            AppendModel(json, model);
        }

        _ = json.Append("}}");
    }

    /// <summary>Even models price with one cost, odd ones with a context-tiered list, so both pricing arms are read.</summary>
    private static void AppendModel(StringBuilder json, int model)
    {
        var id = model.ToString(CultureInfo.InvariantCulture);
        _ = json.Append("\"model").Append(id).Append("\":{\"modelID\":\"vendor/model").Append(id).Append("\",\"name\":\"Model ").Append(id).Append("\",\"family\":\"bench\",")
            .Append("\"settings\":{\"baseURL\":\"https://model.example/v1\",\"temperature\":0.2},")
            .Append("\"variants\":[{\"id\":\"high\",\"settings\":{\"effort\":\"high\"}},{\"id\":\"low\",\"settings\":{\"effort\":\"low\"}}],")
            .Append("\"capabilities\":{\"tools\":true,\"input\":[\"text\",\"image\"],\"output\":[\"text\"]},\"cost\":");
        _ = model % 2 == 0
            ? json.Append("{\"input\":3,\"output\":15,\"cache\":{\"read\":0.3,\"write\":3.75}}")
            : json.Append("[{\"input\":3,\"output\":15},{\"tier\":{\"type\":\"context\",\"size\":200000},\"input\":6,\"output\":22.5}]");
        _ = json.Append(",\"limit\":{\"context\":200000,\"output\":64000}}");
    }
}
