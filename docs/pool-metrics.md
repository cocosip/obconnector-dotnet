# Connection pool metrics

The `OceanBaseConnector` meter publishes untagged pool lifecycle measurements across
the pool instances in that provider assembly. A process loading isolated provider
assemblies can have several meters with that name; retain meter identity when
collecting them.

| Instrument suffix after `oceanbase.client.pool.` | Kind | Unit | Meaning |
| --- | --- | --- | --- |
| `sessions` | UpDownCounter | `{session}` | Registered physical sessions, including idle, borrowed and returning/resetting sessions |
| `leased` | UpDownCounter | `{session}` | Physical sessions handed out by pool Rent/TryRent and not yet returned or discarded |
| `borrowed` | Counter | `{borrow}` | Cumulative successful pool borrows; reuse counts again |
| `waiters` | UpDownCounter | `{waiter}` | Pending pool availability waits |
| `discarded` | Counter | `{session}` | Physical sessions discarded by the pool |
| `reset.failures` | Counter | `{failure}` | Failed pool return resets |
| `wait.duration` | Histogram | `s` | Elapsed time since the Rent began, recorded when each availability wait ends |

`leased` adds one when a pool successfully hands out a newly created or reused
physical session. A newly handed-out session may still be authenticating during
logical Open; this does not count only fully open application connections.
Prewarming idle sessions, candidate validation/probing, failed probe candidates and
queued waits do not add leases. Subsequent failed or canceled Open discards its
session and removes the lease.

A lease ends at entry to Return/ReturnAsync, **before** reset. Resetting sessions
still consume pool capacity and remain in `sessions`, but are no longer `leased`.
Discard ends an outstanding lease once; repeated or foreign-pool discard cannot
decrement it twice. Pool clearing, credential retirement and source/pool disposal
leave outstanding leases counted until their owners return or discard them. These
operations do not immediately terminate borrowed work. Broken sessions remain
leased until the logical owner closes/cleans them up.

UpDownCounter values are deltas, not observable absolute gauges. Their sum represents
the current count only when collection includes the corresponding lifetime's
increments and decrements. A listener enabled late can see a negative delta without
the earlier positive delta. Do not use cumulative `borrowed` as a current lease
count, or derive available capacity by subtracting `leased` from Max Pool Size:
reset, prewarm, probes and opening reservations also consume capacity.

Measurements contain no pool key, endpoint, credentials, SQL or exception text.
Callbacks run synchronously outside the pool state lock, using existing exception
isolation and `Activity.Current` restoration. They must return promptly; this is not
a promise of arbitrary connection reentry or workload/overhead limits. Concurrent
lifecycle transitions can interleave measurements, so samples are not an atomic
snapshot of every counter. No new connection option or dependency is required.

For a wait's own duration use `pool.wait` in the separate
[connection phase histogram](obconnector-dotnet-architecture-design.md#connection-phase-diagnostics).
It has different scope from the legacy `wait.duration` aggregate.

Local regression entry points: `PoolLeaseMetricsTests.cs`,
`Session/PhysicalSessionPoolTests.cs` and `PoolLeaseMetricsConsumerTests.cs`.
These fixtures do not establish real OceanBase/OBProxy reset or sustained resource
limits, independent runtimes, or cross-platform acceptance.
