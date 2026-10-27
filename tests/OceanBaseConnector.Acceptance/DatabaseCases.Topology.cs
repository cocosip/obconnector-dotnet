using System;
using System.Collections.Generic;
using System.Data;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace OceanBaseConnector.Acceptance;

internal static partial class DatabaseCases
{
    private static void AddTopology(List<AcceptanceCase> cases)
    {
        foreach (var asynchronous in new[] { false, true })
            foreach (var scenario in new[] { "idle-refresh", "borrowed-isolation", "tcp-fallback" })
                cases.Add(new("matrix.topology-failover." + scenario + (asynchronous ? ".async" : ".sync"), "faults",
                    (c, t) => Topology(c, asynchronous, scenario, t), RequiresTopologyFaultProfile: true));
    }

    private static async Task Topology(CaseContext c, bool asynchronous, string scenario, CancellationToken token)
    {
        TopologyRelay? a = null; TopologyRelay? b = null; OceanBaseDataSource? source = null;
        OceanBaseConnection? first = null; OceanBaseConnection? second = null;
        using var refused = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { ExclusiveAddressUse = true };
        Exception? failure = null;
        try
        {
            using var configuration = c.CreateConnection();
            var builder = new OceanBaseConnectionStringBuilder(configuration.ConnectionString);
            a = new TopologyRelay(builder.Server, builder.Port); b = new TopologyRelay(builder.Server, builder.Port);
            refused.Bind(new IPEndPoint(IPAddress.Loopback, 0)); // Reserve without listening: deterministic refusal, no rebind race.
            var dead = "127.0.0.1:" + ((IPEndPoint)refused.LocalEndPoint!).Port;
            var resolver = new ControlledRoutes(scenario == "tcp-fallback" ? dead : "127.0.0.1:" + a.Port, asynchronous);
            builder.Server = "127.0.0.1"; builder.Port = b.Port;
            builder.Pooling = true; builder.MinPoolSize = 0; builder.MaxPoolSize = scenario == "borrowed-isolation" ? 2 : 1;
            builder.ConnectionLifetime = 0; builder.ConnectionIdleLifetime = 0;
            builder.LoadBalance = OceanBaseLoadBalance.Sequential; builder.ConnectRetryCount = 0; builder.HostRecheckSeconds = 0;
            source = new OceanBaseDataSourceBuilder(builder.ConnectionString).UseAddressResolver(resolver).Build();
            first = (OceanBaseConnection)source.CreateConnection(); second = (OceanBaseConnection)source.CreateConnection();
            if (scenario == "tcp-fallback")
            {
                var rejected = false;
                try { await Open(first); }
                catch (OceanBaseException error) when (!token.IsCancellationRequested && !error.IsExecutionOutcomeUnknown) { rejected = true; }
                c.Check(rejected && first.State == ConnectionState.Closed, "Refused-only discovery must fail Open with a known outcome and leave Closed.");
                c.Check(a.Connections == 0 && b.Connections == 0, "Failed discovery must not fall back to configured healthy routes.");
                resolver.Set(dead + ",127.0.0.1:" + b.Port);
                await Open(first); await Query(first); await Close(first);
                await Open(first); await Query(first);
                c.Check(resolver.Calls == 3 && b.Connections == 1 && a.Connections == 0,
                    "TCP fallback and pooled reuse must each use one discovery snapshot and one healthy physical channel.");
            }
            else
            {
                await Open(first); await Query(first);
                if (scenario == "idle-refresh")
                {
                    await Close(first); await Open(first); await Query(first);
                    c.Check(a.Connections == 1, "Unchanged discovery must reuse its initial physical channel.");
                    await Close(first);
                }
                resolver.Set("127.0.0.1:" + b.Port);
                if (scenario == "borrowed-isolation")
                {
                    await Open(second); await Query(second); await Query(first);
                    c.Check(!a.FirstClosed.IsCompleted && a.Connections == 1 && b.Connections == 1,
                        "Removed borrowed route must remain usable while a new borrower uses the new route.");
                    await Close(first); await Query(second);
                }
                await Open(first); await Query(first);
                await a.FirstClosed.WaitAsync(TimeSpan.FromSeconds(5), token);
                await Close(first); await Open(first); await Query(first);
                if (scenario == "borrowed-isolation") await Query(second);
                c.Check(resolver.Calls == 4 && a.Connections == 1 && b.Connections == (scenario == "borrowed-isolation" ? 2 : 1),
                    "Removed idle route must retire; replacement and subsequent reuse must use exactly one snapshot per Open.");
            }
            source.Clear(); await Close(first); await Close(second);
            await b.FirstClosed.WaitAsync(TimeSpan.FromSeconds(5), token);

            async Task Open(OceanBaseConnection connection)
            { if (asynchronous) await connection.OpenAsync(token); else connection.Open(); }
            async Task Close(OceanBaseConnection connection)
            { if (asynchronous) await connection.CloseAsync(); else connection.Close(); }
            async Task Query(OceanBaseConnection connection)
            {
                using var command = c.Command(connection, c.SelectOne);
                c.EqualNumber(asynchronous ? await command.ExecuteScalarAsync(token) : command.ExecuteScalar(), 1,
                    "The selected physical route must execute the read-only control query.");
                c.ObserveServer(connection);
            }
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            var errors = new List<Exception>();
            foreach (var cleanup in new Func<ValueTask>[] { () => second?.DisposeAsync() ?? default, () => first?.DisposeAsync() ?? default,
                () => source?.DisposeAsync() ?? default, () => b?.DisposeAsync() ?? default, () => a?.DisposeAsync() ?? default })
            { try { await cleanup(); } catch (Exception error) { errors.Add(error); } }
            if (errors.Count != 0) { if (failure != null) errors.Insert(0, failure); throw new AggregateException(errors); }
        }
    }

    private sealed class ControlledRoutes(string addresses, bool asynchronous) : IOceanBaseAddressResolver
    {
        private string _addresses = addresses;
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);
        internal void Set(string value) => Volatile.Write(ref _addresses, value);
        private string Snapshot(CancellationToken token)
        { token.ThrowIfCancellationRequested(); Interlocked.Increment(ref _calls); return Volatile.Read(ref _addresses); }
        public string Resolve(CancellationToken token) => asynchronous
            ? throw new AssertionFailure("Asynchronous Open must use ResolveAsync.") : Snapshot(token);
        public Task<string> ResolveAsync(CancellationToken token) => asynchronous
            ? Task.FromResult(Snapshot(token)) : throw new AssertionFailure("Synchronous Open must use Resolve.");
    }
}
