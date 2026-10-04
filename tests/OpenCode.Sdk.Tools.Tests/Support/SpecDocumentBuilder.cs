using System.Text.Json.Nodes;

namespace OpenCode.Sdk.Tools.Tests.Support;

internal sealed class SpecDocumentBuilder
{
    private readonly FixtureLoader _fixtureLoader;
    private readonly JsonObject _paths = [];
    private readonly JsonObject _schemas = [];
    private readonly JsonObject _components;
    private readonly JsonObject _root;

    public SpecDocumentBuilder()
        : this(new FixtureLoader())
    {
    }

    public SpecDocumentBuilder(FixtureLoader fixtureLoader)
    {
        ArgumentNullException.ThrowIfNull(fixtureLoader);

        _fixtureLoader = fixtureLoader;
        _components = new JsonObject
        {
            ["schemas"] = _schemas,
        };
        _root = new JsonObject
        {
            ["openapi"] = "3.1.0",
            ["info"] = new JsonObject
            {
                ["title"] = "Test API",
                ["version"] = "1.0.0",
            },
            ["paths"] = _paths,
            ["components"] = _components,
        };
    }

    public SpecDocumentBuilder WithOpenApiVersion(string version = "3.1.0")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        _root["openapi"] = version;
        return this;
    }

    public SpecDocumentBuilder WithRawSchema(string name, string fixtureName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(fixtureName);

        _schemas[name] = JsonNode.Parse(_fixtureLoader.Load(fixtureName));
        return this;
    }

    public SpecDocumentBuilder WithSchema(string name, Action<SchemaBuilder> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);

        var schema = new SchemaBuilder();
        configure(schema);
        _schemas[name] = schema.Build();
        return this;
    }

    /// <summary>
    /// Adds an empty schema under <paramref name="name"/> verbatim, so the name may be any text a
    /// JSON key can hold.
    /// </summary>
    public SpecDocumentBuilder WithEmptySchema(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        _schemas[name] = new JsonObject();
        return this;
    }

    /// <summary>
    /// Adds a reusable response under <paramref name="name"/> verbatim, so the name may be any text
    /// a JSON key can hold.
    /// </summary>
    public SpecDocumentBuilder WithResponseComponent(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (_components["responses"] is not JsonObject responses)
        {
            responses = [];
            _components["responses"] = responses;
        }

        responses[name] = new JsonObject
        {
            ["description"] = "Response",
        };
        return this;
    }

    public SpecDocumentBuilder WithConfigPluginTuple(Action<SchemaBuilder> configureTuple)
    {
        ArgumentNullException.ThrowIfNull(configureTuple);

        return WithSchema("Config", schema => schema
            .Type("object")
            .Property("plugin", plugin => plugin
                .Type("array")
                .Items(item => item.AnyOf(
                    branch => branch.Type("string"),
                    configureTuple))));
    }

    public SpecDocumentBuilder WithOperation(string operationId, string method = "get", string path = "/api/x",
        Action<OperationBuilder>? configure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var operation = new OperationBuilder(operationId, _fixtureLoader);
        configure?.Invoke(operation);

        if (_paths[path] is not JsonObject pathItem)
        {
            pathItem = [];
            _paths[path] = pathItem;
        }

        pathItem[method] = operation.Build();
        return this;
    }

    public SpecDocumentBuilder WithRawTopLevel(string key, string rawJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(rawJson);

        _root[key] = JsonNode.Parse(rawJson);
        return this;
    }

    public SpecDocumentBuilder WithRawTopLevelFromFixture(string key, string fixtureName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(fixtureName);

        return WithRawTopLevel(key, _fixtureLoader.Load(fixtureName));
    }

    public string BuildJson() => _root.ToJsonString();
}
