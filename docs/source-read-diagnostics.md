# Upload source read durations

The `OceanBaseConnector` meter exposes `oceanbase.client.source.read.duration`, a
`double` histogram with unit `s`. Enable it through a standard .NET `MeterListener`
or collector. No connection option is required.

Each sample sums application stream `Read`/`ReadAsync` calls for one source kind
in one command exchange. It covers MySQL long-data parameters, Oracle piece-data
parameters and LOCAL INFILE uploads. Multiple streamed parameters and chunks share
one `parameter` sample. EOF and throwing reads count; an exchange that never reads
a source emits nothing. There is at most one sample per used kind per exchange.

| Label | Values |
| --- | --- |
| `oceanbase.source.kind` | `parameter`, `local_infile` |
| `oceanbase.compatibility_mode` | `mysql`, `oracle` |
| `oceanbase.outcome` | `success`, `error`, `canceled`, `timeout` |

Timing includes the source's own work and waits inside its read call. It excludes
stream-factory acquisition, stream disposal, packet sends/acknowledgements, result
consumption and gaps between source calls. It is not pure scheduler, disk or socket
wait time. [Protocol I/O](session-io-diagnostics.md) retains its separate clock,
including packet framing/codec work and network/server waits.

The outcome is the final mapped exchange outcome. Successful source reads followed
by a server error have `error`; a source token cancellation caused by the command
deadline has `timeout`. A sample does not establish whether SQL was sent or a write
committed. Use the operation exception's unknown-outcome classification; metrics
never authorize retries. Failed reads retain existing staged-data retirement rules.
Parameter streams remain caller-owned, while LOCAL INFILE streams remain driver-owned.

Labels contain no SQL, values, source names, filenames, parameter indexes, endpoint,
credentials or exception text. Publication shares the existing protocol accumulator:
after physical lease release, the registered release callback and all pending timed
calls, including a read still completing during exchange disposal. Concurrent stops
publish once. Higher-level transaction/XA/LOB guards can still be active; arbitrary
connection reentry from listeners is not guaranteed.

Source and protocol instruments are independently enabled when an exchange starts.
Enabling a listener later does not instrument that exchange retroactively. If neither
is enabled, no accumulator is allocated. A disabled instrument starts no stopwatch;
its asynchronous wrappers return the underlying tasks directly. Enabled timing can
allocate async state machines and uses the existing accumulator lock; no measured
overhead guarantee is claimed. Listener callbacks run synchronously, must return
promptly, have their exceptions isolated and restore `Activity.Current`. This
histogram's listener time is outside the timed source reads.

Local entry points: `SourceReadDiagnosticsTests.cs`, `SourceReadFixture.cs`,
`SourceReadConsumerTests.cs`, and the existing session-I/O/upload regressions.
Tests use synthetic loopback and require no real database or credentials. Five assets
loaded in one .NET10 host do not prove five independently tested runtimes, real
OceanBase/OBProxy interoperability, sustained load or other resource waits.
