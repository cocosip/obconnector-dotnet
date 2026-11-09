# Diagnostics

The driver exposes four independent telemetry surfaces: protocol events, session I/O timing, upload
source-read timing, and pool lifecycle measurements. None of them requires a connection option, a
package, or a listener to be useful only online. All of them emit fixed scalar fields and never place
SQL, parameters, rows, credentials, endpoints, or exception text into an event.

<a id="protocol"></a>

## Protocol diagnostics

Subscribe to the standard .NET `DiagnosticListener.AllListeners` stream and select listeners named
`OceanBaseConnector.Protocol`. Subscribe to the selected listener with an
`IObserver<KeyValuePair<string, object?>>`.

```csharp
// In IObserver<DiagnosticListener>.OnNext:
if (listener.Name == "OceanBaseConnector.Protocol")
    subscriptions.Add(listener.Subscribe(frameObserver,
        (name, _, _) => name == "OceanBaseConnector.Protocol.Frame"));

// In frameObserver.OnNext(KeyValuePair<string, object?> item):
var fields = (IReadOnlyDictionary<string, object?>)item.Value!;
Console.WriteLine($"{fields["ChannelId"]} {fields["Layer"]} " +
    $"{fields["Direction"]} seq={fields["Sequence"]} length={fields["PayloadLength"]}");
```

Dispose both the AllListeners subscription and each per-listener subscription. A process can contain
multiple listener instances, for example when loading several provider assemblies in isolated load
contexts. Correlate using the listener instance together with ChannelId. ChannelId is a locally generated
Int64, shared by the MySQL channel and its compression/OB20 wrappers, not a server connection ID.

| Event | Fields beyond ChannelId and Layer | Meaning |
| --- | --- | --- |
| `OceanBaseConnector.Protocol.Frame` | Direction, Sequence, ExpectedSequence, PayloadLength, UncompressedLength, RequestId, Flags | A complete read header was obtained, or a whole write frame was accepted by the next stream |
| `OceanBaseConnector.Protocol.Error` | Direction, Failure | A transport/framing operation caught an error, or an unread packet was abandoned |
| `OceanBaseConnector.Protocol.Boundary` | Stage | A MySQL command boundary began or response boundary validation completed |
| `OceanBaseConnector.Protocol.Negotiated` | RequestedLayer, FullLinkTraceRequested, FullLinkTraceEnabled, ModernTraceFormat | Physical authentication, identity verification and configured client charset initialization completed with this framing selection |

Layer is `mysql`, `compressed` or `ob20`. Direction is `read`/`write`, with `boundary` for boundary errors.
Stage is `begin`/`complete`. Failure is `protocol`, `io`, `canceled`, `disposed`, `incomplete` or `error`.
The classification describes the exception observed at that framing layer, before command-level
cancellation/timeout and unknown-outcome mapping. One fault may be observed at several layers.
External Close/cancellation still permits the interrupted read to report its caught failure; read/outer
failure deduplication is independent of the transport's closed flag. An unread payload reports
`incomplete`; buffered continuation faults retain their actual exception classification.

Numeric frame fields describe the header, including malformed headers, before read validation/body
consumption. PayloadLength is Int64; Sequence/ExpectedSequence/UncompressedLength are Int32, RequestId
is Int64 (`-1` outside OB20), and Flags is UInt32 (`0` outside OB20). ChannelId is Int64;
the Frame/Error/Boundary fields otherwise use strings. A read Frame is not proof of valid checksum
or complete body. ExpectedSequence is an observation: compressed MySQL inner packets may legally be
renumbered. A write Frame is not proof of a flush, TCP delivery, server receipt or SQL success.
Compression and OB20 can buffer inner writes.

Subscribe to `OceanBaseConnector.Protocol.Negotiated` before opening when you need the actual protocol
selection. Its dictionary contains exactly ChannelId (Int64), Layer/RequestedLayer (strings: `mysql`,
`compressed` or `ob20`) and FullLinkTraceRequested/FullLinkTraceEnabled/ModernTraceFormat (Boolean).
RequestedLayer describes the connection options; Layer describes the channel wrappers actually enabled.
For example, a server that does not advertise the required OB20 capability can report RequestedLayer
`ob20` with Layer `mysql`. Requested compression still requires its advertised MySQL capability;
a rejected authentication, identity or charset initialization emits no Negotiated event.

