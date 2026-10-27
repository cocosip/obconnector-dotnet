# Protocol diagnostics

Subscribe to the standard .NET `DiagnosticListener.AllListeners` stream and select listeners named
`OceanBaseConnector.Protocol`. Subscribe to the selected listener with an
`IObserver<KeyValuePair<string, object?>>`. No connection option or additional package is required.
The event value implements `IReadOnlyDictionary<string, object?>`; its dictionary is read-only.

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
or complete body. ExpectedSequence is an
observation: compressed MySQL inner packets may legally be renumbered. A write Frame is not proof of a
flush, TCP delivery, server receipt or SQL success. Compression and OB20 can buffer inner writes.

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

The driver emits only these fixed scalar fields. It never places SQL, command bytes, parameters, rows,
authentication data, endpoint/username, protocol extra-info bytes, exception objects/messages or byte
buffers in these events. Existing operation diagnostics provide outcome and trace information separately.
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
See the [unloading evidence and runtime limits](obconnector-dotnet-completeness.md#1559-2026-10-26-可卸载诊断线程状态损坏与默认回归恢复).

Local stream/loopback tests and five assets on one .NET10 host verify this diagnostic contract; they do
not establish real OceanBase/OBProxy/TLS interoperability, independent runtime compatibility, or a
production throughput/latency budget. Other resource-wait diagnostics and deployment performance
acceptance remain open under P2-04/W7.
