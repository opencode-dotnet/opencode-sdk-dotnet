using System.Text.Json.Serialization.Metadata;

namespace OpenCode.Sdk;

/// <summary>One event of an RPC definition, made by <see cref="RpcDefinition.Event{TData}"/>.</summary>
/// <typeparam name="TData">The event's data type.</typeparam>
public sealed class RpcEventDefinition<TData>
{
    internal RpcEventDefinition(RpcDefinition definition, string name, JsonTypeInfo<TData> data)
    {
        Definition = definition;
        Name = name;
        Data = data;
        Type = "rpc." + definition.Id + "." + name;
    }

    /// <summary>Gets the definition that declares this event.</summary>
    public RpcDefinition Definition { get; }

    /// <summary>Gets the event name the plugin declares.</summary>
    public string Name { get; }

    /// <summary>Gets the event type the server publishes it under.</summary>
    public string Type { get; }

    /// <summary>Gets the JSON metadata used to read the event's data.</summary>
    public JsonTypeInfo<TData> Data { get; }
}