FullLinkTraceEnabled means the authenticated capability and request enabled the local OB20 trace
context. ModernTraceFormat is true only when that active context uses modern extra-info encoding;
both remain false when trace is disabled. These fields do not report server sampling, trace records,
remote collector receipt or TLS/authentication plugin details. Trace IDs and extra-info bytes remain
private. A Negotiated event alone does not prove subsequent framed I/O or successful logical Open:
later physical-session/pool initialization can still fail. Correlate later Frame events and operation
outcomes when verifying a complete connection or exchange.

Negotiated is emitted once per successful physical authentication, including newly established pool
sessions. Borrowing/resetting an existing channel does not emit it again. Observers subscribing after
authentication receive no replay or cached snapshot; reopened physical channels have their own IDs.
It shares the same synchronous callback, isolation, reentry and listener-identity rules as Frame events.

Protocol events are process-wide and are not routed through a logical connection's ILogger factory.
With no enabled listener, no per-event dictionary is allocated; each channel has one small tracker.
No event queue is retained by the driver. Observers and IsEnabled predicates run synchronously on the
protocol thread: return promptly, avoid reentering driver operations, and control any application-side
storage or export. Observer exceptions are isolated, Activity.Current is restored, and recursive event
emission on the same thread is suppressed. One throwing observer can prevent other observers from
receiving that event, following DiagnosticListener behavior. If listener creation itself fails because
an AllListeners observer throws, protocol operations continue with this assembly's protocol diagnostics
disabled. No recovery promise is made for arbitrary observer side effects or blocked callbacks.

Reentry protection uses ThreadLocal<bool>, whose framework type argument avoids collectible provider
ThreadStatic slot reuse. An independent-process consumer regression verifies old-context collection and
that an old diagnostic thread exiting cannot corrupt a newly loaded context's static objects. First use
on a thread can allocate a ThreadLocal slot; disabled frames still allocate no per-event dictionary.
See the [unloading evidence and runtime limits](compatibility.md).

Local stream/loopback tests and five assets on one .NET10 host verify this diagnostic contract; they do
not establish real OceanBase/OBProxy/TLS interoperability, independent runtime compatibility, or a
production throughput/latency budget.

<a id="ob20-trace"></a>

## OB20 and full-link trace acceptance

The opt-in acceptance console profile inspects the actual protocol through the `Negotiated` / `Frame`
events and normal query results. It does not run by default; ordinary unit tests and CI use only a
synthetic server and do not depend on a real environment.

Set `ob20TraceProfile` in a standalone acceptance configuration and explicitly enable `Use OB20=true`
in the private connection configuration. Each profile has two cases, `.sync` / `.async`, named
`matrix.ob20-trace-negotiation.<profile>`; only the two matching ones execute and the rest are skipped.
Invalid profiles or conflicting options fail during parsing/connection-config parsing, before any
networking.

| Profile | Enable Full Link Trace | Expected actual frame layer | trace / modern | Additional assertion |
| --- | --- | --- | --- | --- |
| `ob20` | false | OB20 | false / false | Written frames must not contain trace extra-info |
| `trace-legacy` | true | OB20 | true / false | Query write frames carry legacy trace extra-info at least once |
| `trace-modern` | true | OB20 | true / true | Query write frames carry modern trace extra-info at least once |
| `declined-ob20` | true | MySQL | false / false | Verifies ordinary MySQL communication after the OB20/trace request is declined, with no OB20 frames |
| `declined-trace` | true | OB20 | false / false | OB20 enabled, trace declined; written frames must not contain trace extra-info |

All profiles forbid compression and retain the original TLS configuration without downgrade. If a
deployment lacks the selected protocol or trace policy, the enabled case fails and is not automatically
switched to another successful profile. A TLS handshake failure likewise remains a failure.

