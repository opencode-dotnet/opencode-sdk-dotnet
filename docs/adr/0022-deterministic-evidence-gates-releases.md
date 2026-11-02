# Deterministic evidence gates releases; live-model evidence never does

Date: 2026-08-26

Release-gating evidence must be deterministic and anchored to the accepted snapshot: exact-pin
real-server tests and the same-commit simulated-model session workflow block, a scheduled
latest-tip canary is the intended non-blocking drift signal, and agent-driven real-provider runs are external
dated evidence that earns no coverage or gate credit. The rejected alternative — letting live-model
or moving-target runs gate — ties green to nondeterministic model behavior and upstream's daily
churn instead of to the contract a release actually ships against. Reversal trigger: an explicit
release-policy decision that deliberately promotes external acceptance evidence.

Evidence: internal research, 2026-08-25, "What did the coverage-program grilling seal, and what did
its fact-finding measure?".
