using System.Collections.Frozen;

namespace OpenCode.Sdk.Internal;

/// <summary>The operation-specific rules carried by a call's immutable description.</summary>
/// <param name="Reconnects">Whether stream entry selects the reconnecting owner.</param>
/// <param name="HasNoNetworkWindow">Whether an unset per-call window ignores the client window.</param>
/// <param name="CarriesMediaCapability">Whether this read can return experimental media references.</param>
internal readonly record struct OperationTraits(bool Reconnects, bool HasNoNetworkWindow, bool CarriesMediaCapability = false)
{
    private static readonly FrozenDictionary<string, OperationTraits> TraitsByOperation =
        new Dictionary<string, OperationTraits>(StringComparer.Ordinal)
        {
            ["event.subscribe"] = new(Reconnects: true, HasNoNetworkWindow: false),
            ["experimental.generate.decisions"] = new(Reconnects: false, HasNoNetworkWindow: true),
            ["experimental.generate.image"] = new(Reconnects: false, HasNoNetworkWindow: true),
            ["experimental.generate.text"] = new(Reconnects: false, HasNoNetworkWindow: true),
            ["experimental.session.wait"] = new(Reconnects: false, HasNoNetworkWindow: true),
            ["rpc.call"] = new(Reconnects: false, HasNoNetworkWindow: true),
            ["session.command"] = new(Reconnects: false, HasNoNetworkWindow: true),
            ["session.generate"] = new(Reconnects: false, HasNoNetworkWindow: true),
            ["session.inbox.list"] = new(Reconnects: false, HasNoNetworkWindow: false, CarriesMediaCapability: true),
            ["session.message.get"] = new(Reconnects: false, HasNoNetworkWindow: false, CarriesMediaCapability: true),
            ["session.message.list"] = new(Reconnects: false, HasNoNetworkWindow: false, CarriesMediaCapability: true),
            ["session.shell"] = new(Reconnects: false, HasNoNetworkWindow: true),
        }.ToFrozenDictionary(StringComparer.Ordinal);

    public static OperationTraits Lookup(string operationId) =>
        TraitsByOperation.TryGetValue(operationId, out var traits) ? traits : default;
}
