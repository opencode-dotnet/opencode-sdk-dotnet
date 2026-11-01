# Known union variants are read by generated readers, with source-generated metadata as their oracle

Date: 2026-11-02

A known variant of a generated union, and a record nested beneath a variant of the message, event and
session-log unions, is materialized by a reader the generator emits for that record, not by
System.Text.Json's source-generated object metadata. The reader reads the members, applies the
record's required, nullable and fixed-marker rules, and constructs the record once.

This amends ADR-0009's mechanism, which materializes known values through the source-generated
context. The union converter still scans a copied reader for the discriminator, then hands a known
variant to its generated reader. The rest of ADR-0009 stands:
- an unknown discriminator lands in the union's unknown carrier with its raw `JsonElement`;
- writes go through the source-generated metadata;
- no reflection path exists.

## Why

On the large message (2,400 parts) and a prompt-event fixture (1,024 delta frames and 16 text-ended
frames), measured in exact bytes on .NET 10, the metadata path's cost is the record shell, not the
union dispatch. Dispatching through the union adds no bytes over reading the concrete records, and
reading each variant through its own metadata converter, as the SDK now does, saves none.

Readers of the generated kind, written by hand for the variants and nested records on those paths,
cut the large message from 5,098,776 B to 3,086,288 B (−39.5%). They cut the prompt fixture from
1,450,368 B to 663,424 B (−54.3%, about 638 B per frame). Of that, the nested records' readers
account for −303,520 B on the large message and −146,304 B on the prompt fixture. Their gain comes
from records repeated per part or per frame. .NET 11 allocates about the same on the metadata path,
so no runtime change in hand closes the gap.

## The semantic contract

A generated reader accepts and refuses exactly what the metadata path accepts and refuses, under the
context's options: case-sensitive names, `RespectNullableAnnotations`, duplicates last-wins,
unmapped members skipped, strict number handling by default, the default depth.
- **Refusals:** a missing required member, a JSON `null` for a non-nullable member without an in-band
  null representation, and a wrong token kind refuse. A `null` is refused at once, even when a later
  duplicate would have replaced it. A failure reports the same exception, path and message as the
  metadata path, and leaves the reader where the metadata path leaves it.
- **Nullable members:** a `null` for a nullable member reads as `null`, dictionaries included. The
  reader takes the null-token branch itself, because a direct converter call bypasses the
  serializer's null handling.
- **Represented values:** represented nulls stay represented: a `JsonElement` member reads a JSON
  `null` in band. Property-level conversion rules stay the property's, such as a member that admits
  named floating-point literals.
- **Shape tolerance:** escaped property names, reordered members, and a discriminator in any position
  read as they do today.

Measured, this handling is free: readers that follow it allocate exactly what readers that skip it
do.

## What keeps metadata

- **Prefix-tagged variants,** such as the event union's `rpc.` variant.
- **Open-model records,** which retain additional members (`[JsonExtensionData]`).
- **Nested records beneath the other unions' variants:** about 2,700 lines on paths no benchmark
  measures.

The decision takes effect variant by variant. Until the change that converts a variant merges, that
variant keeps metadata, and canon describes it so.

## The oracle

The test suite carries a second serializer configuration that materializes every record, nested
unions included, through metadata alone. It reads every fixture and every generated corpus value both
ways and compares the results; a difference fails the build. The configuration shares no generated
reader with the product path. Named mutants prove that: a reader that drops a member, and a dispatch
to the wrong variant, each fail the comparison.

## Considered options

- **Reading each variant through its own metadata converter, and stopping there:** it saves time, not
  bytes. It shipped first.
- **Changing the public model shape** (settable members, dropping `required`): it reverses ADR-0004
  and breaks the public API.
- **Lazy dictionary storage:** API-neutral, and decided separately.
- **The hot unions only:** narrower than chosen. The other unions' variants are about 4,100 more
  reader lines.
- **Every nested record:** the other unions' nested records add about 2,700 lines with no measured
  gain.

## Consequences

- **Generated code:** grows by the readers of every union variant and of the measured families'
  nested records.
- **The emitter:** gains a model-aware reader path in the hottest part of the generator.
- **The oracle:** every schema change runs through the differential test.
- **Canon** changes with the code that makes it true: `protocol-and-generation.md`'s version-skew and
  serialization sections name generated readers for the unions each change converts, and
  `testing-style.md` names the oracle when its harness lands.

## Reversal trigger

Reconsider if System.Text.Json's metadata path closes most of the measured gap, if C# unions
(ADR-0036) bring a cheaper construction path, or if the differential test finds drift the emitter
cannot close without per-schema exceptions.
