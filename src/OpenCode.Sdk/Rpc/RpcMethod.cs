using System.Text.Json.Serialization.Metadata;

namespace OpenCode.Sdk;

/// <summary>One method of an RPC definition, made by <see cref="RpcDefinition.Method{TInput,TOutput}"/> and called through <c>client.Rpc.CallAsync</c>.</summary>
/// <typeparam name="TInput">The method's input type.</typeparam>
/// <typeparam name="TOutput">The method's output type.</typeparam>
public sealed class RpcMethod<TInput, TOutput>
{
    internal RpcMethod(RpcDefinition definition, string name, JsonTypeInfo<TInput> input, JsonTypeInfo<TOutput> output)
    {
        Definition = definition;
        Name = name;
        Input = input;
        Output = output;
    }

    /// <summary>Gets the definition that declares this method.</summary>
    public RpcDefinition Definition { get; }

    /// <summary>Gets the method name the plugin registers.</summary>
    public string Name { get; }

    /// <summary>Gets the JSON metadata used to serialize the input.</summary>
    public JsonTypeInfo<TInput> Input { get; }

    /// <summary>Gets the JSON metadata used to read the output.</summary>
    public JsonTypeInfo<TOutput> Output { get; }
}
