# A stream payload whose known variant cannot be read arrives as the unknown variant

Date: 2026-10-25

On the event bus and the session log, a frame whose declared variant fails to deserialize arrives as the union's unknown
variant, with its discriminator and raw payload, whenever that unknown-variant carrier can hold the frame. The stream goes on.

This follows upstream. Its first-party interface reads both streams through its promise client, which parses each frame's
JSON and casts it, so a payload that has drifted from its declared shape reaches the consumer. Failing the frame would
reconnect the bus, losing the events not yet read and those published while it was down, and it would end the session log.

What still fails:
- **A frame the carrier refuses**, with its original decode failure as the cause: invalid JSON, a value that isn't an
  object, a missing or unusable discriminator, or one the union's prefix-tagged arm claims.
- **A null payload**, with its own failure.
- **A stream payload union that declares impossible tags**, which is refused at generation.

On these two streams, then, ADR-0009's retained `JsonElement` is paid for by a declared variant that couldn't be read as
well as by an unknown one. Call results and every other union keep ADR-0009 as written.

## Considered options

- A new public carrier that also keeps the decode reason: it adds public surface before any consumer has asked for the
  reason. It stays open as a later API decision.
- Keeping the failure: the SDK would drop events that upstream's own client delivers.

## Reversal trigger

Reconsider if upstream's promise client starts validating stream payloads against their schemas, or if a consumer needs the
decode reason, which would bring back the carrier that keeps it.
