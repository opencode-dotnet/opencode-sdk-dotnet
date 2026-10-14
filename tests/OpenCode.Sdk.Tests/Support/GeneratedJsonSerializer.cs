using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using OpenCode.Sdk.Models;

namespace OpenCode.Sdk.Tests.Support;

internal sealed class GeneratedJsonSerializer
{
    private const string ContextTypeName = "OpenCode.Sdk.Internal.OpenCodeJsonContext";
    private readonly JsonSerializerContext _context = LoadContext();

    public T Deserialize<T>(string json)
        where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize(json, GetTypeInfo(typeof(T))) as T
               ?? throw new JsonException($"The {typeof(T).Name} fixture deserialized to null.");
    }

    public string Serialize<T>(T value)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(value);
        return JsonSerializer.Serialize(value, GetTypeInfo(typeof(T)));
    }

    public JsonTypeInfo GetTypeInfo(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return _context.GetTypeInfo(type)
               ?? throw new InvalidOperationException($"The generated JSON context has no metadata for {type.Name}.");
    }

    private static JsonSerializerContext LoadContext()
    {
        var contextType = typeof(ISessionMessageInfo).Assembly.GetType(ContextTypeName, throwOnError: true, ignoreCase: false)
                          ?? throw new InvalidOperationException($"The generated JSON context '{ContextTypeName}' was not found.");
        var defaultProperty = contextType.GetProperty("Default", BindingFlags.Public | BindingFlags.Static)
                              ?? throw new InvalidOperationException("The generated JSON context has no Default property.");
        return defaultProperty.GetValue(null) as JsonSerializerContext
               ?? throw new InvalidOperationException("The generated JSON context Default property returned no context.");
    }
}
