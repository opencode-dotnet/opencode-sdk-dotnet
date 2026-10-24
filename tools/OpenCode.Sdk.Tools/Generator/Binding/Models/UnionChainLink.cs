using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

internal sealed record UnionChainLink(string Name, string MarkerWireName, string MarkerName,
    LiteralKind MarkerKind, IReadOnlyList<HoistedMemberPlan> HoistedMembers,
    UnionPrefixVariantPlan? PrefixVariant, IReadOnlyList<UnionVariantPlan> Variants, bool IsTerminal);
