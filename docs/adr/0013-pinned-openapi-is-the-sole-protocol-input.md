# Pinned OpenAPI is the sole protocol-semantic input

Date: 2026-08-26

The generator derives wire types, constraints, statuses, schemas, media types, and protocol
extensions exclusively from the pinned `spec/openapi.json`. Upstream TypeScript and Effect schema
source is provenance and diagnostic evidence, never a generation input: consuming it would create
a second contract, require executing or reimplementing upstream's schema runtime, and make an
internal refactor look like HTTP drift. A construct the OpenAPI projection cannot represent stays
faithful to the document or fails closed; it is not silently repaired from implementation source.
The pinned document itself is produced through the receipt-governed snapshot process (ADR-0020);
this record governs what generation consumes from it. ADR-0029 adopts one bounded second artifact
under this record's reversal trigger: upstream's pinned codegen rule for the input a
trailing-wildcard route carries, mirrored rather than read and guarded by the source watch.
ADR-0037 admits a generated type that follows upstream's decode instead of the document, as a
reason-bearing, source-watched, gated and self-retiring exception.

Curation may choose .NET names and placement, collapse OpenAPI shapes proven equivalent,
fingerprint exclusions already evidenced by the document, and map an operation whose upstream
identity violates upstream's own conventions onto its intended identity through a reason-bearing
operation-identity row that carries the upstream report and retires when the fix lands. It may
not add a wire type, format,
range, cross-field rule, or runtime validation absent from the pinned artifact. Descriptions remain
documentation, not executable constraints. A projection discrepancy is rechecked against current
upstream, recorded as research, and reported upstream rather than becoming a hidden local override.

## Consequences

- Naming, grouping, envelope-property naming, validated aliases, and operation exclusions remain
  legitimate curation because they organize the represented contract without enriching it.
- Property/parameter type overrides, name-derived semantics, and behavior-premised validation are
  outside curation. Existing rows and heuristics in those categories must be removed or replaced by
  faithful OpenAPI representation.
- The generated .NET surface can be less ergonomic than upstream's first-party client when
  upstream's OpenAPI projection loses a decode transform. Deterministic single-source fidelity
  wins over a nicer API maintained from private implementation knowledge.

## Reversal Trigger

Reconsider only if upstream publishes a richer versioned machine-readable contract, or this
project deliberately adopts a second pinned, reproducible protocol artifact through a new ADR.

Evidence: internal research, 2026-08-17, "What may curation change?" and "Which evidence follows
from the reset?".
