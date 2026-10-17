namespace OpenCode.Sdk.Tools.Tests.Support;

internal static class SchemaPresets
{
    public static SpecDocumentBuilder WithObject(this SpecDocumentBuilder spec, string name, Action<SchemaBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(configure);

        return spec.WithSchema(name, schema =>
        {
            _ = schema.Type("object");
            configure(schema);
        });
    }

    public static void RequiredId(SchemaBuilder schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        _ = schema.Property("id", property => property.Type("string"), required: true);
    }

    public static SpecDocumentBuilder WithTaggedError(this SpecDocumentBuilder spec, string name, string tag)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(tag);

        return spec.WithObject(name, schema => schema
            .AdditionalPropertiesFalse()
            .Property("_tag", property => property.Type("string").Enum(tag), required: true)
            .Property("message", property => property.Type("string"), required: true));
    }

    public static SpecDocumentBuilder WithJsonOperation(this SpecDocumentBuilder spec, string operationId, string path,
        Action<SchemaBuilder> response)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(response);

        return spec.WithOperation(operationId, method: "get", path: path, configure: operation => operation
            .Response(200, "application/json", response));
    }
}
