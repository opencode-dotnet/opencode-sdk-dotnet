# Generated unions stay interfaces and carriers; C# unions come as sibling types

Date: 2026-10-14

C# 15 adds union types. Upstream's protocol is full of unions, which the SDK represents as hoisted
interfaces over sealed records (ADR-0011) and as structural carriers (ADR-0016). The maintainer
wants C# unions in the SDK. The question for `1.0.0` was whether they replace that representation,
or arrive later.

**Decision.** `1.0.0` keeps the interfaces and carriers. An existing generated type never gains a
C# union declaration. C# unions arrive additively, as sibling types over the same records, in a
minor version after `1.0.0`. Making them the primary representation is a major-version decision.

**Why.**

- A consumer compiler without C# 15 refuses pattern matching directly on a union type. Such a
  consumer has to match on the union's value, which loses the exhaustiveness check that is the point
  of a union. Most consumers of the `net10.0`, `net472` and `netstandard2.0` assets will compile
  with such compilers for years. The interface model works on every language version the SDK
  supports.
- Adding a union declaration to a type that already shipped is source-breaking: type patterns on
  that type stop compiling. A later opt-in on the same types is therefore not additive, and only a
  sibling type keeps every existing consumer compiling.
- Upstream's arm sets are open, and refreshes add arms. Exhaustive union matching turns each added
  arm into a compiler warning, and into a run-time failure for a consumer that was not recompiled.
  The interface model's default arm absorbs an added arm.
- System.Text.Json cannot tell the SDK's object-shaped arms apart by itself, on either the newest or
  the downlevel serializer. The SDK's own converters stay in either representation, so unions bring
  no simplification of the reading side.

**Consequences.** Consumers on C# 15 get union matching only once the sibling types ship. A sibling
union is not an arm: passed where the interface is expected, it defeats type tests on the arm, so the
SDK never hands one out in place of an interface value.

**Reversal trigger.** Once compilers with union support are the floor of the consumers the SDK
supports, a major version may make unions the primary representation.

Evidence: internal research, 2026-10-14, "C# 15 unions on the .NET 11 RC2 SDK" (a library-side
probe and a consumer-side spike across `net11.0`, `net10.0`, `net472` and `net8.0` consumers on
the .NET 10 and .NET 11 compilers).
