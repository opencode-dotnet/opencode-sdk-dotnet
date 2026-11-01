using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenCode.Sdk.Tools.Generator.Refresh.Models;

namespace OpenCode.Sdk.Tools.Generator.Refresh;

/// <summary>
/// Pure, read-only probes over an OpenAPI document's bytes: the operation set, its digest, the
/// component count, the <c>contentSchema</c> signal, and the patch predicates' keyword and style checks.
/// The synchronizer never interprets documents beyond these invariants — semantic understanding
/// stays with ingestion.
/// </summary>
internal static class DocumentInspector
{
    public static string Sha256Hex(ReadOnlySpan<byte> content) => Convert.ToHexStringLower(SHA256.HashData(content));

    public static IReadOnlyList<string> CompareStats(DocumentStats stats, SnapshotReceipt receipt)
    {
        var problems = new List<string>();
        if (!string.Equals(stats.OperationSetDigest, receipt.OperationSetDigest, StringComparison.Ordinal))
        {
            problems.Add("the operation-set digest does not match the receipt");
        }

        if (stats.OperationIds.Count != receipt.OperationCount)
        {
            problems.Add("the operation count does not match the receipt");
        }

        if (stats.ComponentCount != receipt.ComponentCount)
        {
            problems.Add("the component count does not match the receipt");
        }

        if (stats.ContentSchemaCount != receipt.ContentSchemaCount)
        {
            problems.Add("the contentSchema count does not match the receipt");
        }

        return problems;
    }

    public static DocumentStats Inspect(byte[] documentBytes)
    {
        ArgumentNullException.ThrowIfNull(documentBytes);

        using var document = Parse(documentBytes);
        var root = document.RootElement;
        var operationIds = ReadOperationIds(root);
        var componentCount = 0;
        if (root.TryGetProperty("components", out var components)
            && components.ValueKind is JsonValueKind.Object
            && components.TryGetProperty("schemas", out var schemas)
            && schemas.ValueKind is JsonValueKind.Object)
        {
            componentCount = schemas.EnumerateObject().Count();
        }

        return new DocumentStats
        {
            OperationIds = operationIds,
            OperationSetDigest = Sha256Hex(Encoding.UTF8.GetBytes(string.Join('\n', operationIds))),
            ComponentCount = componentCount,
            ContentSchemaCount = CountPropertyOccurrences(root, "contentSchema"),
        };
    }

    public static KeywordPresence CheckComponentKeyword(byte[] documentBytes, string component, string keyword)
    {
        ArgumentNullException.ThrowIfNull(documentBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(component);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyword);

        using var document = Parse(documentBytes);
        if (!document.RootElement.TryGetProperty("components", out var components)
            || components.ValueKind is not JsonValueKind.Object
            || !components.TryGetProperty("schemas", out var schemas)
            || schemas.ValueKind is not JsonValueKind.Object
            || !schemas.TryGetProperty(component, out var schema)
            || schema.ValueKind is not JsonValueKind.Object)
        {
            return KeywordPresence.ComponentMissing;
        }

        return schema.TryGetProperty(keyword, out _) ? KeywordPresence.Carries : KeywordPresence.Lacks;
    }

    public static KeywordPresence CheckParameterStyle(byte[] documentBytes, string operationId, string parameter, string style)
    {
        ArgumentNullException.ThrowIfNull(documentBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(parameter);
        ArgumentException.ThrowIfNullOrWhiteSpace(style);

        using var document = Parse(documentBytes);
        var found = false;
        foreach (var operation in ReadOperations(document.RootElement))
        {
            if (!operation.TryGetProperty("operationId", out var id)
                || id.ValueKind is not JsonValueKind.String
                || !string.Equals(id.GetString(), operationId, StringComparison.Ordinal)
                || !operation.TryGetProperty("parameters", out var parameters)
                || parameters.ValueKind is not JsonValueKind.Array)
            {
                continue;
            }

            foreach (var candidate in parameters.EnumerateArray())
            {
                if (candidate.ValueKind is not JsonValueKind.Object
                    || !candidate.TryGetProperty("name", out var name)
                    || name.ValueKind is not JsonValueKind.String
                    || !string.Equals(name.GetString(), parameter, StringComparison.Ordinal))
                {
                    continue;
                }

                found = true;
                if (!candidate.TryGetProperty("style", out var declaredStyle)
                    || declaredStyle.ValueKind is not JsonValueKind.String
                    || !string.Equals(declaredStyle.GetString(), style, StringComparison.Ordinal))
                {
                    return KeywordPresence.Lacks;
                }
            }
        }

        return found ? KeywordPresence.Carries : KeywordPresence.ComponentMissing;
    }

    private static JsonDocument Parse(byte[] documentBytes)
    {
        try
        {
            return JsonDocument.Parse(documentBytes);
        }
        catch (JsonException exception)
        {
            throw new SnapshotRefreshException($"the document is not valid JSON: {exception.Message}", exception);
        }
    }

    private static List<string> ReadOperationIds(JsonElement root)
    {
        var operationIds = new List<string>();
        foreach (var operation in ReadOperations(root))
        {
            if (operation.TryGetProperty("operationId", out var operationId) && operationId.ValueKind is JsonValueKind.String)
            {
                operationIds.Add(operationId.GetString()!);
            }
        }

        operationIds.Sort(StringComparer.Ordinal);
        return operationIds;
    }

    private static IEnumerable<JsonElement> ReadOperations(JsonElement root) =>
        root.TryGetProperty("paths", out var paths) && paths.ValueKind is JsonValueKind.Object
            ? paths.EnumerateObject()
                .Where(static path => path.Value.ValueKind is JsonValueKind.Object)
                .SelectMany(static path => path.Value.EnumerateObject())
                .Select(static member => member.Value)
                .Where(static value => value.ValueKind is JsonValueKind.Object)
            : [];

    private static int CountPropertyOccurrences(JsonElement element, string propertyName)
    {
        var count = 0;
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, propertyName, StringComparison.Ordinal))
                    {
                        count++;
                    }

                    count += CountPropertyOccurrences(property.Value, propertyName);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    count += CountPropertyOccurrences(item, propertyName);
                }

                break;
            case JsonValueKind.Undefined:
            case JsonValueKind.String:
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
            default:
                break;
        }

        return count;
    }
}
