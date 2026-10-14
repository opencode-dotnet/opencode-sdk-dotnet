# Generated string enums are extensible

Date: 2026-10-14

A string enum of the pinned document is generated as an extensible value type: the pinned values
are named, and any other string the server sends is kept as it arrived and written back unchanged.
The SDK talks to servers newer than its pin: a user-installed background service, a launched
`opencode` from the PATH, or an explicit endpoint. With strict enums, one value added upstream made
an older SDK fail at run time, on a one-shot response and on every event stream frame that carried
it. Measured over the past pins, every value upstream added was one the SDK reads from a response or
an event. Removals break the other way: a newer SDK fails against an older server that still sends
a value the newer pin dropped.

The value type compares spellings ordinally, because upstream compares string literals exactly. It
reports whether a value is one the pin defines, so a consumer can detect a server newer than the
SDK. A default value has no spelling, and it is refused on the wire rather than sent empty. The
token kind stays strict: a number or another non-string token is still a malformed body.

## Considered options

- **Keep C# enums and fail on an unknown value.** This is what the decision replaces. It breaks
  every deployed consumer the first time a newer server sends a new value.
- **C# enums with an `Unknown` member.** This keeps `switch` and `case` labels, but it loses the
  value: an object read with an unknown value and written back (a configuration round trip) would
  change it.
- **A carrier with a kind enum and the raw value.** This is the pattern of the generated structural
  unions. It doubles the public types for every enum and still grows the kind enum on each refresh.

## Consequences

- Consumers can no longer use enum values as constant patterns (`case X.Value:`,
  `{ Member: X.Value }`). They compare with `==` instead. This breaks source compatibility once, and
  the change ships before 1.0.
- Package validation (ADR-0034) treats a new pinned value as an additive member. A removed pinned
  value is a removed member of a generated type, which a minor version may carry.
- A query parameter's spelling comes from the value itself, so a value the pin does not know can be
  sent too.
- This record supersedes the strict enum conversion that ADR-0014 lists among its materialization
  walls. The token-kind check is unchanged.

## Reversal trigger

Reconsider if upstream publishes a compatibility contract under which a server never sends a value
older clients do not know, or if C# gains an extensible enum construct that keeps constant
patterns.

Evidence:
- internal research, 2026-10-14, "Enum growth over the past pins" (memo B evidence);
- internal research, 2026-10-14, "Extensible enum emitter spike".
