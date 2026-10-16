using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace OpenCode.Sdk.Tests.Harness;

/// <summary>Builds the small configuration seed owned by the ordinary pinned server fixture.</summary>
internal sealed class ServerConfigSeed
{
    private static readonly JsonSerializerOptions NodeValueOptions = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    private readonly JsonArray _plugins = [];

    private readonly JsonObject _root;

    internal ServerConfigSeed()
    {
        _root = new JsonObject { ["plugins"] = _plugins };
    }

    internal ServerConfigSeed WithPluginDirectory(string pluginDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginDirectory);

        _plugins.Add(JsonSerializer.SerializeToNode(pluginDirectory, NodeValueOptions));
        return this;
    }

    internal string Render() => _root.ToJsonString();
}
