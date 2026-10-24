using System;
using System.Collections.Concurrent;
using System.Data;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Session;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed class PoolRoutingTests
    {
        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task DifferentResolversCannotBorrowEachOthersSessions(bool asynchronous, bool reassign)
        {
            await using var first = new RoutingServer();
            await using var second = new RoutingServer();
            var configuration = Configuration(first.Port);
            var firstResolver = new Resolver(first.Port);
            var secondResolver = new Resolver(second.Port);
            using var a = new OceanBaseConnection(configuration) { AddressResolver = firstResolver };
            using var b = new OceanBaseConnection(configuration) { AddressResolver = secondResolver };
            PhysicalSession? firstSession = null;
            PhysicalSession? secondSession = null;
            try
            {
                await Open(a, asynchronous);
                firstSession = a.GetOpenSession();
                await Close(a, asynchronous);
                var next = reassign ? a : b;
                if (reassign) a.AddressResolver = secondResolver;
                await Open(next, asynchronous);
                secondSession = next.GetOpenSession();
                Assert.NotSame(firstSession, secondSession);
                Assert.Equal("127.0.0.1:" + second.Port, secondSession.Endpoint);
                Assert.Equal(1, secondResolver.Calls);
                await Close(next, asynchronous);
            }
            finally
            {
                a.Close(); b.Close();
                ClearPool(configuration, firstResolver); ClearPool(configuration, secondResolver);
            }
        }

        [Theory]
        [InlineData(false, false, 0)]
        [InlineData(true, false, 0)]
        [InlineData(false, true, 0)]
        [InlineData(true, true, 0)]
        [InlineData(false, false, 2)]
        [InlineData(true, false, 2)]
        [InlineData(false, true, 2)]
        [InlineData(true, true, 2)]
        public async Task EachBorrowUsesOneFreshSnapshotAndRetiresRemovedEndpoints(bool asynchronous, bool dataSource, int minimum)
        {
            await using var first = new RoutingServer();
            await using var second = new RoutingServer();
            var configuration = Configuration(first.Port, minimum);
            var resolver = new Resolver(first.Port);
            using var source = dataSource ? new OceanBaseDataSourceBuilder(configuration).UseAddressResolver(resolver).Build() : null;
            using var connection = dataSource ? (OceanBaseConnection)source!.CreateConnection() : new OceanBaseConnection(configuration) { AddressResolver = resolver };
            PhysicalSession? original = null;
            PhysicalSession? replacement = null;
            try
            {
                await Open(connection, asynchronous);
                original = connection.GetOpenSession();
                Assert.Equal(1, resolver.Calls);
                await Close(connection, asynchronous);
                await Open(connection, asynchronous);
                Assert.Same(original, connection.GetOpenSession());
                Assert.Equal(2, resolver.Calls);
                await Close(connection, asynchronous);
                resolver.Port = second.Port;
                await Open(connection, asynchronous);
                replacement = connection.GetOpenSession();
                Assert.NotSame(original, replacement);
                Assert.Equal(SessionState.Closed, original.State);
                Assert.Equal("127.0.0.1:" + second.Port, replacement.Endpoint);
                Assert.Equal(3, resolver.Calls);
                Assert.Equal(Math.Max(1, minimum), second.Accepted);
                await Close(connection, asynchronous);
            }
            finally { connection.Close(); if (!dataSource) ClearPool(configuration, resolver); }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task FailedDiscoveryCannotFallBackToHealthyIdleSession(bool asynchronous)
        {
            await using var server = new RoutingServer();
            var resolver = new Resolver(server.Port);
            using var source = new OceanBaseDataSourceBuilder(Configuration(server.Port)).UseAddressResolver(resolver).Build();
            using var connection = (OceanBaseConnection)source.CreateConnection();
            await Open(connection, asynchronous);
            var session = connection.GetOpenSession();
            await Close(connection, asynchronous);
            resolver.Fail = true;
            await Assert.ThrowsAsync<OceanBaseException>(() => Open(connection, asynchronous));
            Assert.Equal(ConnectionState.Closed, connection.State);
            resolver.Fail = false;
            await Open(connection, asynchronous);
            Assert.Same(session, connection.GetOpenSession());
            await Close(connection, asynchronous);
        }

        [Theory]
        [InlineData(false, "close")]
        [InlineData(true, "close")]
        [InlineData(false, "timeout")]
        [InlineData(true, "timeout")]
        [InlineData(true, "cancel")]
        public async Task IdleSessionsCannotBypassDiscoveryCancellationOrBudget(bool asynchronous, string stopping)
        {
            await using var server = new RoutingServer();
            var resolver = new Resolver(server.Port);
            var configuration = new OceanBaseConnectionStringBuilder(Configuration(server.Port)) { ConnectionTimeout = 1 }.ConnectionString;
            using var source = new OceanBaseDataSourceBuilder(configuration).UseAddressResolver(resolver).Build();
            using var connection = (OceanBaseConnection)source.CreateConnection();
            using var caller = new CancellationTokenSource();
            await Open(connection, asynchronous);
            var session = connection.GetOpenSession();
            await Close(connection, asynchronous);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var late = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            resolver.Sync = token =>
            {
                entered.TrySetResult(); token.WaitHandle.WaitOne(); token.ThrowIfCancellationRequested();
                throw new InvalidOperationException("unreachable");
            };
            resolver.Async = _ => { entered.TrySetResult(); return late.Task; };
            var opening = asynchronous ? connection.OpenAsync(caller.Token) : Task.Run(connection.Open);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (stopping == "close") await Close(connection, asynchronous);
                if (stopping == "cancel") caller.Cancel();
                var error = await Record.ExceptionAsync(() => opening.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.True(opening.IsCompleted);
                if (stopping == "timeout") Assert.IsType<TimeoutException>(error);
                else Assert.IsAssignableFrom<OperationCanceledException>(error);
                if (stopping == "cancel") Assert.Equal(caller.Token, ((OperationCanceledException)error!).CancellationToken);
                Assert.Equal(ConnectionState.Closed, connection.State);
                late.TrySetResult("127.0.0.1:1");
                resolver.Sync = null; resolver.Async = null;
                await Open(connection, asynchronous);
                Assert.Same(session, connection.GetOpenSession());
                await Close(connection, asynchronous);
            }
            finally { late.TrySetResult("127.0.0.1:1"); connection.Close(); await opening.ContinueWith(_ => { }); }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ClosedCallbackChangingResolverRetainsOriginalPoolCleanupIdentity(bool asynchronous)
        {
            await using var first = new RoutingServer();
            await using var second = new RoutingServer();
            var configuration = Configuration(first.Port);
            var firstResolver = new Resolver(first.Port);
            var nextResolver = new Resolver(second.Port);
            using var connection = new OceanBaseConnection(configuration) { AddressResolver = firstResolver };
            PhysicalSessionPool? oldPool = null;
            var reopen = true;
            connection.StateChange += (_, args) =>
            {
                if (args.CurrentState != ConnectionState.Closed || !reopen) return;
                reopen = false;
                connection.AddressResolver = nextResolver;
                connection.Open();
            };
            try
            {
                await Open(connection, asynchronous);
                oldPool = PhysicalSessionPoolRegistry.Get(configuration, firstResolver);
                connection.GetOpenSession().MarkDiscardOnReturn();
                await Close(connection, asynchronous);
                Assert.Equal("127.0.0.1:" + second.Port, connection.GetOpenSession().Endpoint);
                Assert.NotSame(oldPool, PhysicalSessionPoolRegistry.Get(configuration, firstResolver));
                await Close(connection, asynchronous);
            }
            finally
            {
                reopen = false; connection.Close(); oldPool?.Dispose();
                ClearPool(configuration, firstResolver); ClearPool(configuration, nextResolver);
            }
        }

        private static void ClearPool(string configuration, Resolver resolver)
        {
            var pool = PhysicalSessionPoolRegistry.Get(configuration, resolver);
            pool.Dispose();
            PhysicalSessionPoolRegistry.ReleaseIfEmpty(configuration, pool);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task FailedConcurrentDiscoveryCannotDisposePoolStillResolvingAnotherBorrow(bool asynchronous)
        {
            await using var server = new RoutingServer();
            var resolver = new Resolver(server.Port);
            var configuration = Configuration(server.Port);
            using var a = new OceanBaseConnection(configuration) { AddressResolver = resolver };
            using var b = new OceanBaseConnection(configuration) { AddressResolver = resolver };
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            Task<string> Resolve()
            {
                if (Interlocked.Increment(ref calls) != 1) return Task.FromException<string>(new InvalidOperationException("discovery failed"));
                entered.TrySetResult();
                return result.Task;
            }
            resolver.Sync = _ => Resolve().GetAwaiter().GetResult();
            resolver.Async = _ => Resolve();
            var opening = Open(a, asynchronous);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Assert.ThrowsAsync<OceanBaseException>(() => Open(b, asynchronous));
                result.TrySetResult("127.0.0.1:" + server.Port);
                await opening.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(ConnectionState.Open, a.State);
                await Close(a, asynchronous);
            }
            finally
            {
                result.TrySetResult("127.0.0.1:" + server.Port);
                await opening.ContinueWith(_ => { });
                a.Close(); b.Close(); ClearPool(configuration, resolver);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task RemovedBorrowedEndpointFinishesNormallyButCannotBeBorrowedAgain(bool asynchronous)
        {
            await using var first = new RoutingServer();
            await using var second = new RoutingServer();
            var resolver = new Resolver(first.Port);
            var configuration = new OceanBaseConnectionStringBuilder(Configuration(first.Port)) { MaxPoolSize = 2 }.ConnectionString;
            using var source = new OceanBaseDataSourceBuilder(configuration).UseAddressResolver(resolver).Build();
            using var a = (OceanBaseConnection)source.CreateConnection();
            using var b = (OceanBaseConnection)source.CreateConnection();
            using var c = (OceanBaseConnection)source.CreateConnection();
            await Open(a, asynchronous);
            var original = a.GetOpenSession();
            resolver.Port = second.Port;
            await Open(b, asynchronous);
            Assert.Equal(SessionState.Open, original.State);
            Assert.Equal("127.0.0.1:" + second.Port, b.GetOpenSession().Endpoint);
            await Close(a, asynchronous);
            await Open(c, asynchronous);
            Assert.Equal(SessionState.Closed, original.State);
            Assert.Equal("127.0.0.1:" + second.Port, c.GetOpenSession().Endpoint);
            await Close(b, asynchronous); await Close(c, asynchronous);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task DiscoveryConnectionBudgetAlsoBoundsLongerPoolWait(bool asynchronous)
        {
            await using var server = new RoutingServer();
            var resolver = new Resolver(server.Port);
            var configuration = new OceanBaseConnectionStringBuilder(Configuration(server.Port)) { ConnectionTimeout = 1, PoolWaitTimeout = 30 }.ConnectionString;
            using var source = new OceanBaseDataSourceBuilder(configuration).UseAddressResolver(resolver).Build();
            using var a = (OceanBaseConnection)source.CreateConnection();
            using var b = (OceanBaseConnection)source.CreateConnection();
            await Open(a, asynchronous);
            var session = a.GetOpenSession();
            var opening = Open(b, asynchronous);
            try
            {
                var error = await Record.ExceptionAsync(() => opening.WaitAsync(TimeSpan.FromSeconds(7)));
                Assert.True(opening.IsCompleted);
                Assert.IsType<TimeoutException>(error);
                Assert.Equal(ConnectionState.Closed, b.State);
                Assert.Same(session, a.GetOpenSession());
                await Close(a, asynchronous);
                await Open(b, asynchronous);
                Assert.Same(session, b.GetOpenSession());
                await Close(b, asynchronous);
            }
            finally { b.Close(); a.Close(); await opening.ContinueWith(_ => { }); }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task RemovedRegistryPoolCannotPoisonAnotherClosedConnectionsCache(bool asynchronous)
        {
            await using var server = new RoutingServer();
            var resolver = new Resolver(server.Port);
            var configuration = Configuration(server.Port);
            using var a = new OceanBaseConnection(configuration) { AddressResolver = resolver };
            using var b = new OceanBaseConnection(configuration) { AddressResolver = resolver };
            try
            {
                await Open(a, asynchronous); await Close(a, asynchronous);
                await Open(b, asynchronous);
                var old = b.GetOpenSession();
                old.MarkDiscardOnReturn();
                await Close(b, asynchronous);
                await Open(a, asynchronous);
                Assert.NotSame(old, a.GetOpenSession());
                Assert.Equal(ConnectionState.Open, a.State);
                await Close(a, asynchronous);
            }
            finally { a.Close(); b.Close(); ClearPool(configuration, resolver); }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ResolverDoesNotChangeUnsupportedAmbientIsolationException(bool asynchronous)
        {
            await using var server = new RoutingServer();
            var resolver = new Resolver(server.Port);
            var configuration = Configuration(server.Port);
            using var connection = new OceanBaseConnection(configuration) { AddressResolver = resolver };
            using var scope = new System.Transactions.TransactionScope(System.Transactions.TransactionScopeOption.RequiresNew,
                new System.Transactions.TransactionOptions { IsolationLevel = System.Transactions.IsolationLevel.RepeatableRead },
                System.Transactions.TransactionScopeAsyncFlowOption.Enabled);
            try
            {
                await Assert.ThrowsAsync<NotSupportedException>(() => Open(connection, asynchronous));
                Assert.Equal(ConnectionState.Closed, connection.State);
            }
            finally { connection.Close(); ClearPool(configuration, resolver); }
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task SameResolverSharesOnlyTheOrdinaryRegistryPool(bool asynchronous, bool dataSource)
        {
            await using var server = new RoutingServer();
            var resolver = new Resolver(server.Port);
            var configuration = Configuration(server.Port);
            using var sourceA = dataSource ? new OceanBaseDataSourceBuilder(configuration).UseAddressResolver(resolver).Build() : null;
            using var sourceB = dataSource ? new OceanBaseDataSourceBuilder(configuration).UseAddressResolver(resolver).Build() : null;
            using var a = dataSource ? (OceanBaseConnection)sourceA!.CreateConnection() : new OceanBaseConnection(configuration) { AddressResolver = resolver };
            using var b = dataSource ? (OceanBaseConnection)sourceB!.CreateConnection() : new OceanBaseConnection(configuration) { AddressResolver = resolver };
            try
            {
                await Open(a, asynchronous);
                var session = a.GetOpenSession();
                await Close(a, asynchronous);
                await Open(b, asynchronous);
                if (dataSource) Assert.NotSame(session, b.GetOpenSession());
                else Assert.Same(session, b.GetOpenSession());
                Assert.Equal(2, resolver.Calls);
                await Close(b, asynchronous);
            }
            finally { a.Close(); b.Close(); if (!dataSource) ClearPool(configuration, resolver); }
        }

        private static Task Open(OceanBaseConnection connection, bool asynchronous) => asynchronous ? connection.OpenAsync() : Task.Run(connection.Open);
        private static Task Close(OceanBaseConnection connection, bool asynchronous) => asynchronous ? connection.CloseAsync() : Task.Run(connection.Close);
        private static string Configuration(int port, int minimum = 0) => new OceanBaseConnectionStringBuilder
        {
            Server = "127.0.0.1",
            Port = port,
            UserId = "u",
            Password = "p",
            TlsMode = OceanBaseTlsMode.Disabled,
            Pooling = true,
            MaxPoolSize = Math.Max(1, minimum),
            MinPoolSize = minimum,
            ConnectionTimeout = 3,
            CommandTimeout = 2,
        }.ConnectionString;

        // Deliberately identical value equality must not merge distinct routing policies.
        private sealed class Resolver : IOceanBaseAddressResolver
        {
            internal Resolver(int port) => Port = port;
            internal int Port;
            internal int Calls;
            internal bool Fail;
            internal Func<CancellationToken, string>? Sync;
            internal Func<CancellationToken, Task<string>>? Async;
            public string Resolve(CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref Calls);
                if (Sync != null) return Sync(cancellationToken);
                if (Fail) throw new InvalidOperationException("discovery failed");
                return "127.0.0.1:" + Port;
            }
            public Task<string> ResolveAsync(CancellationToken cancellationToken)
            {
                if (Async == null) return Task.FromResult(Resolve(cancellationToken));
                Interlocked.Increment(ref Calls);
                return Async(cancellationToken);
            }
            public override bool Equals(object? obj) => obj is Resolver;
            public override int GetHashCode() => 1;
        }

        private sealed class RoutingServer : IAsyncDisposable
        {
            private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
            private readonly CancellationTokenSource _stop = new CancellationTokenSource();
            private readonly ConcurrentBag<Task> _clients = new ConcurrentBag<Task>();
            private readonly Task _accepting;
            private int _accepted;
            internal RoutingServer() { _listener.Start(); Port = ((IPEndPoint)_listener.LocalEndpoint).Port; _accepting = Accept(); }
            internal int Port { get; }
            internal int Accepted => Volatile.Read(ref _accepted);
            private async Task Accept()
            {
                try
                {
                    while (true)
                    {
                        var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                        Interlocked.Increment(ref _accepted);
                        _clients.Add(Serve(client));
                    }
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            }
            private async Task Serve(TcpClient client)
            {
                using (client)
                {
                    client.NoDelay = true;
                    var channel = new PacketChannel(client.GetStream());
                    try
                    {
                        await channel.WritePacketAsync(ProtocolFixture.Load("initial-handshake-oceanbase").Payload, _stop.Token);
                        await channel.ReadPacketAsync(_stop.Token);
                        await channel.WritePacketAsync(new byte[] { 0, 0, 0, 6, 0, 0, 0 }, _stop.Token);
                        await ServerIdentity.ReplyAsync(channel, token: _stop.Token);
                        while (true)
                        {
                            var request = await channel.ReadPacketAsync(_stop.Token);
                            Assert.Single(request);
                            Assert.True(request[0] == 0x1f || request[0] == 0x0e);
                            await channel.WritePacketAsync(new byte[] { 0, 0, 0, 6, 0, 0, 0 }, _stop.Token);
                            channel.BeginCommand();
                        }
                    }
                    catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
                    catch (ProtocolException error) when (error.Message.StartsWith("Connection closed while reading", StringComparison.Ordinal)) { }
                }
            }
            public async ValueTask DisposeAsync()
            {
                _stop.Cancel();
                await _accepting;
                _listener.Stop();
                await Task.WhenAll(_clients).WaitAsync(TimeSpan.FromSeconds(5));
                _stop.Dispose();
            }
        }
    }
}
