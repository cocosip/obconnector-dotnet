# Controlled topology acceptance

The [acceptance console](../tests/OceanBaseConnector.Acceptance/README.md) has six explicit
`matrix.topology-failover.idle-refresh/borrowed-isolation/tcp-fallback.sync/async` cases.
Set `topologyFaultProfile=true` to enable them. No object-creation permission is needed.
Default/example configurations skip before listeners or connections. Ordinary unit/CI tests
use synthetic loopback only. The profile requires a single plain DNS/IPv4 upstream host,
separate Port, TLS disabled, no compression and no OB20; incompatible settings skip without
downgrade. This profile does not validate transport security.

Two transparent loopback relays point to the same configured server. They count physical
connections and signal peer termination without parsing or recording traffic. A private
pool uses a controlled public `IOceanBaseAddressResolver`; every logical Open must use one
snapshot through the matching sync/async resolver method. Each path executes only fixed
`SELECT 1` or `SELECT 1 FROM DUAL` and makes these assertions:

- Idle refresh: unchanged routing first reuses A, removing A replaces its idle connection
  with B and closes A, and another borrower reuses B without a new physical connection.
- Borrowed isolation: a leased A remains usable while a new borrower uses B. After A returns,
  the next borrow retires A and opens another B; both B leases remain usable and later reuse
  does not reconnect. Changing discovery does not migrate active work.
- TCP fallback: a bound non-listening exclusive socket reserves a refused endpoint. A
  refused-only snapshot must fail Open with a known outcome, leave Closed and never contact
  the configured healthy fallback. The same connection/private pool then opens with a
  refused-plus-healthy snapshot and reuses its single healthy physical connection afterward.

No DDL, writes, transactions or cluster settings are changed. Credentials stay in connection
memory/private pipes and are never sent to the refused endpoint. Reports contain fixed
assertions and server/runtime identity, omitting endpoints, connection strings, SQL and raw
exception text. Connections, private pools, listeners, forwarding tasks and reserved sockets
are disposed on success, failure or cancellation; primary and cleanup failures are preserved.
Stopping a relay is part of fixture cleanup, not proof that a real database node failed.

Direct OceanBase4.4.2.2, Windows/.NET10.0.13, TLS disabled: Oracle and MySQL each pass7/7
(connection control plus six cases), zero failures/skips/cleanup failures, no objects created.

See the [module record](history/obconnector-dotnet-progress.md#2026-10-27-controlled-topology-acceptance)
for current build, unit, real-server and independent review gates. All tests use the existing
product APIs; this module adds no product source, public options or dependencies.

This covers controlled client routes to one upstream. Actual independent cluster nodes,
discovery services/TNS/OCP refresh, proxy backend identity/reroute, DNS IP changes, authentication
or TLS failures, active-session failover, transaction reconciliation/replay, other versions,
platforms/runtimes and sustained workload remain open under P0-06/P1-09/P2-06 and W1/W4/W8.
Two unconditional deferred placeholders remain: TLS/authentication and OB20/trace negotiation.
The23-gap/eight-package baseline is not closed by these cases.
