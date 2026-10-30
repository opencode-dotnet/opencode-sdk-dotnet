# Protocol and Generation Architecture

Date: 2026-10-30

Canonical current rules for the protocol surface, generator, generated models, and runtime
materialization boundary. ADRs record why these decisions were made; dated research records the
evidence and may contain superseded positions.

## Protocol authority and surface

- The sole protocol-semantic input is the pinned `spec/openapi.json`, produced from the exact
  commit of upstream's `packages/protocol/openapi.json` at a pinned upstream release tag. Snapshot
  production and refresh policy are receipt-governed (ADR-0020); `spec/SNAPSHOT.md` owns the
  exact identity and the current procedure (ADR-0005, ADR-0013, ADR-0020).
- Upstream implementation source is provenance and diagnostic evidence only. It never supplies a
  missing wire type, constraint, format, status, media type, or validation rule (ADR-0013). One
  rule is the bounded exception: a route whose path ends in one trailing `/*` binds upstream's
  own codegen rule for it — a required `Path` member (`Wildcard` when a parameter already takes
  `path`), first in the operation's request record, that fills the path escaped segment by segment
  as upstream's `encodePath` escapes it, with a dot segment refused by name. The rule is mirrored,
  not read; the source watch pins the codegen file, and `tools/oracles/wildcard-path.ts` holds the
  SDK's request paths to upstream's client (ADR-0029).
- A generated type may follow upstream's decode instead of the document only as a deliberate
  exception (ADR-0037). The exception carries a reason-bearing curation row, quotes upstream's
  decode schema in a source-watched file as its evidence, sits behind a fail-closed gate (every
  subject of its kind is classified, and a test holds the bound types to upstream's generated
  client at the pin), and retires: a row fails generation once the document declares the type or
  drops the subject. Query parameters are the one kind today (Operations, streams, and exclusions).
- The public SDK covers the OpenCode 2.x protocol surface only. At the current pin, operation
  identities use dotted group/action segments without a version prefix. The longest dotted prefix of
  an identity that a `groups` row curates selects its client family, and the identity itself is
  never remapped. For most operations that prefix is the first segment, and `experimental.*`
  operations use the flat ExperimentalClient with explicit route arguments unless a longer curated
  prefix, such as `experimental.persistentPty`, owns them. Every upstream group id is
  `server.<name>`, so an endpoint that omits its own identifier leaks `server.<name>.<endpoint>`;
  any such group-qualified identity is refused until an explicit reason-bearing identity repair
  names it, and well-formed identities cannot be remapped (ADR-0005).
- An operation upstream serves under `/api/experimental/` is experimental. Its group's curation row
  names the family's diagnostic id (`OPENCODE` and three digits), and the generator marks with
  `[Experimental]` every public member that calls such an operation and every public type that only
  such operations reach; the hand-written doors over an experimental family carry the same id. A
  group with an experimental route and no id, or with an id and no experimental route, fails
  generation, and so does a type reached only by experimental operations of two different ids.
- Derived model names strip Effect's encode-side `*Encoded` suffix from a component's final segment
  whenever a name remains, whatever other components the document holds — a mechanical
  projection-artifact rule owned by `ProjectionArtifactNamePolicy`, never a per-row curation act. A
  stripped name that meets another schema's name refuses through the type-name collision wall, and
  a `schemaNames` row resolves it.
- A union with no schema identity of its own is named from where it sits: its owner, its property
  path and its branch markers, never from its branches. An inline object branch with exactly one
  required key that no sibling branch requires is keyed by that key, as a literal marker keys its
  branch, so its name survives a reorder. A name derived through an unmarked branch's ordinal needs
  a reason-bearing `schemaNames` row, and a union with more than one such branch refuses.

## Snapshot production

