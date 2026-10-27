# Session protocol I/O durations

The `OceanBaseConnector` meter exposes `oceanbase.client.protocol.io.duration`, a
`double` histogram with unit `s`. Enable it with a standard .NET `MeterListener` or
your metrics collector. No connection option is required.

Each sample is the sum of timed protocol calls in **one command exchange and one
direction**, not a packet count or an individual socket latency. An exchange emits
at most one `read` and one `write` sample, only for directions actually used.
Preparation/control requests and queued statement closes within that exchange are
included. Transactions, XA, cursor fetches and LOB operations that use the same
exchange mechanism are included; this is not restricted to SQL execution.

| Label | Values |
| --- | --- |
| `oceanbase.protocol.direction` | `read`, `write` |
| `oceanbase.compatibility_mode` | `mysql`, `oracle` |
| `oceanbase.outcome` | `success`, `error`, `canceled`, `timeout` |

The outcome describes the final exchange result, so an otherwise successful write
followed by a failed read has `error` on both samples. Cancellation and timeout use
the exchange's mapped exception. These labels contain no SQL, parameters, endpoint,
credentials or exception text. They do not distinguish confirmed failure from an
unknown execution outcome: the exception and existing operation diagnostics remain
authoritative. A metric never authorizes retrying a write.

Timing covers packet/payload read and write calls, including framing, buffering,
copying, compression/OB20 codec work and any synchronous protocol observers within
those calls. It also includes their network/server waits. It is neither socket-only
time nor server-only SQL time. Buffered calls may complete without socket I/O.

Application gaps between reader/stream calls, application-side row processing and
upload-source reads are excluded. Parameter and LOCAL INFILE source reads have their
independent [source-read histogram](source-read-diagnostics.md); LOCAL INFILE packet writes are
included here. Handshake/TLS/authentication and pool waits have their separate
[connection phase metrics](obconnector-dotnet-architecture-design.md#connection-phase-diagnostics).
Do not add these durations together as an exact decomposition of command or Open
elapsed time; scope and lifetime differ.

An exchange starts accumulating only if this histogram is enabled when the
exchange is created. Enabling a listener later does not instrument that exchange
retroactively. Protocol timing and source timing are enabled independently. With neither
histogram enabled no shared accumulator is created; disabled async wrappers
return the underlying tasks directly. Enabled async wrappers can allocate state
machines; no measured overhead guarantee is claimed.

Publication occurs after the exchange releases its physical lease, invokes its
registered release callback and completes any pending timed calls. Higher-level
transaction/XA/LOB reservation guards can still be active; callbacks do not have a
general guarantee that they can start another operation on that connection.
Streaming exchanges publish when they complete, fail or
are disposed, rather than after every row. An empty exchange emits no sample.
Concurrent pending completions share one publication. This histogram's own listener
execution time is excluded. Callbacks are synchronous and must return promptly;
callback exceptions are isolated and `Activity.Current` is restored. Existing
operation spans and slow-operation diagnostics retain their separate clocks.

Local regression entry points are `SessionIoDiagnosticsTests.cs` and
`DiagnosticsConsumerTests.cs`. Local fixture measurements establish neither real
OceanBase/OBProxy interoperability nor workload, overhead or cross-platform limits.
