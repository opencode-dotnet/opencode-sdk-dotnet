# Structural unions use generated carriers with token and first-match dispatch

Date: 2026-10-30

An untagged structural union emits one sealed record carrier plus a `Kind` enum, guarded typed
accessors, factories, and a custom converter. The converter follows pinned branch order, dispatches
through source-generated metadata, preserves an unclaimed valid non-null value token as the
carrier's raw `Unknown` arm, and refuses malformed content after a branch is selected. This is
separate from ADR-0011: marked object schemas implement membership interfaces, while values such as
`string | number | boolean | string[]` require a carrier because CLR primitives and collections
cannot implement a generated interface.

A branch is selected by its JSON token kind. Named object branches may share the object token; each
then carries a first-match claim — its required keys, and the values its literal- or
enum-constrained properties admit — and the converter tries the claims in declaration order,
deep-parses the first that holds, and keeps an object no claim holds as `Unknown`. That is the rule
upstream's own decoder applies (Effect's `Union.getParser`: candidates narrowed by runtime type and
literal sentinels, then the first member in declaration order that decodes), and the server encodes
these bodies from typed values, so the wire always came from exactly one member. A branch an
earlier claim always claims is unreachable and fails binding, as does any other branch competing
for an already claimed token — a dictionary or free-form object takes every object, so nothing
beside it can be told apart. `tools/oracles/first-match-union.ts` runs upstream's decoder over a
checked-in corpus and records which member takes each value; a parity test holds the generated
converters to those verdicts.

For overlapping `string | special-number`, the earlier broad string branch owns the named
`"NaN"`, `"Infinity"`, and `"-Infinity"` spellings; the remaining number arm admits ordinary JSON
numbers. A non-finite `double` constructed through that number arm cannot write a JSON number and is
refused by `Utf8JsonWriter`; callers use the text arm for those exact wire spellings. Collapsed
same-primitive refinements produce no carrier and no dead branch models. A string enum beside a free
string is an open enum: it binds the generated extensible enum with the enum's values, which accepts
any other string, never a plain string.

## Considered options

- Plain `JsonElement` was smaller but discarded deterministic typed branches the pin exposes.
- One wrapper record per arm made pattern matching direct but multiplied public types for every
  primitive, collection, and future `Model | Model[]` union.
- Reusing the marked-union interface shape was impossible without synthetic wrappers because
  `string`, `double`, and `IReadOnlyList<T>` cannot implement an SDK interface.
- For object branches sharing a token: refusing them left `config.get` and
  `experimental.migration.v1.status` unusable, and with them the only configuration read; trying
  each branch's deep parse in turn would follow Effect exactly on malformed input too, but decides
  by exception and buries the reason a value was not claimed. The claim decides before any deep
  parse; a value whose selected branch then fails to parse is refused rather than offered to the
  next branch, which only a value the server did not encode from a typed member can reach.

The decision was compile- and round-trip-probed with reflection fallback disabled. Reconsider if C#
gains a package-compatible native union representation, if upstream's Effect release changes its
union decode (the source watch pins the catalog line, and the oracle re-runs at that refresh), or if
a pinned union needs object branches that no required key or literal sentinel tells apart.
