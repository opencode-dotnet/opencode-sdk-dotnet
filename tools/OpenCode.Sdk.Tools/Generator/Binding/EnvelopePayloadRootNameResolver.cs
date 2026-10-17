using System.Collections.ObjectModel;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

internal static class EnvelopePayloadRootNameResolver
{
    /// <summary>Names success payloads promoted out of envelope spine by operation identity, not their projection keys.</summary>
    /// <remarks>
    /// A success payload that ingestion promoted into the graph carries no schema identity of
    /// its own: it was promoted out of envelope spine — the operation's own inline root, or a
    /// wrapper the dialect never names — and neither spelling belongs on the public surface.
    /// The rule covers bare roots, data members and location data (the list item or single object).
    /// Component payloads keep their identity; schemaNames rows override. Cursor-list items stay
    /// nominal (ADR-0017), so that position has no promoted key to claim.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> Resolve(SpecDocument document,
        IReadOnlyList<SpecOperation> selected, BindingErrorCollector errors)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var operation in selected)
        {
            if (operation.Responses.FirstOrDefault(static response => response.StatusCode is 200) is not
                { IsSse: false, Schema: RefNode reference, } success)
            {
                continue;
            }

            // Cursor-list items stay nominal, so that shape has no promoted key to claim; the
            // remaining shapes never reach a payload at all.
            var payloadKey = success.EnvelopeShape switch
            {
                SpecEnvelopeShape.Bare => reference.Target,
                SpecEnvelopeShape.Data => ResolveDataMemberKey(document, reference.Target),
                SpecEnvelopeShape.DataLocation => ResolveDataLocationPayloadKey(document, reference.Target),
                // A single-key wrapper's value is nominal or a primitive shape the type
                // machinery names on its own, so that shape never promotes a payload key here.
                SpecEnvelopeShape.SingleKey or SpecEnvelopeShape.CursorData
                    or SpecEnvelopeShape.DataHasMore or SpecEnvelopeShape.None => null,
                _ => null,
            };
            if (payloadKey is null || !payloadKey.Contains('#', StringComparison.Ordinal))
            {
                continue;
            }

            // A bare root embeds the operation id and cannot be shared, but a component data
            // wrapper can be: two operations claiming one payload under different names refuse
            // rather than letting iteration order pick a winner.
            var payloadName = OperationNamePolicy.PayloadTypeName(operation);
            if (names.TryGetValue(payloadKey, out var existing) && !StringComparer.Ordinal.Equals(existing, payloadName))
            {
                errors.Add(
                    BindingErrorCategory.Naming,
                    payloadKey,
                    $"envelope payload is claimed as both '{existing}' and '{payloadName}' by selected operations");
                continue;
            }

            names[payloadKey] = payloadName;
        }

        return new ReadOnlyDictionary<string, string>(names);
    }

    /// <summary>
    /// Resolves the wrapper member the payload binder reads, matching the single-required-<c>data</c>
    /// shape <c>EnvelopeFacetBinder.BindDataPayload</c> accepts; any other wrapper refuses there.
    /// </summary>
    private static string? ResolveDataMemberKey(SpecDocument document, string wrapperKey) =>
        document.Schemas.TryGetValue(wrapperKey, out var wrapper)
        && wrapper is ObjectNode { Properties: [{ Name: "data", IsRequired: true, Schema: RefNode payload, }] }
            ? payload.Target
            : null;

    /// <summary>Resolves location data to the array's item key or the single value's key.</summary>
    /// <remarks>
    /// The shape check is <see cref="EnvelopeWrapperShape.IsDataLocation"/>, the same call
    /// <c>EnvelopeFacetBinder.ResolveDataLocationWrapper</c> makes, so what this claims and what
    /// the binder reads cannot drift apart. The caller filters component
    /// keys (no '#'), which also keeps references to named arrays out.
    /// </remarks>
    private static string? ResolveDataLocationPayloadKey(SpecDocument document, string wrapperKey)
    {
        if (!document.Schemas.TryGetValue(wrapperKey, out var target) || target is not ObjectNode wrapper)
        {
            return null;
        }

        if (!EnvelopeWrapperShape.IsDataLocation(wrapper, out var data) || data is null)
        {
            return null;
        }

        return data.Schema switch
        {
            ArrayNode { Item: RefNode item } => item.Target,
            RefNode payload => payload.Target,
            _ => null,
        };
    }
}