The configuration example references only the private connection source and contains no connection values:

```json
{
  "mode": "MySql",
  "connectionStringEnvironment": "OB_ACCEPTANCE_CONNECTION_STRING",
  "ob20TraceProfile": "trace-modern",
  "groups": ["faults"]
}
```

The private connection for this configuration must request both OB20 and Full Link Trace. `--list` does
not parse credentials or connect. See the [acceptance README](../tests/OceanBaseConnector.Acceptance/README.md)
for how to run it.

Assertions and boundaries:

- Copy the configuration and disable pooling to force a fresh physical handshake; do not reuse an old
  pooled session, create database objects, or change server settings.
- Accept diagnostics only for the current logical execution flow, matched by listener instance and
  ChannelId, requiring exactly one negotiation event whose six field types and values are all correct.
- Command-frame counting starts only after Open; complete three fixed `SELECT 1` queries (with `FROM DUAL`
  in Oracle), requiring the result to be 1 and bidirectional communication on the actual frame layer.
  Synchronous commands register caller cancellation; asynchronous commands pass the token.
- Active trace also requires extra-info to be actually sent and checks the modern flag on write frames.
  Across the three queries, the server policy from the first response may enable subsequent requests;
  if no sampling/control produces sent data, the case fails explicitly. Only the extra-info flag is
  observed; its contents are neither read nor stored.
- The capture keeps only fixed scalar and boolean summaries, never event dictionaries, trace IDs,
  payloads, SQL, endpoints, or credentials. Connections, commands, Activities, and subscriptions are
  disposed with the case, including on failure/cancellation.

Successfully consuming query results verifies the response body/checksum path; read Frame events alone do
not prove the body is valid, and written frames do not prove the server received them. Trace write frames
prove the local send path accepted the data, not that a collector received it, that the server keeps
sampling, that the full link is exported, that real OBProxy is compatible, or that performance budgets are
met. The TLS/auth matrix remains a separate placeholder; this module does not implement OB20
compression/reroute.

Current real-environment results: on the direct OceanBase 4.4.2.2 MySQL/Oracle tenants, Windows/.NET
10.0.14, TLS Disabled, the `declined-ob20` sync/async cases were run; both modes passed the connection
control and the two profile cases (6 passed, 0 failed each, other cases skipped per configuration). Both
modes' `ob20` profile failed as expected, with diagnostics showing OB20 was requested but the actual
`Layer=mysql`; therefore that environment provides no actual OB20 frames or trace sending policy. The
client keeps the explicit failure and does not disguise the server's rejection as OB20 success. This
result proves only the real path of the ordinary MySQL fallback and cannot prove OB20/trace, OBProxy, or
collector compatibility.

Implementation entry points: [case execution](../tests/OceanBaseConnector.Acceptance/DatabaseCases.Ob20Trace.cs),
[scalar capture](../tests/OceanBaseConnector.Acceptance/Ob20TraceCapture.cs).

<a id="session-io"></a>

## Session protocol I/O durations

The `OceanBaseConnector` meter exposes `oceanbase.client.protocol.io.duration`, a `double` histogram with
unit `s`. Enable it with a standard .NET `MeterListener` or your metrics collector. No connection option
is required.

Each sample is the sum of timed protocol calls in **one command exchange and one direction**, not a packet
count or an individual socket latency. An exchange emits at most one `read` and one `write` sample, only
for directions actually used. Preparation/control requests and queued statement closes within that
exchange are included. Transactions, XA, cursor fetches and LOB operations that use the same exchange
mechanism are included; this is not restricted to SQL execution.

| Label | Values |
| --- | --- |
| `oceanbase.protocol.direction` | `read`, `write` |
| `oceanbase.compatibility_mode` | `mysql`, `oracle` |
| `oceanbase.outcome` | `success`, `error`, `canceled`, `timeout` |

The outcome describes the final exchange result, so an otherwise successful write followed by a failed
read has `error` on both samples. Cancellation and timeout use the exchange's mapped exception. These
labels contain no SQL, parameters, endpoint, credentials or exception text. They do not distinguish
confirmed failure from an unknown execution outcome: the exception and existing operation diagnostics
remain authoritative. A metric never authorizes retrying a write.

