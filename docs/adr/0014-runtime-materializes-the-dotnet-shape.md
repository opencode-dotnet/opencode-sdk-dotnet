# Runtime materializes the .NET shape without revalidating server schema

Date: 2026-08-18

The SDK validates transport and framing, parses JSON, and performs the checks required to
materialize the public .NET shape or dispatch a protocol union. It does not replay the server's
OpenAPI validation after a value is already representable in that shape. At an optional-property
boundary, omission and explicit JSON null collapse to the same absent CLR state, except at a
request-reachable optional-and-nullable property, where they stay apart in `Optional<T?>` and the
wrapper's `HandleNull` converter is what lets a JSON null arrive as the set-null state. At required
properties and present collection slots, JSON null materializes either as CLR null or as the
selected representation's canonical in-band JSON-null state. Collection children are not scanned
or normalized; non-discriminator fixed literals remain ordinary primitive properties; and declared
no-content responses ignore an unexpected body. `NoThrow` remains scoped to API errors: transport,
framing, JSON, dispatch, and impossible top-level materialization still throw
`OpenCodeTransportException`.

Requiredness and nullability are independent. A schema-required property emits `required`; a
property emits nullable C# when it is optional or when the selected representation requires CLR
null to materialize an admitted JSON null. A representation that source-generated
`System.Text.Json` proves can round-trip JSON null in-band does not gain `Nullable<T>` solely because
the schema admits null. `WhenWritingNull` applies only to optional properties, because a
required-nullable property must serialize an explicit null; a request-reachable
optional-and-nullable property uses `WhenWritingDefault` instead, because its absent state is the
wrapper's `default` rather than CLR null. Optional collections remain nullable
rather than normalizing absence to empty. Generated collections are shallow `init`-only
`IReadOnlyList<T>` / `IReadOnlyDictionary<string, T>` surfaces: the SDK does not copy or wrap
caller-supplied collections, and callers own later mutation.

Union discriminator checks remain because they select the concrete C# type; unknown tags retain
their explicit raw-payload carriers. Required members, non-null top-level payloads, enum token
kinds, SSE framing, response-status dispatch, and generated-source/AOT metadata remain
materialization or protocol walls rather than schema revalidation. Unknown values of generated
string enums: ADR-0035.

## Consequences

- Null-rejecting optional-property converters, collection-child scans, empty normalization,
  defensive collection copies, and non-discriminator literal validation are removed. The closed
  `Optional<T?>` converters are not that validation returning: they carry an admitted JSON null
  into a distinct CLR state instead of refusing it, and refuse nothing the schema admits.
- A valid UTF-8 one-shot success may materialize directly from bytes. Charset/BOM selection and
  malformed-UTF-8 replacement remain equivalent to `HttpContent` string decoding, so the fast path
  does not introduce a stricter one-shot decoder. Error bodies remain decoded strings for `RawBody`.
  A one-shot JSON success with one declared materializer does not validate response `Content-Type`;
  declared media types remain fail-closed generation inputs, and runtime media dispatch returns only
  when one status can select among several materializers. SSE keeps its protocol-specific media and
  UTF-8 framing rules.
- Concrete immutable collection types remain a pre-freeze benchmark/design question. Until that
  evidence exists, public collection surfaces stay `IReadOnly*` without claiming deep immutability.

Evidence: internal research, 2026-08-17, "Where does SDK runtime validation stop?" and the
questions after it on curation, evidence, and null materialization.
