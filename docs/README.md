# Documentation Index

Current scope, gaps and acceptance each have exactly one maintained entry, so that a development log cannot
turn into a product promise.

## Current status at a glance

- **Core ADO.NET surface implemented**: connections (sync/async), commands, parameters, readers, typed
  results, local transactions, savepoints and `GetSchema`, in both MySQL and Oracle tenant modes.
- **Opt-in extensions implemented**: physical pooling, idle statement cache, batches and multi-send
  pipelining, MySQL multi-statements/LOCAL INFILE/compression, Oracle procedures, `OUT`/`INOUT` parameters,
  REF CURSOR, LOB locators, OBJECT/collection values and array binding, and explicit XA.
- **Live evidence is narrow**: direct OceanBase 4.4.2.2 endpoints, Windows, `TLS Mode=Disabled`, selected
  subsets only. Every deployment claim must name `server version + tenant mode + direct/ODP + protocol +
  TLS/auth + client OS/runtime + options`.
- **Cannot be verified in the current environment** (recorded, not treated as unsupported): TLS/mTLS and
  enterprise CA/revocation/rotation, OB20/full-link trace/compressed OB20, OBProxy/ODP/multi-node/OCP/TNS
  discovery, non-native authentication/KDC, real Linux/macOS runtime matrices, and long-running stability or
  node-failure scenarios. The gap matrix keeps these as blockers with the exact evidence needed to close them.
- **Known server-side limitations** are listed individually so they stay visible without being counted as
  passing client results.

## Documents

| Document | Purpose |
| --- | --- |
| [Feature and capability matrix](obconnector-dotnet-progress.md) | What the SDK must provide, MySQL/Oracle differences, option/type/direction boundaries and current implementation. |
| [Missing-feature and verification matrix](obconnector-dotnet-completeness.md) | Missing implementation, limited subsets, candidates, unverified areas, server-permission blockers (G01-G15, E01-E15) and exclusions. |
| [Development tracking list](obconnector-dotnet-missing-features-roadmap.md) | **Single tracking entry for future development**: tasks D/A/S, progress, server prerequisites, scope decisions, pinned evidence sources. |
| [Acceptance criteria](acceptance-criteria.md) | Test layers, minimum assertions, complete live/security/package gates and closing conditions. |
| [Compatibility and known limitations](compatibility.md) | What each capability guarantees, what the current endpoints do not prove, and retained failures/skips. |
| [Connection strings](connection-strings.md) | Every supported connection-string keyword, its default and purpose, validation rules and usage examples. |

## Reference contracts

- [Architecture and API details](obconnector-dotnet-architecture-design.md): configuration, pooling, TLS,
  ambient transactions, discovery and every documented behavior contract.
- [Failure handling and recovery](failure-handling.md): unknown-outcome classification, the application
  verification pattern, and the opt-in network/XA/topology acceptance harnesses.
- [Diagnostics](diagnostics.md): protocol events, OB20/trace acceptance, session I/O, source-read timing and
  pool metrics.
- [Credentials](credentials.md): built-in and custom credential providers and rotation.
- [Character sets](client-character-sets.md): strict client/result/national encoding mappings.
- [Schema](schema.md): `GetSchema` collections, restrictions and permissions.
- [Connector/J architecture outline](obconnector-j-architecture-outline.md): vendor reference only, never
  accepted as evidence for this provider.

The architecture document also contains historical phase design. Where its schedule or scope differs from the
current matrices, the matrices win; API details must still be checked against the source.

Process logs, stage snapshots, per-module walkthrough records and pause checkpoints have been removed; git
history is the archive. Failures, skips and unverified TLS boundaries in the current documents take precedence
over any historical success record.
