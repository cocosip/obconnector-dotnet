using System;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.UnitTests.Transport;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        private sealed class AddressResolver : IOceanBaseAddressResolver, IDisposable
        {
            internal Func<CancellationToken, string> Sync = _ => throw new InvalidOperationException("Wrong resolver method.");
            internal Func<CancellationToken, Task<string>> Async = _ => throw new InvalidOperationException("Wrong resolver method.");
            internal int Calls;
            internal int DisposeCount;
            public void Dispose() => DisposeCount++;
            public string Resolve(CancellationToken token) { Interlocked.Increment(ref Calls); return Sync(token); }
            public Task<string> ResolveAsync(CancellationToken token) { Interlocked.Increment(ref Calls); return Async(token); }
            internal static AddressResolver Returning(string addresses) => new AddressResolver
            { Sync = _ => addresses, Async = _ => Task.FromResult(addresses) };
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task TopologyRoundRobinRotatesDirectReopensAndSourceConnections(bool asynchronous, bool dataSource)
        {
            var servers = Enumerable.Range(1, 3).Select(value => Server(async channel =>
            { await Query(channel, "selected"); await channel.WritePacketAsync(Ok((byte)value)); })).ToArray();
            var builder = new OceanBaseConnectionStringBuilder(Configuration(servers[0].Port))
            { Server = string.Join(",", servers.Select(s => "127.0.0.1:" + s.Port)), LoadBalance = OceanBaseLoadBalance.RoundRobin };
            using var source = dataSource ? new OceanBaseDataSourceBuilder(builder.ConnectionString).Build() : null;
            using var direct = dataSource ? null : new OceanBaseConnection(builder.ConnectionString);
            try
            {
                for (var i = 0; i < servers.Length; i++)
                {
                    var connection = direct ?? (OceanBaseConnection)source!.CreateConnection();
                    try
                    {
                        if (asynchronous) await connection.OpenAsync(); else connection.Open();
                        using var command = connection.CreateCommand(); command.CommandText = "selected";
                        Assert.Equal(i + 1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                        connection.Close();
                    }
                    finally { if (dataSource) connection.Dispose(); }
                }
            }
            finally { direct?.Close(); foreach (var server in servers) server.Stop(); }
            await Task.WhenAll(servers.Select(s => s.Completion)).WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task TopologyDiscoveryRefreshesEachOpenAndUsesOnlyMatchingMethod(bool asynchronous)
        {
            using var unused = UnusedFallback();
            var servers = Enumerable.Range(1, 2).Select(value => Server(async channel =>
            { await Query(channel, "fresh"); await channel.WritePacketAsync(Ok((byte)value)); })).ToArray();
            var resolver = new AddressResolver();
            var calls = 0;
            string Resolve() => calls++ == 0 ? "127.0.0.1" : "127.0.0.1:" + servers[1].Port;
            if (asynchronous) resolver.Async = _ => Task.FromResult(Resolve()); else resolver.Sync = _ => Resolve();
            var builder = new OceanBaseConnectionStringBuilder(Configuration(servers[0].Port))
            { Server = "127.0.0.1:" + ((IPEndPoint)unused.LocalEndpoint).Port };
            using var connection = new OceanBaseConnection(builder.ConnectionString) { AddressResolver = resolver };
            try
            {
                for (var i = 0; i < 2; i++)
                {
                    if (asynchronous) await connection.OpenAsync(); else connection.Open();
                    Assert.Throws<InvalidOperationException>(() => connection.AddressResolver = null);
                    Assert.Same(resolver, connection.AddressResolver);
                    using var command = connection.CreateCommand(); command.CommandText = "fresh";
                    Assert.Equal(i + 1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                    connection.Close();
                }
                Assert.Equal(2, resolver.Calls); Assert.False(unused.Pending());
                connection.AddressResolver = null;
            }
            finally { connection.Close(); foreach (var server in servers) server.Stop(); }
            await Task.WhenAll(servers.Select(s => s.Completion)).WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, "empty")]
        [InlineData(true, "empty")]
        [InlineData(false, "null")]
        [InlineData(true, "null")]
        [InlineData(false, "invalid")]
        [InlineData(true, "invalid")]
        [InlineData(false, "length")]
        [InlineData(true, "length")]
        [InlineData(false, "count")]
        [InlineData(true, "count")]
        [InlineData(false, "throw")]
        [InlineData(true, "throw")]
        [InlineData(true, "null-task")]
        public async Task TopologyInvalidDiscoveryNeverFallsBackToConfiguredServer(bool asynchronous, string kind)
        {
            using var unused = UnusedFallback();
            var value = kind == "empty" ? "" : kind == "null" ? null : kind == "length" ? new string('x', 65537)
                : kind == "count" ? string.Join(",", Enumerable.Repeat("localhost", 257)) : "secret@invalid";
            var resolver = new AddressResolver
            {
                Sync = _ => kind == "throw" ? throw new InvalidOperationException("resolver failure") : value!,
                Async = _ => kind == "null-task" ? null! : kind == "throw" ? Task.FromException<string>(new InvalidOperationException("resolver failure")) : Task.FromResult(value!),
            };
            using var connection = new OceanBaseConnection(Configuration(((IPEndPoint)unused.LocalEndpoint).Port)) { AddressResolver = resolver };
            var error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? connection.OpenAsync() : Task.Run(connection.Open));
            Assert.DoesNotContain("secret", error.ToString()); Assert.Equal(1, resolver.Calls);
            Assert.False(unused.Pending()); Assert.Equal(ConnectionState.Closed, connection.State);
        }

        [Theory]
        [InlineData(false, "close")]
        [InlineData(true, "close")]
        [InlineData(false, "timeout")]
        [InlineData(true, "timeout")]
        [InlineData(true, "cancel")]
        public async Task TopologyDiscoveryUsesOpeningBudgetAndCloseToken(bool asynchronous, string stop)
        {
            using var unused = UnusedFallback(); using var caller = new CancellationTokenSource();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var resolver = new AddressResolver
            {
                Sync = token => { entered.SetResult(); token.WaitHandle.WaitOne(); token.ThrowIfCancellationRequested(); return "localhost"; },
                Async = async token => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return "localhost"; },
            };
            var builder = new OceanBaseConnectionStringBuilder(Configuration(((IPEndPoint)unused.LocalEndpoint).Port)) { ConnectionTimeout = stop == "timeout" ? 1 : 5 };
            using var connection = new OceanBaseConnection(builder.ConnectionString) { AddressResolver = resolver };
            var opening = asynchronous ? connection.OpenAsync(caller.Token) : Task.Run(connection.Open);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (stop == "close") connection.Close(); else if (stop == "cancel") caller.Cancel();
            var error = await Record.ExceptionAsync(() => opening.WaitAsync(TimeSpan.FromSeconds(5)));
            if (stop == "timeout") Assert.IsType<TimeoutException>(error);
            else { var canceled = Assert.IsType<OperationCanceledException>(error); if (stop == "cancel") Assert.Equal(caller.Token, canceled.CancellationToken); }
            Assert.False(unused.Pending()); Assert.Equal(ConnectionState.Closed, connection.State);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task TopologyDiscoveryAndHandshakeShareOneTotalTimeout(bool asynchronous)
        {
            var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = LoopbackServer.Start(async client =>
            {
                accepted.SetResult();
                Assert.Equal(0, await client.GetStream().ReadUpToAsync(new byte[1], 1));
            });
            var resolver = new AddressResolver
            {
                Sync = token => { token.WaitHandle.WaitOne(TimeSpan.FromSeconds(2)); token.ThrowIfCancellationRequested(); return "127.0.0.1"; },
                Async = async token => { await Task.Delay(TimeSpan.FromSeconds(2), token); return "127.0.0.1"; },
            };
            var builder = new OceanBaseConnectionStringBuilder(Configuration(server.Port)) { ConnectionTimeout = 3 };
            using var connection = new OceanBaseConnection(builder.ConnectionString) { AddressResolver = resolver };
            try
            {
                var timer = Stopwatch.StartNew();
                await Assert.ThrowsAsync<TimeoutException>(() => asynchronous ? connection.OpenAsync() : Task.Run(connection.Open));
                Assert.True(accepted.Task.IsCompletedSuccessfully, "The discovered endpoint was not reached.");
                Assert.True(timer.Elapsed < TimeSpan.FromSeconds(4.5), timer.Elapsed.ToString());
                Assert.Equal(ConnectionState.Closed, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task TopologyLateAsyncDiscoveryCannotConnectAfterCanceledOpen(bool fault)
        {
            using var unused = UnusedFallback(); using var caller = new CancellationTokenSource();
            var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var resolver = new AddressResolver { Async = _ => pending.Task };
            using var connection = new OceanBaseConnection(Configuration(((IPEndPoint)unused.LocalEndpoint).Port)) { AddressResolver = resolver };
            var opening = connection.OpenAsync(caller.Token); caller.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal(caller.Token, error.CancellationToken);
            if (fault) pending.SetException(new InvalidOperationException("late")); else pending.SetResult("127.0.0.1");
            await Task.Yield(); Assert.False(unused.Pending()); Assert.Equal(ConnectionState.Closed, connection.State);
            Assert.Same(resolver, connection.AddressResolver);
        }

        [Fact]
        public async Task TopologyOldDiscoveryCannotPublishIntoReopenedConnection()
        {
            using var unused = UnusedFallback();
            var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = Server(async channel => { await Query(channel, "new session"); await channel.WritePacketAsync(Ok(3)); });
            using var connection = new OceanBaseConnection(Configuration(server.Port)) { AddressResolver = new AddressResolver { Async = _ => pending.Task } };
            try
            {
                var oldOpening = connection.OpenAsync(); connection.Close();
                connection.AddressResolver = AddressResolver.Returning("127.0.0.1"); await connection.OpenAsync();
                pending.SetResult("127.0.0.1:" + ((IPEndPoint)unused.LocalEndpoint).Port);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => oldOpening);
                Assert.Equal(ConnectionState.Open, connection.State);
                using var command = connection.CreateCommand(); command.CommandText = "new session";
                Assert.Equal(3, await command.ExecuteNonQueryAsync()); Assert.False(unused.Pending());
            }
            finally { pending.TrySetCanceled(); connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task TopologyCloseInvokesResolverCancellationWithoutHoldingSessionLock(bool throwingCallback)
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var inspected = false;
            OceanBaseConnector.Internal.Session.PhysicalSession? session = null;
            var resolver = new AddressResolver
            {
                Async = token =>
                {
                    token.Register(() =>
                    {
                        inspected = Task.Run(() => session!.State).Wait(TimeSpan.FromSeconds(2));
                        if (throwingCallback) throw new InvalidOperationException("Application cancellation callback failed.");
                    });
                    entered.SetResult(); return Task.Delay(Timeout.Infinite, token).ContinueWith(_ => "localhost");
                },
            };
            using (session = new OceanBaseConnector.Internal.Session.PhysicalSession(new OceanBaseConnectionStringBuilder("Server=localhost;User ID=u").Build(), resolver))
            {
                var opening = session.OpenAsync(); await entered.Task;
                session.Close();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening);
                Assert.True(inspected, "Resolver cancellation ran under the physical session lock.");
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task TopologyDirectReconfigurationResetsRoundRobin(bool connectionString)
        {
            using var unused = UnusedFallback();
            var servers = Enumerable.Range(1, 2).Select(value => Server(async channel =>
            { await Query(channel, "reset"); await channel.WritePacketAsync(Ok((byte)value)); })).ToArray();
            var index = 0;
            var resolver = new AddressResolver { Async = _ => Task.FromResult($"127.0.0.1:{servers[index++].Port},127.0.0.1:{((IPEndPoint)unused.LocalEndpoint).Port}") };
            var configuration = Configuration(servers[0].Port) + ";Load Balance=RoundRobin";
            using var connection = new OceanBaseConnection(configuration) { AddressResolver = resolver };
            try
            {
                for (var i = 0; i < 2; i++)
                {
                    await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "reset";
                    Assert.Equal(i + 1, await command.ExecuteNonQueryAsync()); connection.Close();
                    if (connectionString) connection.ConnectionString = configuration; else connection.AddressResolver = resolver;
                }
                Assert.False(unused.Pending());
            }
            finally { connection.Close(); foreach (var server in servers) server.Stop(); }
            await Task.WhenAll(servers.Select(s => s.Completion)).WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public void TopologySourceFreezesResolverAndOwnsNoApplicationResources()
        {
            var resolver = AddressResolver.Returning("localhost");
            var builder = new OceanBaseDataSourceBuilder("Server=localhost;User ID=u").UseAddressResolver(resolver);
            using var source = builder.Build(); builder.UseAddressResolver(null);
            using var connection = (OceanBaseConnection)source.CreateConnection();
            Assert.Same(resolver, connection.AddressResolver);
            Assert.Throws<InvalidOperationException>(() => connection.AddressResolver = null);
            source.Dispose(); Assert.Equal(0, resolver.DisposeCount);
            using var direct = new OceanBaseConnection { AddressResolver = resolver }; direct.Dispose();
            Assert.Equal(0, resolver.DisposeCount);
            Assert.Throws<ObjectDisposedException>(() => direct.AddressResolver = resolver);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task TopologyConcurrentDataSourceOpensShareOnlyTheSelector(bool asynchronous)
        {
            var servers = Enumerable.Range(1, 3).Select(value => Server(async channel =>
            { await Query(channel, "parallel"); await channel.WritePacketAsync(Ok((byte)value)); })).ToArray();
            var addresses = string.Join(",", servers.Select(s => "127.0.0.1:" + s.Port));
            var resolver = AddressResolver.Returning(addresses);
            using var source = new OceanBaseDataSourceBuilder(Configuration(servers[0].Port) + ";Load Balance=RoundRobin").UseAddressResolver(resolver).Build();
            try
            {
                var values = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(async () =>
                {
                    using var connection = source.CreateConnection();
                    if (asynchronous) await connection.OpenAsync(); else connection.Open();
                    using var command = connection.CreateCommand(); command.CommandText = "parallel";
                    return asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery();
                })));
                Assert.Equal(new[] { 1, 2, 3 }, values.OrderBy(x => x)); Assert.Equal(3, resolver.Calls);
            }
            finally { foreach (var server in servers) server.Stop(); }
            await Task.WhenAll(servers.Select(s => s.Completion)).WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task TopologyRoundRobinWrapsAfterRefusedFirstCandidate(bool asynchronous, bool dataSource)
        {
            using var refused = RefusedEndpoint();
            var first = Server(async channel => { await Query(channel, "selected"); await channel.WritePacketAsync(Ok(1)); });
            var second = Server(async channel => { await Query(channel, "selected"); await channel.WritePacketAsync(Ok(2)); });
            var calls = 0;
            string Addresses() => $"127.0.0.1:{(Interlocked.Increment(ref calls) == 1 ? first.Port : second.Port)},127.0.0.1:{PortOf(refused)}";
            var resolver = new AddressResolver { Sync = _ => Addresses(), Async = _ => Task.FromResult(Addresses()) };
            var builder = new OceanBaseConnectionStringBuilder(Configuration(first.Port)) { LoadBalance = OceanBaseLoadBalance.RoundRobin };
            using var source = dataSource ? new OceanBaseDataSourceBuilder(builder.ConnectionString).UseAddressResolver(resolver).Build() : null;
            using var direct = dataSource ? null : new OceanBaseConnection(builder.ConnectionString) { AddressResolver = resolver };
            try
            {
                for (var i = 0; i < 2; i++)
                {
                    var connection = direct ?? (OceanBaseConnection)source!.CreateConnection();
                    try
                    {
                        if (asynchronous) await connection.OpenAsync(); else connection.Open();
                        using var command = connection.CreateCommand(); command.CommandText = "selected";
                        Assert.Equal(i + 1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                        connection.Close();
                    }
                    finally { if (dataSource) connection.Dispose(); }
                }
                Assert.Equal(2, resolver.Calls);
            }
            finally { direct?.Close(); first.Stop(); second.Stop(); }
            await Task.WhenAll(first.Completion, second.Completion).WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, "auth")]
        [InlineData(true, "auth")]
        [InlineData(false, "mode")]
        [InlineData(true, "mode")]
        [InlineData(false, "transaction")]
        [InlineData(true, "transaction")]
        public async Task TopologyNeverDiscoversOrSwitchesAgainAfterAuthenticationOrSql(bool asynchronous, string failure)
        {
            using var unused = UnusedFallback();
            var server = LoopbackServer.Start(async client =>
            {
                var channel = new PacketChannel(client.GetStream());
                await channel.WritePacketAsync(ProtocolFixture.Load("initial-handshake-oceanbase").Payload);
                await channel.ReadPacketAsync(); await channel.WritePacketAsync(failure == "auth" ? Error() : Ok());
                if (failure != "transaction") return;
                await ServerIdentity.ReplyAsync(channel);
                channel.BeginCommand(); await Query(channel, "SET SESSION TRANSACTION ISOLATION LEVEL READ COMMITTED"); await channel.WritePacketAsync(Ok());
                channel.BeginCommand(); await Query(channel, "set autocommit = 0"); await channel.WritePacketAsync(Ok(status: 4));
                channel.BeginCommand(); await Query(channel, "must not replay");
            });
            var resolver = AddressResolver.Returning($"127.0.0.1:{server.Port},127.0.0.1:{((IPEndPoint)unused.LocalEndpoint).Port}");
            var builder = new OceanBaseConnectionStringBuilder(Configuration(server.Port)) { LoadBalance = OceanBaseLoadBalance.RoundRobin };
            if (failure == "mode") builder.ExpectedMode = OceanBaseCompatibilityMode.MySql;
            using var connection = new OceanBaseConnection(builder.ConnectionString) { AddressResolver = resolver };
            try
            {
                var error = await Assert.ThrowsAsync<OceanBaseException>(async () =>
                {
                    if (asynchronous) await connection.OpenAsync(); else connection.Open();
                    using var transaction = connection.BeginTransaction();
                    using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "must not replay";
                    if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery();
                });
                Assert.Equal(failure == "transaction", error.IsExecutionOutcomeUnknown);
                Assert.Equal(1, resolver.Calls); Assert.False(unused.Pending()); Assert.NotEqual(ConnectionState.Open, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task TopologyDiscoveredTlsHostIsUsedAndIdentityFailureDoesNotSwitch(bool asynchronous)
        {
            using var unused = UnusedFallback(); using var certificate = TestCertificateFactory.CreateSelfSigned();
            string? sni = null;
            var server = LoopbackServer.Start(async client =>
            {
                var channel = new PacketChannel(client.GetStream());
                await channel.WritePacketAsync(ProtocolFixture.Load("initial-handshake-oceanbase").Payload);
                Assert.Equal(32, (await channel.ReadPacketAsync()).Length);
                using var tls = new SslStream(client.GetStream(), false);
                try
                {
                    await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificateSelectionCallback = (_, name) => { sni = name; return certificate; },
                    });
                }
                catch (System.Security.Authentication.AuthenticationException) { }
                catch (System.IO.IOException) { }
            });
            var resolver = AddressResolver.Returning($"localhost:{server.Port},127.0.0.1:{((IPEndPoint)unused.LocalEndpoint).Port}");
            var builder = new OceanBaseConnectionStringBuilder(Configuration(server.Port)) { Server = "bootstrap.invalid", TlsMode = OceanBaseTlsMode.VerifyFull };
            using var connection = new OceanBaseConnection(builder.ConnectionString) { AddressResolver = resolver };
            try
            {
                await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? connection.OpenAsync() : Task.Run(connection.Open));
                Assert.Equal("localhost", sni); Assert.Equal(1, resolver.Calls); Assert.False(unused.Pending());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