- The accepted snapshot is produced by the `refresh-spec` synchronizer (ADR-0020): prepare
  resolves a moving reference once to a full SHA and writes only scratch artifacts with a
  receipt; verify compares the accepted document, statistics, checkout, ordered patch set, and
  watched sources with the committed receipt observationally. Apply is a human act over one
  reviewed receipt: it completes validation and prepares every payload before changing any
  accepted artifact, then installs `spec/openapi.json`, renders the identity in `spec/SNAPSHOT.md`,
  re-pins `spec/source-watch.json` hashes, moves the submodule checkout, and writes
  `spec/receipt.json`. It never stages, commits, or pushes. A validation refusal leaves the
  accepted artifacts unchanged; the writes are not an atomic filesystem/Git transaction.
- Production always runs the exact pinned upstream generator in a scratch worktree of the
  candidate commit (`bun install --frozen-lockfile --ignore-scripts`, then the protocol package's
  `generate`); with an empty patch list its output is the normalized document. Upstream's
  committed artifact is never consumed: the receipt records its hash beside the generator's
  (`rawDocumentSha256`, `generatedBaselineSha256`), so a stale upstream copy is visible at every
  refresh. Restore patches —
  ordered and hash-pinned under `spec/patches/` with a manifest carrying the upstream report,
  touched files, repair predicate, and retirement condition — run through the exact pinned
  upstream generator after the unpatched baseline run, and prepare refuses a patch whose repair
  predicate that baseline already satisfies, forcing an empty-patch retirement refresh.
- The committed receipt records the exact inputs, hashes, blob-based patch preimages,
  operation-set digest, and operation delta of the accepted snapshot, together with the named
  release tag used to render its identity. Apply renders the date from the injected
  `TimeProvider`'s UTC date at apply time; the receipt carries no date.
  `refresh-spec --verify` is the standing observational check.
- The source watch (`spec/source-watch.json`) pins, by path, SHA-256 and one content anchor, the
  upstream files the hand-written doors read as inputs, the two upstream lists the generator's
  secret masking follows (ADR-0028), and the upstream rules the generator mirrors — Effect's union
  decode (ADR-0016), the trailing-wildcard codegen rule (ADR-0029), and the decode schemas that
  query parameter type rows quote (ADR-0037). It is a refresh-time review trigger that never reaches
  ingestion or emission; generation reads it only to refuse a type row whose evidence it does not
  watch (ADR-0013, ADR-0037).

## Construction pipeline

- The SDK has a hand-written behavior core. Generated models and operation methods come from the
  repository's own generator (ADR-0003, ADR-0008).