Timing covers packet/payload read and write calls, including framing, buffering, copying,
compression/OB20 codec work and any synchronous protocol observers within those calls. It also includes
their network/server waits. It is neither socket-only time nor server-only SQL time. Buffered calls may
complete without socket I/O.

Application gaps between reader/stream calls, application-side row processing and upload-source reads are
excluded. Parameter and LOCAL INFILE source reads have their independent [source-read histogram](#source-read);
LOCAL INFILE packet writes are included here. Handshake/TLS/authentication and pool waits have their
separate [connection phase metrics](obconnector-dotnet-architecture-design.md#connection-phase-durations).
Do not add these durations together as an exact decomposition of command or Open elapsed time; scope and
lifetime differ.

An exchange starts accumulating only if this histogram is enabled when the exchange is created. Enabling a
listener later does not instrument that exchange retroactively. Protocol timing and source timing are
enabled independently. With neither histogram enabled no shared accumulator is created; disabled async
wrappers return the underlying tasks directly. Enabled async wrappers can allocate state machines; no
measured overhead guarantee is claimed.

Publication occurs after the exchange releases its physical lease, invokes its registered release callback
and completes any pending timed calls. Higher-level transaction/XA/LOB reservation guards can still be
active; callbacks do not have a general guarantee that they can start another operation on that connection.
Streaming exchanges publish when they complete, fail or are disposed, rather than after every row. An empty
exchange emits no sample. Concurrent pending completions share one publication. This histogram's own
listener execution time is excluded. Callbacks are synchronous and must return promptly; callback
exceptions are isolated and `Activity.Current` is restored. Existing operation spans and slow-operation
diagnostics retain their separate clocks.

Local regression entry points are `SessionIoDiagnosticsTests.cs` and `DiagnosticsConsumerTests.cs`. Local
fixture measurements establish neither real OceanBase/OBProxy interoperability nor workload, overhead or
cross-platform limits.

<a id="source-read"></a>

## Upload source read durations

The `OceanBaseConnector` meter exposes `oceanbase.client.source.read.duration`, a `double` histogram with
unit `s`. Enable it through a standard .NET `MeterListener` or collector. No connection option is required.

Each sample sums application stream `Read`/`ReadAsync` calls for one source kind in one command exchange.
It covers MySQL long-data parameters, Oracle piece-data parameters and LOCAL INFILE uploads. Multiple
streamed parameters and chunks share one `parameter` sample. EOF and throwing reads count; an exchange that
never reads a source emits nothing. There is at most one sample per used kind per exchange.

| Label | Values |
| --- | --- |
| `oceanbase.source.kind` | `parameter`, `local_infile` |
| `oceanbase.compatibility_mode` | `mysql`, `oracle` |
| `oceanbase.outcome` | `success`, `error`, `canceled`, `timeout` |

Timing includes the source's own work and waits inside its read call. It excludes stream-factory
acquisition, stream disposal, packet sends/acknowledgements, result consumption and gaps between source
calls. It is not pure scheduler, disk or socket wait time. [Protocol I/O](#session-io) retains its separate
clock, including packet framing/codec work and network/server waits.

The outcome is the final mapped exchange outcome. Successful source reads followed by a server error have
`error`; a source token cancellation caused by the command deadline has `timeout`. A sample does not
establish whether SQL was sent or a write committed. Use the operation exception's unknown-outcome
classification; metrics never authorize retries. Failed reads retain existing staged-data retirement rules.
Parameter streams remain caller-owned, while LOCAL INFILE streams remain driver-owned.

Labels contain no SQL, values, source names, filenames, parameter indexes, endpoint, credentials or
exception text. Publication shares the existing protocol accumulator: after physical lease release, the
registered release callback and all pending timed calls, including a read still completing during exchange
disposal. Concurrent stops publish once. Higher-level transaction/XA/LOB guards can still be active;
arbitrary connection reentry from listeners is not guaranteed.

Source and protocol instruments are independently enabled when an exchange starts. Enabling a listener
later does not instrument that exchange retroactively. If neither is enabled, no accumulator is allocated.
A disabled instrument starts no stopwatch; its asynchronous wrappers return the underlying tasks directly.
Enabled timing can allocate async state machines and uses the existing accumulator lock; no measured
overhead guarantee is claimed. Listener callbacks run synchronously, must return promptly, have their
exceptions isolated and restore `Activity.Current`. This histogram's listener time is outside the timed
source reads.

Local entry points: `SourceReadDiagnosticsTests.cs`, `SourceReadFixture.cs`,
`SourceReadConsumerTests.cs`, and the existing session-I/O/upload regressions. Tests use synthetic loopback
and require no real database or credentials. Five assets loaded in one .NET10 host do not prove five
independently tested runtimes, real OceanBase/OBProxy interoperability, sustained load or other resource
waits.

<a id="pool"></a>

## Connection pool metrics

The `OceanBaseConnector` meter publishes untagged pool lifecycle measurements across the pool instances in
that provider assembly. A process loading isolated provider assemblies can have several meters with that
name; retain meter identity when collecting them.

| Instrument suffix after `oceanbase.client.pool.` | Kind | Unit | Meaning |
| --- | --- | --- | --- |
| `sessions` | UpDownCounter | `{session}` | Registered physical sessions, including idle, borrowed and returning/resetting sessions |
| `leased` | UpDownCounter | `{session}` | Physical sessions handed out by pool Rent/TryRent and not yet returned or discarded |
| `borrowed` | Counter | `{borrow}` | Cumulative successful pool borrows; reuse counts again |
| `waiters` | UpDownCounter | `{waiter}` | Pending pool availability waits |
| `discarded` | Counter | `{session}` | Physical sessions discarded by the pool |
| `reset.failures` | Counter | `{failure}` | Failed pool return resets |
| `wait.duration` | Histogram | `s` | Elapsed time since the Rent began, recorded when each availability wait ends |

`leased` adds one when a pool successfully hands out a newly created or reused physical session. A newly
handed-out session may still be authenticating during logical Open; this does not count only fully open
application connections. Prewarming idle sessions, candidate validation/probing, failed probe candidates
and queued waits do not add leases. Subsequent failed or canceled Open discards its session and removes the
lease.

A lease ends at entry to Return/ReturnAsync, **before** reset. Resetting sessions still consume pool
capacity and remain in `sessions`, but are no longer `leased`. Discard ends an outstanding lease once;
repeated or foreign-pool discard cannot decrement it twice. Pool clearing, credential retirement and
source/pool disposal leave outstanding leases counted until their owners return or discard them. These
operations do not immediately terminate borrowed work. Broken sessions remain leased until the logical
owner closes/cleans them up.

UpDownCounter values are deltas, not observable absolute gauges. Their sum represents the current count
only when collection includes the corresponding lifetime's increments and decrements. A listener enabled
late can see a negative delta without the earlier positive delta. Do not use cumulative `borrowed` as a
current lease count, or derive available capacity by subtracting `leased` from Max Pool Size: reset,
prewarm, probes and opening reservations also consume capacity.

Measurements contain no pool key, endpoint, credentials, SQL or exception text. Callbacks run
synchronously outside the pool state lock, using existing exception isolation and `Activity.Current`
restoration. They must return promptly; this is not a promise of arbitrary connection reentry or
workload/overhead limits. Concurrent lifecycle transitions can interleave measurements, so samples are not
an atomic snapshot of every counter. No new connection option or dependency is required.

For a wait's own duration use `pool.wait` in the separate
[connection phase histogram](obconnector-dotnet-architecture-design.md#connection-phase-durations). It has
different scope from the legacy `wait.duration` aggregate.

Local regression entry points: `PoolLeaseMetricsTests.cs`, `Session/PhysicalSessionPoolTests.cs` and
`PoolLeaseMetricsConsumerTests.cs`. These fixtures do not establish real OceanBase/OBProxy reset or
sustained resource limits, independent runtimes, or cross-platform acceptance.
