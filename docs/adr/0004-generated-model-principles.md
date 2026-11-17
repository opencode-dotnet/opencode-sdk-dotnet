# Generated models: init-only, required-mirroring, schema-shaped null representation

Date: 2026-08-18

Generated models are sealed records with `init`-only properties. Schema presence and represented
null are independent: a required member emits the C# `required` modifier; an optional property emits
nullable C# so omission and explicit null collapse to one absent state, except on a schema a
selected request body reaches, where an optional-and-nullable property emits `Optional<T?>` and
keeps absence, an explicit null, and a value apart. A required property whose
selected representation uses CLR null emits `required T?` when its schema admits null, accepts an
explicit null, and writes it back rather than omitting the member. When a representation carries
JSON null in-band, its canonical non-null CLR state represents wire null instead of `Nullable<T>`.
`JsonElement` is the current carrier through `JsonValueKind.Null`.

Collections follow the same outer-property rule. Optional lists/dictionaries are nullable;
required collections remain required. Present list slots and dictionary entries have no omission
state, so their item/value types use nullable C# only when the selected representation requires CLR
null. A source-generation-proven in-band JSON-null carrier remains non-nullable. The public surface
uses `IReadOnlyList<T>` / `IReadOnlyDictionary<string, T>` without defensive copies, read-only
wrappers, empty normalization, or recursive item/value validation. `IReadOnly*` is an API view, not
a deep-immutability guarantee; caller-supplied collection ownership stays with the caller
(ADR-0014). The open-model `AdditionalProperties` bag is the exception; its ownership rules live in
[Generated model shape](../architecture/protocol-and-generation.md#generated-model-shape).

A literal used for union dispatch emits as a constant/get-only property because successful
dispatch already proved it; a prefix-tagged discriminator emits as a required string because
dispatch proved only its prefix. Constructing the arm directly with a value outside its prefix, or
deserializing such a value through the arm's own context rather than the union, surfaces that guard
as an `ArgumentException`. Other boolean, numeric, and string literals remain ordinary primitive
properties so the SDK preserves the wire value instead of validating or silently normalizing a
representable server contradiction.

Evidence for the model/nullability decision: internal research, 2026-08-17, "Where does SDK runtime
validation stop?" and the questions after it on curation, evidence, and null materialization.

## Generator policy

- **`Uri` only for OpenAPI `format: uri`;** a property name or upstream implementation source
  cannot supply a missing format through curation (ADR-0013). Filesystem paths stay `string`.
- **Acronyms use ordinary PascalCase regardless of length** (`Id`, `Io`, `Ip`, `Url`,
  `Api`, `Pty`, `Mcp`, `Tui`); brand spellings use curated exceptions (`OAuth`).
- **Identifier mapping is mechanical** — every wire name maps to PascalCase with
  `[JsonPropertyName]` carrying wire fidelity (`_tag`, snake_case, dotted schema
  names).
- **`WhenWritingNull` only on optional properties;** required-nullable members must retain an
  explicit JSON null. Optional schema-non-null and schema-nullable values intentionally share the
  same C# representation wherever a request body does not reach the schema.
- **A request-reachable optional-and-nullable property emits `Optional<T?>`** with
  `WhenWritingDefault` and a closed property-level `[JsonConverter]` per instantiation, because the
  wrapper is generic and no type-level converter can name it. `default` is absent and is not
  written; a set null writes JSON null; a value assigns through an implicit conversion.
  `JsonSourceGenerationMode.Metadata` on the emitted registry is load-bearing for that member-level
  converter, not a performance preference. Reachability is the whole key: no verb, no curation row,
  and no server knowledge participates, and a schema reached from both directions carries one
  representation.
- **In-band JSON-null carriers stay non-nullable;** admission requires source-generated
  serialization evidence that JSON null materializes as a canonical non-null CLR state and writes
  back as JSON null in every supported runtime context. This is a representation capability, never
  an endpoint/property curation rule.
- **Guard emission** — every generated method opens with BCL throw-helper guards;
  CA1062 at `error` guards the contract; invariants otherwise live in the type system
  (NRT, `required`, init-only assignment, guarded getters).
- **Fixed-literal emission** — union discriminators are constants; non-discriminator literals
  remain ordinary primitive properties with no duplicate server-schema validation.
- **XML documentation emission** — generated docs come from spec
  `summary`/`description`; operation methods emit `<exception cref>` lists from
  declared error responses; CS1591 becomes `error` (hand-written surface documented by
  hand).
