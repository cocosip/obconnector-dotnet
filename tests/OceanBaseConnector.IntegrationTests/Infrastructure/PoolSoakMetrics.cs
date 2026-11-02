using System;
using System.Diagnostics.Metrics;

namespace OceanBaseConnector.IntegrationTests.Infrastructure;

internal sealed class PoolSoakMetrics : IDisposable
{
    private readonly object _gate = new();
    private readonly MeterListener _listener = new();
    private long _sessions, _leases, _waiters, _borrows;

    internal PoolSoakMetrics()
    {
        _listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter.Name == "OceanBaseConnector" && instrument.Name is
                "oceanbase.client.pool.sessions" or "oceanbase.client.pool.leased" or
                "oceanbase.client.pool.waiters" or "oceanbase.client.pool.borrowed")
                owner.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            lock (_gate)
            {
                switch (instrument.Name)
                {
                    case "oceanbase.client.pool.sessions": _sessions += value; break;
                    case "oceanbase.client.pool.leased": _leases += value; break;
                    case "oceanbase.client.pool.waiters": _waiters += value; break;
                    case "oceanbase.client.pool.borrowed": _borrows += value; break;
                }
            }
        });
        _listener.Start();
    }

    internal (long Sessions, long Leases, long Waiters, long Borrows) Snapshot()
    {
        lock (_gate) return (_sessions, _leases, _waiters, _borrows);
    }

    public void Dispose() => _listener.Dispose();
}
