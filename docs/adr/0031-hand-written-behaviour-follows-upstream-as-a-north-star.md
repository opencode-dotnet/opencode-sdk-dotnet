# Hand-written behaviour follows upstream's first-party behaviour as a north star

Date: 2026-10-04

Everything the SDK implements by hand rather than generates takes upstream's first-party TypeScript behaviour at the pinned release as its reference. That covers the standalone launcher, the background-service doors (ADR-0025), the terminal doors, and the transport and error behaviour around them. The reference runtime is the build upstream ships on its `latest` channel, Bun. The default is to do what upstream does. When a refresh shows upstream changed a behaviour the SDK mirrors, the SDK follows.

The SDK diverges only for one of three reasons:

- it is the idiomatic .NET form of the same contract;
- it is measurably safer or more robust, argued from its mechanism and, where timing or process state matters, probed on every supported OS;
- it is a superset: upstream's behaviour plus something more.

Every divergence is recorded in the canon of the behaviour it concerns, with its reason and the trigger that would reverse it.

The SDK does not:

- behave in a way the server, its child processes, or the consumer would see as different in kind;
- drop a capability to match upstream (the launcher's stderr tail stays);
- copy byte for byte what nobody can observe.

A divergence that matters is scheduled and closed, not parked. Whether it matters is a judgement, not an assumption.

## Considered options

- **Match upstream exactly, invisible details included.** Rejected. It would mean, for example, reproducing JavaScript's `JSON.stringify` number formatting, or removing the stderr tail the launcher keeps for startup diagnostics. That costs a great deal and nobody can observe the result. It would also give up improvements that come from using .NET idiomatically.
- **Treat upstream only as inspiration.** Rejected. Consumers run the SDK beside the CLI and the desktop app against the same server and the same state, and they expect the same lifecycle. The source watch exists so that upstream's behaviour changes are reviewed, not ignored.

## Consequences

- Each refresh's review sorts every watched behaviour change into one of two outcomes: follow it, or record a divergence with its reason.
- Each divergence list lives with the canon it concerns: `docs/architecture/client-runtime.md` for the launcher, the background-service doors, and the terminal doors.

Evidence: internal research, 2026-10-04, "How closely do the hand-written doors follow upstream, and when may they diverge?".