- The pinned `Microsoft.OpenApi` reader owns OpenAPI parsing. The generator owns a minimal,
  fail-closed semantic projection into SpecIR; it does not maintain a second OpenAPI parser
  (ADR-0003). The generator's reader runs the library's default validation rule set except the
  components-key rule, which the generator replaces with an exact equivalent without the library's
  100 ms wall-clock regex timeout
  ([microsoft/OpenAPI.NET#3101](https://github.com/microsoft/OpenAPI.NET/issues/3101)).
- Roslyn syntax trees own emission. The generator is repository tooling under `tools/`; output is
  committed under `src/OpenCode.Sdk`, reviewed as source, and regeneration-verified (ADR-0003).
- Generated output passes the analyzer wall on merit. The same tool owns deliberate spec refreshes
  and their generated diffs (ADR-0003).
- Generated files are changed through the generator, never by hand. The output manifest, not a
  folder or filename convention, identifies generator-owned files. After validation, the writer
  atomically publishes ownership of the old and new paths together before installing sources
  individually by temporary file and rename, then deleting stale sources. It formats in place in
  the real project, atomically installs the marker, attributes and reach files (or deletes optional
  files with no content), and atomically writes the new-only manifest last as the final commit point.
  An interrupted generation can be rerun because every touched source path is already manifested;
  an absent manifested path passes provenance validation. The writer does not adopt arbitrary
  unmanifested files. A formatter-interrupted file with a damaged provenance header still refuses;
  this sequence promises restartability, not filesystem-wide atomicity, concurrent writers or
  power-loss durability.
  The generated project must be restored before formatting so the formatter can remove redundant
  usings; the generator refuses otherwise and never restores the project itself.
- The repository tool forwards Ctrl+C cancellation to the running command and returns the configured
  cancellation exit code. Its composition root maps tool refusals to an unwrapped `Refused: <message>`
  diagnostic and exit code 1; other failures retain the CLI framework's error rendering and exit code.
- Generated and hand-written files share the slice folders (ADR-0033). One placement rule maps
  each generated file's kind and owning slice to its folder: models under the family's `Models/`
  (Sessions models in their curated area subfolders), body requests, operation records and
  clients at the family root, route tables, adapters, envelope DTOs, converters and internal-raw
  clients under the family's `Internal/`, the shared error spine under `Common/Errors/` and
  `Common/Internal/`, and the serializer registry under the root `Internal/Serialization/`. The
  writer reserves the folder names `Common`, `Internal`, `Hosting` and `Properties` for the shared
  homes, and `Models` and `Pagination` because the slice layout has no such root folder and a
  family may never recreate one; it marks every manifest entry `linguist-generated` in a
  writer-owned `.gitattributes` beside the manifest.
- A writer-owned `.generated-reach.txt` beside the manifest lists each generated type's owning
  slice and the families whose operations reach it, so a refresh shows reach changes in its
  diff; reach never moves a file. Neither file ships in a package.
- Pending (unselected and not transport-owned) operations are recorded in the committed
  `.generation-incomplete` marker, each carrying the bindability the same selection-path binder
  finds today — `[bindable]`, or `[refused: <verbatim wall messages>]` — so wall-free drift among
  pending operations surfaces as a reviewed diff instead of accumulating silently; the marker also
  lists the fingerprint-pinned transport-owned operations beside them while it exists. Selected
  operations are listed in `tools/generation-profile.txt`; admitting an upstream operation adds its
  id there, along with its naming and owner curation rows.

## Curation boundary

Curation may:

- choose .NET names and placement for represented OpenAPI constructs — handle placement
  follows ADR-0019. Every curation row, group rows included, carries its reason and passes one
  registered row contract that checks key presence, reason presence, and duplicate keys before
  per-kind existence, selection, redundancy, and stale checks. This includes the name of the
  carrier interface a hoisted union member is declared with, where the mechanical name is correct
  but graceless, the member name of one enum value where the mechanical casing is (an
  `enumMemberNames` row), and the C# parameter name of one route value where the mechanical one
  says less than the rest of the surface (a `parameterNames` row; a handle family's parameter
  takes one name across the family). Brand spellings (`OAuth`) are fixed in the name policy, so
  a word that spells one, in any case, is written the brand's way;
- name an operation the closed verb grammar cannot name, or names gracelessly, through a
  reason-bearing `operationNames` row. The closing segment is a verb only when the grammar lists
  it; a `GET` without one reads as `Get<Subject>Async`, and any other operation without one
  requires a row. The HTTP method supplies no name segment and removes none. Lists pluralize the
  last subject word, including the group fallback, for methods and envelope payload members by
  the regular rule and closed irregular table; a list naming row that restates that output
  refuses. A list whose last subject word ends in a single `s` requires an `operationNames` row,
  unless it is a handle method with no subject, and an `envelopePayloadNames` row for a derived
  payload member. Request, response, and operation-derived payload type names follow the
  operation identity, never an `operationNames` row (ADR-0008);
- declare that a family emits an internal raw layer rather than a public surface, where
  hand-written code owns that family's public doors (ADR-0021);
- document a generated type or one of its members where the wire says too little, through a
  reason-bearing `descriptions` row keyed by the generated C# type and optional member: its summary
  replaces the generated one and its remarks join any the type already carries; a row on an
  operation method may omit its summary when it carries remarks, so upstream's summary stays and the
  remarks follow the route remark. A row wins over a schema description, which wins over the summary
  the generator derives from an unambiguous wire role (an event, its exclusive payload, an
  operation's exclusive body, response or query record, an error of the error spine); a record with
  two roles keeps the plain placeholder. A row whose type or member is gone, a duplicate, or an
  empty summary or reason fails generation. A documentation-only `descriptions` row documents either
  an upstream fact or behaviour the SDK owns. An upstream fact (a unit, sentinel, default, format or
  meaning) cites upstream's explicit declaration as `packages/<path>:<line>` with a verbatim
  excerpt, proven against the pinned tree; a fact inferred only from upstream's implementation is
  not curated: it is reported upstream, and stated in the consumer guides where consumers need it.
  Behaviour the SDK implements itself, such as the event stream's reconnect contract, cites the
  canon section that owns it as `docs/architecture/<file>.md#<heading>` with a verbatim excerpt from
  that section, proven against the repository. A row may target a generated type, one of its
  declared members, or a generated client's operation method. Two committed inventories list the
  rows whose target upstream describes in the OpenAPI projection, and the rows whose cited
  declaration upstream does not describe; a refresh that changes either fails the Tools tests until
  the list is reviewed. A `descriptions` row never changes protocol semantics (ADR-0013). A curated
  interface member's documentation reaches each implementing public property without its own row
  through `<inheritdoc cref>`; different curated documentation from two owners for such a property
  refuses generation. In a curated summary or remark, a paired single-backtick span denotes inline
  code and renders as XML `<c>` text; all text remains XML-escaped;
- place a model in a slice where its name carries no family prefix, through a reason-bearing
  `modelOwners` row; a family's prefixes are its name, its singular, its group key and the
  group's `prefixAliases`, and a model with no prefix and no row fails generation. A
  `modelOwners` row whose model a family prefix or alias already places in the same family
  refuses as redundant. A reason-bearing `sessionAreas` row names a Sessions model area by name
  prefixes; a Sessions model no row matches stays in the family's `Models/`. A row, pattern or
  alias that places nothing refuses as stale (ADR-0033);
- collapse OpenAPI shapes proven structurally equivalent, ignoring the order in which an object
  declares its properties and emitting the surviving schema's order;
- fingerprint exclusions already evidenced by the pinned document; and
- map an operation whose upstream identity violates upstream's own conventions onto its intended
  identity through a reason-bearing operation-identity row carrying the upstream report; the row
  retires when upstream's fix makes it stale (ADR-0013).

A request-body or stream-cause claim renames a key only when its claiming edges, and references from
the keys the claim itself names, are its sole reachable referrers; when any other reachable schema
or operation root references it, binding refuses at that key, naming the referrers, and no curation
row overrides the refusal.

A route value whose mechanical name is a C# keyword requires a `parameterNames` row; a declared
header with such a name refuses.

The structurally-equivalent collapse is mechanical wherever upstream's stabilize suffix names it: a
reachable `<base>_<N>` component folds into `<base>` when the two are structurally identical. A
non-equivalent candidate remains distinct only through an explicit, reason-bearing `schemaNames`
row, and only when it is a nominal schema outside the response spine — an envelope wrapper or
operation-inline root is never a model, and a non-nominal shape has no model identity to
distinguish; every other non-equivalent candidate refuses by name, with or without a row. The
ordinary curation validity and resolved-name collision checks still apply. Naming never changes
either shape or prevents an equivalent pair from folding: a row naming a candidate that folds is
refused as redundant, so the refresh that makes a pair equivalent surfaces the dead row instead of
carrying it, and a `schemaAliases` row remains only for a duplicate no convention recognizes. This
distinction handles upstream identifiers reused for different public and event projections without
treating the numeric suffix as evidence of structural equivalence.

Curation may not add a wire type, format, constraint, cross-field rule, or runtime validation
absent from the pin, apart from ADR-0037's gated type exception. Descriptions generate
documentation, not executable semantics. Projection loss remains faithful or fails closed and is
reported upstream rather than repaired from private implementation knowledge (ADR-0013).

## Generated model shape

- Models are sealed records with `init`-only properties (ADR-0004).
- A numeric member keeps its schema type: `number` binds `double` and `integer` binds `long`, epoch
  timestamps included; neither the name nor the meaning of a member changes its type.
- OpenAPI presence and represented null are independent. A required schema member emits C#
  `required`; an optional property is nullable so omission and explicit JSON null share one absent
  state, except on a schema a selected request body reaches, where an optional-and-nullable
  property emits `Optional<T?>` and keeps absence, an explicit null, and a value apart (ADR-0004,
  ADR-0014).
- A tri-state request property emits `[JsonIgnore(Condition = WhenWritingDefault)]` beside a
  property-level `[JsonConverter]` naming the closed converter emitted for its instantiation: the
  wrapper is generic, so no type-level converter can name it; a special-number instantiation's
  converter reads and writes the named literals `NaN`, `Infinity` and `-Infinity` itself, because
  `[JsonNumberHandling]` is invalid on the wrapper member. A member-level converter costs the
  containing type its serialization fast path, which makes `JsonSourceGenerationMode.Metadata` on
  the emitted registry load-bearing rather than a performance preference (ADR-0004, ADR-0014).
- An object schema that declares named properties beside an unrestricted additional-properties
  schema — Effect's `StructWithRest` over `Record(String, Any)`, which the document encodes as a
  single-element `allOf` carrying only the rest's `additionalProperties` — emits its named members
  plus a public `AdditionalProperties` view, an init-only `IReadOnlyDictionary<string, JsonElement>`
  holding every other wire member by name (empty, never null, when the body carried none), so no
  member the server sent is lost. The serializer fills that view through an internal
  `[JsonExtensionData]` bag the model also carries: System.Text.Json deserializes extension data
  only through a settable member and refuses an init-only one, so the settable member is internal
  and the public surface keeps the init-only rule above; the bag is created on the first open
  member, so a body without one allocates nothing. A typed additional-properties schema beside
  named properties still refuses: no member could carry it without loss.
- A required or present value uses nullable C# only when the selected representation needs CLR null
  to materialize JSON null. A source-generation-proven in-band null carrier remains non-nullable;
  `JsonElement` currently carries JSON null through `JsonValueKind.Null` (ADR-0004, ADR-0014).
- A string declaring `contentEncoding: base64` materializes as `ReadOnlyMemory<byte>` — a
  represented token conversion the serializer performs natively; any other content encoding fails
  closed (ADR-0014).
- Optional collections remain nullable. Generated collection properties expose shallow
  `IReadOnlyList<T>` or `IReadOnlyDictionary<string, T>` references without defensive copies,
  read-only wrappers, empty normalization, or recursive child validation. Callers retain ownership
  of supplied collections (ADR-0004, ADR-0014). The open-model `AdditionalProperties` view above is
  the one exception: it copies on init and answers an empty view for an absent bag.
- A model with a secret member overrides `ToString()` and prints that member as `[REDACTED]` (empty
  when absent), every other member in the compiler's own shape. The floor is upstream's HTTP
  recorder field list with its matching rule; reasoned `redactedMembers` rows add or lift a mask;
  every open model's `AdditionalProperties` is decided by a reasoned `redactedOpenMembers` row
  (`redact: true` masks it, `redact: false` states it carries no credential), and the bind refuses
  an undecided one;
  a member whose wire name carries one of upstream's secret-marker words refuses the bind until a
  `redactedMembers` or `secretLookingNames` row decides it; a union whose known arms reach a masked
  member at any depth prints its unknown arm's preserved payload as `[REDACTED]` (ADR-0028).
- Only literals used to dispatch a union become constants or get-only properties. A prefix-tagged
  arm's discriminator is not a literal: it stays a required string property, proven on read to
  carry the prefix. Other fixed values remain ordinary primitives so a representable server value
  is preserved rather than revalidated locally (ADR-0004, ADR-0014).
- An envelope payload is accepted when its ingested schema binds to a supported type plan: named
  models, lists, dictionaries, inline objects promoted under deterministic operation-scoped
  names (reasoned naming rows override), and represented-nullable payloads — distinguished
  from the error path by response state, never by treating CLR null as an unset backing field.
  Location-envelope data may reference an array component or promote an inline object; the
  cursor-list item stays nominal (ADR-0017). Unsupported nodes fail closed, and no
  family-specific shape exception exists. A success body the operation declares inline as an
  object requiring exactly one property that is not `data` is envelope spine too: the payload
  flattens onto the response under that key's PascalCase name and the wrapper is never emitted
  as a model, while a named component with one property keeps its own identity. Requiredness is
  the binder's wall, not the classifier's: an inline object whose sole
  non-`data` property is optional still classifies this way but refuses at bind time
  (`EnvelopeFacetBinder.SingleKeyMember`, "must reference an object requiring exactly one
  property") rather than falling back to a promoted bare-body model — fail-closed, and not
  exercised by any operation in the pin today.

## Runtime materialization boundary

The runtime validates transport and framing, parses JSON, and performs the checks needed to
materialize the declared .NET shape or dispatch a protocol union. It does not replay server-side
OpenAPI validation for a value already representable in that shape (ADR-0014).

Required members, top-level payload presence, represented token conversion, the string token kind
of a generated enum, union dispatch, response-status selection, SSE framing, and source-generated
serializer metadata remain hard walls. A generated string enum is extensible (ADR-0035): an
extensible readonly struct whose pinned values are static properties, whose pinned spellings are
`const string` members of a nested `Values` class that a `case` label or a property pattern matches
against its `Value` property, whose generated converter
reads any string as a value and writes it back unchanged, refuses a non-string token, and refuses
to write a default value, and whose equality is ordinal. Declared no-content responses drain and ignore unexpected bodies; ordinary one-shot JSON
success decoding preserves `HttpContent` charset, BOM, and replacement-decoding behavior while
materializing valid UTF-8 directly (ADR-0014).

For stream payloads only, a `JsonException` from normal materialization can instead be represented
by the union's existing unknown arm when its converter accepts the frame (ADR-0038). The known
attempt still performs the checks above; one-shot results and failure causes keep their failures.

The exact standalone `not: {}` applicator is the dialect's never schema. The exact standalone
`not: {"type":"null"}` is how Effect renders a struct with no declared fields: nothing says what
the content is, so it binds as an object of unspecified content. Other `not` shapes refuse at
their applicator pointer rather than approximating general JSON Schema negation. A required never
member makes its object branch uninhabitable. Tagged unions preserve such a branch as a known
impossible tag in their bound plan, emit no dead public variant for it, and refuse that tag during
dispatch instead of routing it through ADR-0009's unknown carrier (ADR-0015). An optional never
member is a must-be-absent marker: the key may not appear, so it generates no property.

An unmarked union of a free-form object and an array of unrestricted values binds to
`JsonElement`, so both the object and the array that Effect's object keyword admits remain
materializable.

## Version-skew tolerance

- Every tagged union deserializes an unrecognized discriminator into that union's explicit unknown
  carrier, preserving the tag and raw `JsonElement` payload. A discriminator is recognized when it
  equals a declared literal tag or, where the union carries a prefix-tagged arm, starts with that
  arm's prefix; the carrier refuses a tag the prefix claims. Unknown frame names are not payload
  variants and remain framing failures (ADR-0009).
- Known tagged-union arms scan a copied UTF-8 reader for the last top-level discriminator and then
  materialize once through source-generated metadata; the scan remains safe when serializer stream
  entry points supply a partial reader, and it does not build a JSON DOM. Unknown arms retain
  the DOM needed by their lossless carrier, including a stream payload recovered after its known
  arm could not materialize. Direct union deserialization keeps the known-arm failure; only the
  stream adapter and reader retry through concrete unknown-arm metadata (ADR-0038). Public
  unknown-carrier construction requires the payload's discriminator (and any fixed outer marker)
  to agree with the exposed marker properties; serialization remains payload-only replay (ADR-0009).
  Dispatch order is fixed: declared literal tags, then the single prefix-tagged arm, then the unknown
  carrier.
- Every tagged union dispatches on one marker property. The error union's is `_tag`, which every
  error the pinned contract declares carries. An error response that references a schema without
  exactly one required `_tag` literal refuses by name; one that carries a required `name` literal
  instead is the `{name, data}` error dialect, which the generator does not admit, and refuses as
  that dialect (ADR-0007, ADR-0009).
- A tagged union may carry at most one prefix-tagged arm: a direct component object branch whose
  discriminator property is a string constrained by exactly Effect's TemplateLiteral projection
  `^<prefix>[\s\S]*?$`. The arm is the branch whose prefix marker sits on the union's
  discriminating property; a prefix marker on any other property — a templated identifier
  beside a literal tag — is inert, and that branch stays a literal variant. The literal-tagged
  branches fix the discriminator, so a marked union needs at least one; a union of prefix arms
  alone refuses as sharing no discriminating marker property. No literal tag of the union may
  start with the prefix, and the arm may not sit inside a nested union or be uninhabited; every
  other shape refuses by name. The live event union's `rpc.` arm is the only such arm today.
- A marked union is emitted as an interface declaring its discriminator and every property its
  members already agree on. One wire schema remains one sealed record implementing every marked
  union to which it belongs (ADR-0011).
- A property is hoisted onto that interface when every member the union dispatches to declares it
  with the same wire name, required-ness, nullability, and represented type, where identity
  ignores the value of a literal that discriminates nothing. No property any union in the chain
  dispatches on is hoisted, a nested union inherits what its outer union promises, and members
  that each promote their own record for one identical shape get a single generated carrier
  interface those records implement — deterministically named from the declaring interface and
  the wire property, overridable by a reason-bearing curation naming row. The records are not
  collapsed. Every hoisted member of a union interface is nullable because that union's unknown
  carrier materializes none of them; carrier interfaces have no unknown arm and keep the records'
  own nullability. Hoisting emits no converter, registers no serializer metadata, and leaves the
  wire shape unchanged: a member a record cannot satisfy directly is implemented explicitly, and
  an explicit implementation is not a public property (ADR-0009, ADR-0011).
- A token-distinct structural union emits one sealed carrier record with a `Kind`, guarded typed
  accessors and factories, an explicit raw unknown arm for unclaimed non-null value tokens, and a
  source-generated converter. Pinned
  branch order resolves subset overlap. Named object branches may share the object token: each
  gets a first-match claim — its required keys, and the values its literal- or enum-constrained
  properties admit — and the converter tries the claims in declaration order, deep-parses the
  first that holds, and keeps an object no claim holds as the raw unknown arm, which mirrors
  upstream's Effect union decode (`Union.getParser`). A branch an earlier claim always claims,
  and any other branch competing for an already claimed token, fails binding.
  `tools/oracles/first-match-union.ts` runs upstream's own decoder over a checked-in corpus, and
  a parity test holds the converters to its verdicts. Same-primitive refinements collapse without
  emitting dead branch models (ADR-0016).
- The interfaces and carriers above are the representation of every union. No generated type
  carries a C# union declaration. C# union types, when they come, are generated as additional
  sibling types over the same records, and the SDK never returns one where an interface value is
  expected (ADR-0036).
- Known objects skip additive unmapped fields, including when the pinned schema is closed. Required
  shape and represented token types remain materializable. Pure dictionaries retain their value
  schema; a named object combined with a typed additional-properties schema fails binding until both
  sides can be represented without loss, while an unrestricted one binds as the open model above
  (ADR-0012, ADR-0014).

## Operations, streams, and exclusions

- Every HTTP operation method, including streaming operations, is generated as a short delegation
  into the hand-written behavior core (ADR-0008).
- Bound root payload-union identity supplies a stream adapter's optional concrete unknown-arm
  metadata and scopes its carrier documentation; an adapter without such a union emits null.
  A stream payload union that reaches known-impossible tags, directly or through a nested union,
  refuses binding by root union, offending union, and tag name, so fallback cannot bypass
  impossible-tag dispatch. The behavior core learns no generated model name or operation identity
  from this wiring (ADR-0038).
- Every generated operation member, `Enumerate*Async` companions included, documents its operation
  identity and its HTTP method and route template in `<remarks>` as text only; no emitter branches
  on the identity (ADR-0008). A cursor-enumeration companion also links to its page operation and
  includes that operation's effective summary: the curated summary; otherwise the upstream summary
  or description, joined when both exist; otherwise the route fallback. The companion's remarks
  contain the route remark, the page operation's curated remarks, a link to the page operation,
  and the effective summary, in that order; its documentation has no separate curation target.
- An internal-raw family emits its clients sealed and internal — internal operation methods and
  handle factory, no mocking constructor — under raw type names, while the root client keeps the
  public family accessor its hand-written door answers. Only such a family may carry a
  document-declared request header: it emits as an optional trailing method parameter and travels
  the declared-header channel into the pipeline. The header value stays a caller concern and never
  enters curation or generated code, and a header-bearing operation forgoes the cursor-enumeration
  companion (ADR-0013, ADR-0021).
- A query binds the `ListRequest` base by inclusion: it must carry the admitted optional `limit`
  (typed `int` by its row), `order`, and `cursor` parameters in their admitted shapes, and every
  further optional parameter binds beside them as its own request property. A missing spine member,
  or one whose schema is not the admitted shape, keeps the request flat and unpaginated.
- A cursor-list operation emits an additive `Enumerate*Async` companion only when binding proves the
  admitted `ListRequest` query, `ListCursor` response, item collection, and operation signature, and
  the query declares no required parameter — a continuation must be constructible from no request at
  all. Generated response adapters project page items, the opaque next cursor, and the continuation
  request into one hand-written traversal core. Method names, descriptions, and upstream source do
  not independently confer pagination semantics (ADR-0013, ADR-0017).
- A query parameter binds by shape: a required parameter becomes a `required`, non-nullable
  request property and makes the request itself a required method and route argument; an optional
  parameter binds nullable and is omitted from the wire when unset, whether or not its schema
  admits JSON null. A plain string binds as the integer its `queryParameterTypes` row names, where
  upstream's server decodes it as a number and upstream's generated client takes one, or as a
  string under a reason-bearing `stringQueryParameters` row; a plain string with neither row fails
  generation, and a row whose parameter the document now declares as a number, or no longer
  carries, fails it too (ADR-0037). The integer travels as its invariant decimal text and the
  server enforces its range. The spine's own value sets (list order, the parent-session filter)
  and the optional deep-object location selector bind their spine types. The exact true/false text
  binds `bool?` for an optional parameter and `bool` for a required one, and the route builder
  writes lowercase `true` or `false`; an unset optional is omitted. Any other string enum becomes
  a generated extensible enum whose spelling the route builder writes as given, refusing a default
  value; every other query shape refuses by name.
- The SSE engine remains hand-written runtime behavior; generated stream methods bind their route,
  payload, frame, declared failure event, typed cause, and statuses from the pin. Cause models,
  converters, array metadata, and adapter metadata pass through the same emitted registry and
  System.Text.Json source-generation compile proof as payload models (ADR-0008, ADR-0015).
- Exclusion is reserved for transports the HTTP pipeline cannot carry, such as a WebSocket upgrade.
  Every excluded operation is fingerprint-pinned so protocol drift forces review (ADR-0008).
- Unknown response media types fail generation. Supported non-JSON response bodies follow the
  fail-closed content-type-to-payload mapping recorded by ADR-0008. An `application/octet-stream`
  success whose schema is `string` with `format: binary` binds a binary envelope: the buffered
  bytes as `Content` (`ReadOnlyMemory<byte>`) and the server's `Content-Type` as `ContentType`,
  materialized without a charset decode; its error statuses keep their JSON bodies (ADR-0029). A
  required `application/octet-stream` request body with the same schema binds as the caller's
  `Stream`, the last wire parameter after a standalone query record, which it never absorbs.

## Serialization and Native AOT

System.Text.Json source generation is mandatory. The generator emits the single serializer
registry used by product code; reflection fallback is not a product path. `IsAotCompatible` is
enabled on every modern target the packages build (`net10.0` and later), where the platform supports
that contract, as a target-framework range rather than a named target (ADR-0003).
