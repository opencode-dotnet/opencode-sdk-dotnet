# Union membership is an interface carrying what every member shares

Date: 2026-09-12

A generated **marked union** is emitted as an interface, and each wire schema stays one
`sealed record` implementing every union it belongs to. Membership is not expressible as a base
class because a schema can belong to more than one union: 41 of the 43 branches of
`Session.Event.Durable` are also direct branches of `V2Event`, so the same leaf must answer to both
the durable log stream and the live event bus. C# allows one base class, and the binder already
refuses this by name — *"schema cannot derive from both '…' and '…'"* — which would leave
`event.subscribe`, the surface every upstream front-end consumes (internal research, 2026-08-17,
"sdk-next, the embedded model, and why the HTTP surface is safe to build on"), permanently
ungenerable. Interfaces make membership plural, and because both unions discriminate on the same
wire field the leaf satisfies both contracts with one property.

The interface declares the union's discriminator and every property its members already agree on.
A union whose members each carry the same shape but no common type forces a consumer to switch over
concrete types to read it: 43 arms of the durable session log each declare a `durable` envelope of
`{ aggregateID, seq, version }`, so a generic relay had to name 43 leaves to read a sequence
number. Upstream's TypeScript consumers reach the same value structurally (`"durable" in event`);
the .NET projection loses that unless the interface says it. Hoisting is projection, not invention:
every member already declares the property, and the wire contract is untouched.

## Scope: marked versus structural unions

A marked union has a common literal discriminator whose value selects an object schema:

```json
{ "type": "session.created", "data": {} }
{ "type": "session.deleted", "data": {} }
```

`SessionCreated` and `SessionDeleted` are wire schemas in their own right, so implementing `IEvent`
expresses membership without wrapping either object. A structural union has no such marker; its
branch is selected from the JSON value shape instead:

```json
"hello"
42
true
["a", "b"]
```

Primitive and collection values cannot implement a generated interface. Token-distinct structural
unions therefore use the generated carrier decision in ADR-0016, not this membership mechanism.

## The hoisting rule

Mechanical over the pinned document, with no per-union curation and no admission list:

- **Identity.** A property is hoisted when every member the union dispatches to declares it with
  the same wire name, the same required-ness, the same nullability, and the same represented type.
  Identity ignores the value of a non-dispatch literal — a `const` or single-value `enum` that
  discriminates nothing is an ordinary primitive on both sides (ADR-0004), so the per-event schema
  version `1` or `2` is the same `double`. Descriptions and other documentation-only keywords never
  reach the bound plan and are ignored by construction. A union with fewer than two members shares
  nothing across arms and hoists nothing.
- **Dispatch stays dispatch.** The discriminator is never hoisted, and neither is any property a
  union in the chain reads to dispatch: a nested union's marker, or an outer marker a nested union
  fixes. Discriminator scan order and dispatch are untouched, and a
  prefix-tagged arm is an ordinary member for its non-discriminator properties.
- **Inheritance.** A nested union inherits what its outer union promises and does not redeclare it.
- **Carriers.** When every member promotes its own record for one identical shape, the generator
  emits one interface for those records and declares the member with it. The records are not
  collapsed: one wire schema stays one record (ADR-0004), and each implements the interface beside
  its own identity. The rule applies to those records' own members in turn.
- **Nullability.** Every hoisted member of a union interface is nullable, because that union's
  `Unknown*` carrier holds a raw payload and materializes no typed member (ADR-0009). A required
  member is therefore `T?` on the interface and `T` on every known member, and the carrier answers
  `null`. A hoisted record carrier has no unknown arm, so its members stay exactly as the records
  declare them.
- **Implementation.** A member whose own property already answers the declared type implements it
  implicitly; a value type against `T?`, and a promoted record against its carrier, implement it
  explicitly. An explicit interface implementation is not a public property, so System.Text.Json
  never sees it and the serialized shape is exactly what it was before the member was hoisted.
- **Naming.** A record carrier is named `I<UnionConcept><Property>` — deterministic from the
  declaring interface and the wire property, with nesting resolved the same way from the carrier
  that declares it. A reason-bearing curation row may choose a different .NET name for one, which
  is naming a represented construct rather than adding wire semantics (ADR-0013); the durable
  envelope carries such a row so it reads `IDurableEnvelope`. A name another generated type already
  owns refuses by name.

Hoisting adds no converter and no serializer registration: a record carrier is a plain interface
that nothing deserializes into, and the union's own converter is unchanged.

**Fail-closed walls this admits.** Two unions a leaf belongs to may declare markers with different
names — the leaf then carries both properties, which the wire object must anyway to stay
discriminable in both contexts. Same name with a different kind is refused instead: one JSON field
cannot be both a string and a number, so that is a contradiction in the spec rather than a shape to
model. A hoisted carrier name that collides with another generated type refuses rather than
shadowing it, and a naming row no hoist answers refuses as a decision no longer taken.

Mechanism verified before sealing, by compile and round-trip probe: `JsonConverterAttribute`
targets interfaces; a source-generated context deserializes into an interface-typed member and into
the interface directly; interface inheritance answers `is`; an explicit interface implementation is
invisible to source-generated metadata, so a durable event serializes byte-for-byte as it arrived.
The `net472` leg compiles here and is exercised by the Windows CI matrix (ADR-0002).

## Consequences

- A union interface grows when the pin gives its members a new shared property, and shrinks when
  the pin takes one away. That is the same reviewed-diff surface as any other generated change, and
  it is a binary break for an external implementer of the interface — of which none is known or
  intended, because these interfaces exist to be consumed, not implemented. Hand-written types that
  stand in for a union member inside this repository are fixed in the same change.
- The member-growth hazard that keeps client types free of interfaces does not apply: the members
  come from the pinned document, not from SDK design choices.
- A property the members type differently is not hoisted and stays a per-leaf property. A shared
  value whose arms each bind a separate generated enum is such a case — three `reason` enums are
  three CLR types, and an enum cannot implement an interface — so it fails open rather than being
  approximated.

## Considered options

- **Keep abstract-record bases and refuse the collision** — fail-closed and already implemented,
  but it makes the live event bus ungenerable, against a complete generation profile.
- **Flip a union to an interface only when a leaf gains a second parent** — mechanical, but a
  spec refresh would then turn a shipped `abstract record` into an interface; the extend-only
  evolution posture exists to prevent exactly that.
- **Duplicate a leaf type per union** — rejected: it breaks one type per wire schema (ADR-0004)
  and makes a durable event untypable where a live event is expected.
- **Give the nested family a base that derives from the outer union** — rejected: it would claim
  live-bus membership for `session.usage.recorded`, which the spec denies. Projection never
  invents (ADR-0003).
- **Keep the discriminator as the interface's whole member set** — rejected: it is the smaller
  emitter, but it pushes an exhaustive concrete-type switch onto every generic consumer, including
  this repository's own test suite, and such a switch goes quietly incomplete at the next refresh
  that adds an arm.
- **Collapse the identical promoted records into one type** — rejected: it deletes 43 public types
  and breaks one type per wire schema, and it would fuse shapes the pin keeps separate and may
  diverge.
- **Have the unknown carrier project the hoisted member from its raw payload** — rejected: the
  carrier exists because the payload's meaning is unknown, so reading a typed member out of it
  would invent one (ADR-0003), and a body with no envelope has nothing to read.
- **Expose a `TryGetDurable`-shaped accessor instead of a nullable property** — rejected: honest
  for the carrier, but it gives 43 known arms an out-parameter dance for a value they always carry.
