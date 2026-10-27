# Controlled network outcome acceptance

The [acceptance console](../tests/OceanBaseConnector.Acceptance/README.md) has 16
`matrix.network-outcome-unknown.*` cases. They require both `networkFaultProfile=true`
and `allowCreateObjects=true` in a dedicated test schema. Default/example configurations
skip them before connecting or creating objects. Ordinary unit tests use synthetic loopback.

The test-only relay accepts a single plain DNS/IPv4 host with a separate Port, TLS disabled,
and neither compression nor OB20. Incompatible profiles skip; transport options are never
downgraded. This relay is an acceptance fixture, not a driver feature or a production proxy.

| Operation, each sync/async | Fault | Independently observed committed row count |
| --- | --- | --- |
| Autocommit INSERT | Withhold request before upstream forwarding | 0 |
| Autocommit INSERT | Forward original header plus command byte, then close | 0 |
| Autocommit INSERT | Withhold whole OK response, or forward original header plus marker byte | 1 |
| COMMIT | Withhold whole OK response, or forward original header plus marker byte | 1 |
| ROLLBACK | Withhold whole OK response, or forward original header plus marker byte | 0 |

All faults happen after the provider has sent its request to the relay. Even when the relay
withholds the request from the server, the driver conservatively reports unknown outcome.
This does not prove provider-local pre-send classification. Response cases first receive
a complete server response and require its OK marker; they do not interrupt server execution.

Every case creates one random owned table, verifies unknown outcome and Broken state, and
requires an interrupted transaction to detach. An independent observer reconciles the actual
row count. Close/Open through a private capacity-one pool must create a second relay TCP
connection; a fresh autocommit write and transaction rollback verify healthy recovery.
Exact target-request counts reject replay, including one additional explicit fresh ROLLBACK
in rollback cases. The driver does not retry the interrupted write. See the
[application reconciliation guide](unknown-outcome-recovery.md) for business recovery.

All connections, transactions, pools and relay tasks are disposed on failure. Registered
cleanup drops only the case's exact owned table. Primary and cleanup failures are retained;
reports omit connection details, SQL, values and raw exceptions. Forced process termination
can leave an owned object requiring manual cleanup.

On direct OceanBase 4.4.2.2, Windows/.NET 10.0.13, TLS disabled, both modes passed all 16
cases plus one connection control; all 32 tables were removed. This is bounded direct-server
evidence. Provider-local pre-send, in-flight server execution, timeout/cancellation ordering,
ambient in-doubt/coordinator crashes, real OBProxy, TLS, other versions/platforms and long-running
load remain open under P0-04/W2 and P2-06/W8. A separate [XA coordinator process matrix](xa-process-recovery-acceptance.md)
now covers one prepared/recorded-decision crash point. [Controlled topology cases](topology-acceptance.md)
cover client-route changes and refused TCP; two deferred matrix placeholders remain. It does not close the 23-gap/eight-package baseline.
