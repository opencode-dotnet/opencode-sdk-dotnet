# Error model: typed exception spine carrying tagged error data

Date: 2026-10-16

API failures throw through a typed exception spine (`OpenCodeException` →
`OpenCodeApiException`) by default; per-call `NoThrow` returns them on the response spine.
The per-call options type carries that choice plus the typed `LocationSelector` location —
no arbitrary header dictionary (sealed 2026-08-26; internal research, 2026-08-25, "What did the
coverage-program grilling seal?").

Streams and automatic cursor traversals take the shared `OpenCodeRequestOptions`.
`Location` applies to each stream connection attempt and every page request. A stream yields
an `IAsyncEnumerable<T>` rather than a response envelope, so it has no envelope to carry an
API error; streams and automatic traversals refuse `NoThrow` at the call, before enumeration,
and always throw API errors when encountered. Explicit page methods retain `NoThrow`.
On `event.subscribe`, per-call `NetworkTimeout` is refused, including
`Timeout.InfiniteTimeSpan`: its 2-second connection and default 45-second idle windows follow
upstream's first-party behavior. Other streams apply `NetworkTimeout` only while opening;
automatic traversal applies it separately to every page request.

Upstream's `event.subscribe` takes `RequestOptions`. One shared options type across the
surface is chosen over a dedicated stream options type so callers use the same per-call
location and behavior vocabulary. The trade-off is that the compiler cannot prevent a caller
from selecting a member a stream or traversal must refuse at run time.

Transport and protocol failures always throw `OpenCodeTransportException`. The spec's tagged error
payloads are generated as typed models under an `OpenCodeError` base and ride **as data** on either
channel, pattern-matchable without string sniffing. There is no client-level switch. Throwing API
errors retain the raw body on `OpenCodeApiException`; `NoThrow` responses retain it on their shared
response spine, including when a typed error cannot be parsed. This deliberately renders upstream's
own idiom — tagged structural domain failures consumed as values — through .NET's nominal idiom
instead; nothing in the taxonomy is lost, and the per-call `NoThrow` path preserves
errors-as-values consumption where a call site wants it. Do not "fix" this back toward a
Result-first design without revisiting the four mechanisms in §4.2 of the internal public API
design spec (2026-08-09): the open error set (upstream regenerates the spec continually — a closed
union can never hold), the single error channel, the stream plane's structural need to throw, and
.NET ecosystem convention.

"Protocol failure" here means status/framing/dispatch failure, malformed JSON, or failure to
materialize the declared non-null .NET response shape. A representable value that merely violates
an OpenAPI constraint is not reclassified as transport failure; the server owns that validation
(ADR-0014). `NoThrow` therefore remains unrelated to duplicate schema checks.

## Considered options

- **Result/DU-style returns** — rejected on the four mechanisms; C# 14 has no unions,
  and if a later C# ships them, Result/Try companions can be added additively (the
  reverse migration would be a breaking major).
- **Upstream-mirroring structural guards** (type guards over exception identity) —
  rejected: exception identity is reliable in .NET; the JS constraint doesn't transfer.
- **Client-level error-behavior switch** — rejected on internal research, 2026-08-13,
  "Error-channel scope: per-call NoThrow only, or a client-level default too?": no throw-default
  SDK ships one (Azure/System.ClientModel/OpenAI are per-call only; AWS/gRPC/Octokit/Stripe have no
  switch; Elastic's client-level switch is escalation-only in the opposite direction; FDG: "DO NOT
  have public members that can either throw or not based on some option"). Recorded reversal
  trigger: an additive scoped no-throw sub-view if MCP-server dogfooding demonstrates the need.

Evidence: internal research, 2026-08-17, "sdk-next, the embedded model, and why the HTTP surface is
safe to build on" (upstream error contract), and 2026-08-13, "Error-channel scope"; §4 of the
internal public API design spec (2026-08-09); upstream call-site survey (hey-api `{data, error}`
default with generator-shipped `throwOnError` at per-call and client level; ~76 non-generated
opt-in sites in `packages/app`/CLI; the TUI reads `result.error.data` fields directly). MA0053's
`exceptions_should_be_sealed` option is off by default, so the unsealed exception hierarchy costs
no analyzer arbitration.
