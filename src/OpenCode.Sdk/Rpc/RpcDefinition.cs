using System.Text.Json.Serialization.Metadata;

namespace OpenCode.Sdk;

/// <summary>A plugin's RPC definition: its registered id and typed method and event descriptors.</summary>
/// <remarks>Declare it once in a static field with the JSON metadata of a source-generated context, as upstream's Rpc.define declares a definition.</remarks>
public sealed class RpcDefinition
{
    /// <summary>Declares the RPC id the plugin registers.</summary>
    /// <param name="id">The RPC id.</param>
    /// <exception cref="ArgumentNullException"><paramref name="id"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="id"/> is empty or whitespace.</exception>
    public RpcDefinition(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Id = id;
    }

    /// <summary>Gets the RPC id the plugin registers.</summary>
    public string Id { get; }

    /// <summary>Makes the typed descriptor of one method of this definition.</summary>
    /// <param name="name">The method name.</param>
    /// <param name="input">The JSON metadata of its input.</param>
    /// <param name="output">The JSON metadata of its output.</param>
    /// <typeparam name="TInput">The method's input type.</typeparam>
    /// <typeparam name="TOutput">The method's output type.</typeparam>
    /// <returns>The method's descriptor.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/>, <paramref name="input"/> or <paramref name="output"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or whitespace.</exception>
    public RpcMethod<TInput, TOutput> Method<TInput, TOutput>(string name, JsonTypeInfo<TInput> input, JsonTypeInfo<TOutput> output)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        return new RpcMethod<TInput, TOutput>(this, name, input, output);
    }

    /// <summary>Makes the typed descriptor of one event of this definition.</summary>
    /// <param name="name">The event name.</param>
    /// <param name="data">The JSON metadata of its data.</param>
    /// <typeparam name="TData">The event's data type.</typeparam>
    /// <returns>The event's descriptor.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="data"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or whitespace.</exception>
    public RpcEventDefinition<TData> Event<TData>(string name, JsonTypeInfo<TData> data)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(data);
        return new RpcEventDefinition<TData>(this, name, data);
    }
}
