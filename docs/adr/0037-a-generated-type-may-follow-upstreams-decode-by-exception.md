# A generated type may follow upstream's decode, by exception

Date: 2026-10-15

ADR-0013 stands as the default: the generator takes every wire type from the pinned document, and
curation never retypes what the document declares. Some of the document's types are projection
losses, though. Upstream's server decodes the value into another type, and upstream's own
first-party client takes that decoded type. Several query parameters are the common case: the
document projects them as strings, the server reads them as numbers, and upstream's client takes a
number. Mirroring the projection there gives the SDK a type upstream's own client does not have,
and fixing it after 1.0 would be a breaking change.

A generated type may therefore follow upstream's decode instead of the document, as a deliberate
exception that meets all four conditions:

- **A reason.** A curation row names the subject, the type it binds to and why.
- **Evidence.** The row quotes upstream's decode schema in a file the source watch pins, so a
  refresh that changes the decode stops for review.
- **A fail-closed gate.** Every subject of the kind is classified: it either carries a row or is
  listed as kept by design, with its reason, and an unclassified one fails generation. A test also
  holds the bound types to upstream's generated client at the pin, in both directions.
- **Retirement.** A row fails generation once the document itself declares the type, or no longer
  carries the subject, so it is removed instead of outliving its reason.

This adopts upstream's decode schemas and generated client as a second pinned artifact under
ADR-0013's reversal trigger, bounded to these rows; it does not reopen curation in general. Its
first and only current application is query parameters that the document projects as strings and
upstream decodes as integers. They bind as .NET integers, the wire still carries their decimal
text, and the server keeps enforcing their ranges.

## Considered options

- Keeping the document's strings until upstream fixes its projection: the SDK would ship a type
  upstream's own client does not use, then break it later.
- A curation type override with a reason alone: nothing would notice when the decode changes or a
  new parameter of the same kind arrives.

## Reversal trigger

Reconsider if upstream's document starts carrying the decoded types (every row then retires), or if
upstream's generated client stops being a reproducible artifact of the pin.
