using System;
using System.Data;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using OceanBaseConnector.UnitTests.Transport;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        private static Socket RefusedEndpoint()
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            return socket;
        }

        private static int PortOf(Socket socket) => ((IPEndPoint)socket.LocalEndPoint!).Port;

        private static TcpListener UnusedFallback()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return listener;
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task MultiAddressFallsBackOnlyBeforeSessionEstablishment(bool asynchronous, bool inheritedPort)
        {
            using var refused = RefusedEndpoint();
            var server = Server(async channel =>
            {
                await Query(channel, "fallback query");
                await channel.WritePacketAsync(Ok(3));
            });
            var candidates = $"127.0.0.1:{PortOf(refused)}, " + (inheritedPort ? "127.0.0.1" : $"127.0.0.1:{server.Port}");
            var builder = new OceanBaseConnectionStringBuilder(Configuration(server.Port)) { Server = candidates };
            using var log = new RecordingLoggerFactory();
            using var connection = new OceanBaseConnection(builder.ConnectionString, log);
            try
            {
                if (asynchronous) await connection.OpenAsync(); else connection.Open();
                Assert.Equal(candidates, connection.DataSource);
                Assert.Equal(OceanBaseCompatibilityMode.Oracle, connection.CompatibilityMode);
                using var command = connection.CreateCommand();
                command.CommandText = "fallback query";
                Assert.Equal(3, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                connection.Close();
                foreach (var entry in log.Entries)
                {
                    Assert.DoesNotContain("secret", entry.Message);
                    if (entry.EventId.Id == 1000) continue;
                    Assert.Contains($"Endpoint=127.0.0.1:{server.Port}", entry.Message);
                    Assert.DoesNotContain(candidates, entry.Message);
                }
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, 0)]
        [InlineData(true, 0)]
        [InlineData(false, 1)]
        [InlineData(true, 1)]
        [InlineData(false, 2)]
        [InlineData(true, 2)]
        [InlineData(false, 3)]
        [InlineData(true, 3)]
        [InlineData(false, 4)]
        [InlineData(true, 4)]
        public async Task MultiAddressDoesNotFailOverAfterTcpConnection(bool asynchronous, int failurePhase)
        {
            using var fallback = UnusedFallback();
            var server = LoopbackServer.Start(async client =>
            {
                if (failurePhase == 0) return; // EOF before initial handshake.
                var channel = new PacketChannel(client.GetStream());
                await channel.WritePacketAsync(ProtocolFixture.Load("initial-handshake-oceanbase").Payload);
                await channel.ReadPacketAsync();
                await channel.WritePacketAsync(failurePhase == 1 ? Error() : Ok(status: failurePhase == 4 ? (byte)2 : (byte)6));
                if (failurePhase == 3)
                {
                    channel.BeginCommand();
                    await Query(channel, "must not replay");
                }
            });
            var builder = new OceanBaseConnectionStringBuilder(Configuration(server.Port))
            {
                Server = $"127.0.0.1:{server.Port},127.0.0.1:{((IPEndPoint)fallback.LocalEndpoint).Port}",
            };
            if (failurePhase == 2) builder.ExpectedMode = OceanBaseCompatibilityMode.MySql;
            using var connection = new OceanBaseConnection(builder.ConnectionString);
            try
            {
                var error = await Record.ExceptionAsync(async () =>
                {
                    if (asynchronous) await connection.OpenAsync(); else connection.Open();
                    Assert.Equal(3, failurePhase);
                    using var command = connection.CreateCommand(); command.CommandText = "must not replay";
                    if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery();
                });
                var databaseError = Assert.IsType<OceanBaseException>(error);
                Assert.Equal(failurePhase == 3, databaseError.IsExecutionOutcomeUnknown);
                Assert.DoesNotContain("secret", databaseError.Message);
                if (failurePhase == 1) Assert.Equal(1064, databaseError.ServerErrorCode);
                if (failurePhase == 2) Assert.Contains("ExpectedMode", databaseError.Message);
                if (failurePhase == 4) Assert.Contains("cannot be confirmed", databaseError.Message);
                Assert.False(fallback.Pending());
                Assert.NotEqual(ConnectionState.Open, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task MultiAddressReportsLastTcpFailureWithoutCredentials(bool asynchronous)
        {
            using var first = RefusedEndpoint(); using var last = RefusedEndpoint();
            var builder = new OceanBaseConnectionStringBuilder(Configuration(PortOf(first)))
            { Server = $"127.0.0.1:{PortOf(first)},127.0.0.1:{PortOf(last)}" };
            using var log = new RecordingLoggerFactory();
            using var connection = new OceanBaseConnection(builder.ConnectionString, log);
            var error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? connection.OpenAsync() : Task.Run(connection.Open));
            Assert.Contains($"127.0.0.1:{PortOf(last)}", error.Message);
            Assert.DoesNotContain("secret", error.ToString());
            Assert.False(error.IsExecutionOutcomeUnknown);
            Assert.Equal(ConnectionState.Closed, connection.State);
            foreach (var entry in log.Entries) Assert.DoesNotContain("secret", entry.Message);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task MultiAddressDataSourceOpensThroughTheSameFallbackPath(bool asynchronous)
        {
            using var refused = RefusedEndpoint();
            var server = Server(async channel =>
            {
                await Query(channel, "source query");
                await channel.WritePacketAsync(Ok(2));
            });
            var configuration = new OceanBaseConnectionStringBuilder(Configuration(server.Port))
            { Server = $"127.0.0.1:{PortOf(refused)},127.0.0.1" };
            using var source = new OceanBaseDataSourceBuilder(configuration.ConnectionString).Build();
            try
            {
                using var connection = asynchronous ? await source.OpenConnectionAsync() : source.OpenConnection();
                using var command = connection.CreateCommand(); command.CommandText = "source query";
                Assert.Equal(2, await command.ExecuteNonQueryAsync());
            }
            finally { server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task MultiAddressReopenStartsAtFirstHostAndDoesNotProbeUnusedHosts(bool asynchronous)
        {
            using var first = RefusedEndpoint(); using var unused = UnusedFallback();
            var second = Server(async channel =>
            {
                await Query(channel, "first open"); await channel.WritePacketAsync(Ok(1));
            });
            var configuration = new OceanBaseConnectionStringBuilder(Configuration(second.Port))
            { Server = $"127.0.0.1:{PortOf(first)},127.0.0.1:{second.Port},127.0.0.1:{((IPEndPoint)unused.LocalEndpoint).Port}" };
            using var connection = new OceanBaseConnection(configuration.ConnectionString);
            Task? primaryServing = null;
            try
            {
                if (asynchronous) await connection.OpenAsync(); else connection.Open();
                using var command = connection.CreateCommand(); command.CommandText = "first open";
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
                connection.Close();
                await second.Completion.WaitAsync(TimeSpan.FromSeconds(10));
                first.Listen(1);
                primaryServing = Task.Run(async () =>
                {
                    using var accepted = await first.AcceptAsync();
                    using var peer = new NetworkStream(accepted);
                    var channel = new PacketChannel(peer);
                    await channel.WritePacketAsync(ProtocolFixture.Load("initial-handshake-oceanbase").Payload);
                    await channel.ReadPacketAsync(); await channel.WritePacketAsync(Ok());
                    channel.BeginCommand(); await Query(channel, "reopen"); await channel.WritePacketAsync(Ok(7));
                });
                if (asynchronous) await connection.OpenAsync(); else connection.Open();
                command.CommandText = "reopen";
                Assert.Equal(7, await command.ExecuteNonQueryAsync());
                connection.Close();
                Assert.False(unused.Pending());
                await primaryServing.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally { connection.Close(); first.Dispose(); second.Stop(); }
        }

        [Fact]
        public async Task MultiAddressPreCanceledOpenContactsNoCandidate()
        {
            using var first = UnusedFallback(); using var second = UnusedFallback();
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            var configuration = new OceanBaseConnectionStringBuilder(Configuration(((IPEndPoint)first.LocalEndpoint).Port))
            { Server = $"localhost,localhost:{((IPEndPoint)second.LocalEndpoint).Port}" };
            using var connection = new OceanBaseConnection(configuration.ConnectionString);
            var error = await Assert.ThrowsAsync<OperationCanceledException>(() => connection.OpenAsync(cancellation.Token));
            Assert.Equal(cancellation.Token, error.CancellationToken);
            Assert.False(first.Pending()); Assert.False(second.Pending());
            Assert.Equal(ConnectionState.Closed, connection.State);
        }

        [Theory]
        [InlineData(false, 0)]
        [InlineData(true, 0)]
        [InlineData(false, 1)]
        [InlineData(true, 1)]
        [InlineData(false, 2)]
        [InlineData(true, 2)]
        public async Task MultiAddressStopsOnCancellationCloseOrHandshakeTimeout(bool asynchronous, int stop)
        {
            using var fallback = UnusedFallback();
            var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = LoopbackServer.Start(async client =>
            {
                accepted.SetResult();
                Assert.Equal(0, await client.GetStream().ReadUpToAsync(new byte[1], 1));
            });
            var builder = new OceanBaseConnectionStringBuilder(Configuration(server.Port))
            {
                Server = $"127.0.0.1:{server.Port},127.0.0.1:{((IPEndPoint)fallback.LocalEndpoint).Port}",
                ConnectionTimeout = stop == 2 ? 1 : 0,
            };
            using var caller = new CancellationTokenSource();
            using var connection = new OceanBaseConnection(builder.ConnectionString);
            try
            {
                var opening = asynchronous ? connection.OpenAsync(caller.Token) : Task.Run(connection.Open);
                await accepted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                if (stop == 0)
                {
                    if (asynchronous) caller.Cancel(); else connection.Close();
                }
                if (stop == 1) connection.Close();
                var error = await Record.ExceptionAsync(() => opening.WaitAsync(TimeSpan.FromSeconds(10)));
                if (stop == 2) Assert.IsType<TimeoutException>(error);
                else
                {
                    var canceled = Assert.IsType<OperationCanceledException>(error);
                    if (stop == 0 && asynchronous) Assert.Equal(caller.Token, canceled.CancellationToken);
                }
                Assert.False(fallback.Pending());
                Assert.Equal(ConnectionState.Closed, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task MultiAddressTimeoutBoundsTheWholeOpen(bool asynchronous)
        {
            using var refused = RefusedEndpoint(); using var fallback = UnusedFallback();
            var candidate = $"127.0.0.1:{PortOf(refused)},";
            var builder = new OceanBaseConnectionStringBuilder(Configuration(PortOf(refused)))
            {
                Server = string.Concat(candidate, candidate, candidate, candidate, candidate, "127.0.0.1:" + ((IPEndPoint)fallback.LocalEndpoint).Port),
                ConnectionTimeout = 1,
            };
            using var connection = new OceanBaseConnection(builder.ConnectionString);
            var timer = Stopwatch.StartNew();
            await Assert.ThrowsAsync<TimeoutException>(() => asynchronous ? connection.OpenAsync() : Task.Run(connection.Open));
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(4), timer.Elapsed.ToString());
            Assert.Equal(ConnectionState.Closed, connection.State);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task MultiAddressTlsUsesActualHostOrExplicitName(bool asynchronous, bool explicitName)
        {
            using var refused = RefusedEndpoint();
            using var certificate = TestCertificateFactory.CreateSelfSigned();
            string? sni = null;
            var server = LoopbackServer.Start(async client =>
            {
                var channel = new PacketChannel(client.GetStream());
                var greeting = (byte[])ProtocolFixture.Load("initial-handshake-oceanbase").Payload.Clone();
                // Advertise connection attributes so the actual authentication host can be checked too.
                greeting[Array.IndexOf(greeting, (byte)0, 1) + 19] |= 16;
                await channel.WritePacketAsync(greeting);
                Assert.Equal(32, (await channel.ReadPacketAsync()).Length);
                using var tls = new SslStream(client.GetStream());
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificateSelectionCallback = (_, name) => { sni = name; return certificate; },
                });
                channel.ReplaceUnderlyingStream(tls);
                var authentication = Encoding.UTF8.GetString(await channel.ReadPacketAsync());
                Assert.Contains("_server_host\u0009localhost", authentication);
                await channel.WritePacketAsync(Ok());
                Assert.Equal(0, await tls.ReadUpToAsync(new byte[1], 1));
            });
            var builder = new OceanBaseConnectionStringBuilder(Configuration(server.Port))
            { Server = $"127.0.0.1:{PortOf(refused)},localhost", TlsMode = OceanBaseTlsMode.Required };
            if (explicitName) builder.TlsServerName = "cluster.example.test";
            using var connection = new OceanBaseConnection(builder.ConnectionString);
            try
            {
                if (asynchronous) await connection.OpenAsync(); else connection.Open();
                Assert.Equal(explicitName ? "cluster.example.test" : "localhost", sni);
                connection.Close();
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task MultiAddressVerifyFullFailureDoesNotTryAnotherHost(bool asynchronous)
        {
            using var fallback = UnusedFallback();
            using var certificate = TestCertificateFactory.CreateSelfSigned();
            var server = LoopbackServer.Start(async client =>
            {
                var channel = new PacketChannel(client.GetStream());
                await channel.WritePacketAsync(ProtocolFixture.Load("initial-handshake-oceanbase").Payload);
                Assert.Equal(32, (await channel.ReadPacketAsync()).Length);
                using var tls = new SslStream(client.GetStream());
                try
                {
                    await tls.AuthenticateAsServerAsync(certificate);
                    Assert.Equal(0, await tls.ReadUpToAsync(new byte[1], 1));
                }
                catch (AuthenticationException) { }
            });
            var builder = new OceanBaseConnectionStringBuilder(Configuration(server.Port))
            {
                Server = $"localhost:{server.Port},localhost:{((IPEndPoint)fallback.LocalEndpoint).Port}",
                TlsMode = OceanBaseTlsMode.VerifyFull,
            };
            using var connection = new OceanBaseConnection(builder.ConnectionString);
            try
            {
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? connection.OpenAsync() : Task.Run(connection.Open));
                Assert.IsType<AuthenticationException>(error.InnerException);
                Assert.False(fallback.Pending());
                Assert.Equal(ConnectionState.Closed, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    public sealed class MultiAddressOptionsTests
    {
        [Theory]
        [InlineData("ob1,ob2:2883,[::1]:2884", "ob1:2999,ob2:2883,[::1]:2884")]
        [InlineData(" ::1 , [2001:db8::1] ", "[::1]:2999,[2001:db8::1]:2999")]
        [InlineData("localhost:2881", "localhost:2881")]
        public void CandidatesAreNormalizedAndFrozenWithAllConnectionOptions(string servers, string expected)
        {
            var builder = new OceanBaseConnectionStringBuilder
            {
                Server = servers,
                Port = 2999,
                UserId = "u",
                Password = "secret",
                Database = "db",
                TlsMode = OceanBaseTlsMode.VerifyFull,
                TlsServerName = "cluster.test",
                ExpectedMode = OceanBaseCompatibilityMode.Oracle,
                CommandTimeout = 12,
                ConnectionTimeout = 7,
                PersistSecurityInfo = true,
            };
            var options = builder.Build();
            builder.Server = "other"; builder.Port = 3000; builder.Password = "new";
            Assert.Equal(expected, options.Endpoint);
            foreach (var endpoint in options.Endpoints)
            {
                var selected = options.ForEndpoint(endpoint);
                Assert.Equal(endpoint.Host, selected.Server); Assert.Equal(endpoint.Port, selected.Port);
                Assert.Equal("u", selected.UserId); Assert.Equal("secret", selected.Password); Assert.Equal("db", selected.Database);
                Assert.Equal(7, selected.ConnectionTimeoutSeconds); Assert.Equal(12, selected.CommandTimeoutSeconds);
                Assert.Equal(OceanBaseTlsMode.VerifyFull, selected.TlsMode); Assert.Equal("cluster.test", selected.TlsServerName);
                Assert.Equal(OceanBaseCompatibilityMode.Oracle, selected.ExpectedMode); Assert.True(selected.PersistSecurityInfo);
                Assert.False(selected.Pooling); Assert.DoesNotContain("secret", selected.ToString());
            }
        }

        [Theory]
        [InlineData(",ob1")]
        [InlineData("ob1,")]
        [InlineData("ob1,,ob2")]
        [InlineData("ob1, ,ob2")]
        [InlineData("ob1:0")]
        [InlineData("ob1:65536")]
        [InlineData("ob1:-1")]
        [InlineData("ob1:+1")]
        [InlineData("ob1:abc")]
        [InlineData("ob1:")]
        [InlineData(":2881")]
        [InlineData("[::1")]
        [InlineData("[::1]x")]
        [InlineData("[::1]:")]
        [InlineData("[::1]:0")]
        [InlineData("[ob1]:2881")]
        [InlineData("ob1:2881:extra")]
        [InlineData("jdbc://ob1")]
        [InlineData("u:secret@ob1")]
        [InlineData("ob 1")]
        [InlineData("ob1/path")]
        [InlineData("ob1\\path")]
        public void InvalidAddressListsAreRejectedWithoutEchoingValues(string servers)
        {
            var builder = new OceanBaseConnectionStringBuilder { UserId = "u" };
            var error = Assert.Throws<ArgumentException>(() => builder.Server = servers);
            Assert.DoesNotContain(servers, error.Message);
            Assert.DoesNotContain("secret", error.Message);
        }
    }
}
