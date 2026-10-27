using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.Loader;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.UnitTests.Transport;
using Xunit;

namespace OceanBaseConnector.ConsumerTests
{
    public sealed partial class Ob20ConsumerTests
    {
        [Theory]
        [MemberData(nameof(Cases))]
        public async Task EveryAssetNegotiatesAndExecutesOb20OverTls(string target, bool async, string kind)
        {
            using var certificate = TestCertificateFactory.CreateSelfSigned();
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var serving = Serve(listener, kind, budget.Token, certificate);
            var context = new AssemblyLoadContext("ob20-tls-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                using var connection = (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode=Required;Use OB20={kind != "unrequested"}";
                if (async) await connection.OpenAsync(budget.Token); else connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "update t set x=1";
                    Assert.Equal(1, async ? await command.ExecuteNonQueryAsync(budget.Token) : command.ExecuteNonQuery());
                }
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "update t set x=:p";
                    var parameter = command.CreateParameter(); parameter.ParameterName = "p"; parameter.Value = 7; command.Parameters.Add(parameter);
                    Assert.Equal(1, async ? await command.ExecuteNonQueryAsync(budget.Token) : command.ExecuteNonQuery());
                }
                connection.Close(); await serving.WaitAsync(budget.Token);
            }
            finally { budget.Cancel(); listener.Stop(); context.Unload(); }
        }

        public static IEnumerable<object[]> TlsPartialCases()
        {
            foreach (var target in ConsumerTargetMatrix.Representative)
                foreach (var async in new[] { false, true })
                    foreach (var kind in new[] { "crc", "cancel", "timeout", "close" })
                        yield return new object[] { target, async, kind };
        }

        [Theory]
        [MemberData(nameof(TlsPartialCases))]
        public async Task EveryAssetDiscardsPartialOb20TlsResponsesAfterPublicFieldReads(string target, bool async, string kind)
        {
            using var certificate = TestCertificateFactory.CreateSelfSigned();
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var caller = new CancellationTokenSource();
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token);
                using var tls = new SslStream(peer.GetStream());
                var channel = new PacketChannel(peer.GetStream());
                await Handshake(channel, tls, "enabled", budget.Token, tls: tls, certificate: certificate);
                var request = await ReadRequest(tls, channel, true, 2, budget.Token);
                Assert.Equal(3, request[0]); Assert.Equal("select payload", Encoding.UTF8.GetString(request, 1, request.Length - 1));
                var column = new byte[] { 3, 100, 101, 102, 0, 0, 0, 1, 98, 1, 98, 12, 63, 0, 0, 4, 0, 0, 252, 128, 0, 0, 0, 0 };
                var metadata = Frame(1, new byte[] { 1 }).Concat(Frame(2, column)).Concat(Frame(3, new byte[] { 254, 0, 0, 6, 0 })).ToArray();
                var row = new byte[] { 252, 0, 4 }.Concat(Enumerable.Range(0, 1024).Select(i => (byte)i)).ToArray();
                var response = Ob20Packet.Build(1234, 2, 1, metadata.Concat(Frame(4, row)).Concat(Frame(5, new byte[] { 254, 0, 0, 6, 0 })).ToArray());
                var prefix = 31 + metadata.Length + 4 + 3 + 128;
                await tls.WriteAsync(response.AsMemory(0, prefix), budget.Token);
                if (kind == "crc")
                {
                    await finish.Task.WaitAsync(budget.Token);
                    response[response.Length - 1] ^= 1;
                    await tls.WriteAsync(response.AsMemory(prefix), budget.Token);
                }
                await ObserveClose(tls, budget.Token);
            });
            var context = new AssemblyLoadContext("ob20-tls-partial-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                using var connection = (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode=Required;Use OB20=true";
                if (async) await connection.OpenAsync(budget.Token); else connection.Open();
                using var command = connection.CreateCommand(); command.CommandText = "select payload"; command.CommandTimeout = kind == "timeout" ? 2 : 8;
                using var reader = async ? await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, budget.Token) : command.ExecuteReader(CommandBehavior.SequentialAccess);
                Assert.True(async ? await reader.ReadAsync(budget.Token) : reader.Read());
                using var field = reader.GetStream(0);
                var prefix = new byte[128];
                if (async) await field.ReadExactlyAsync(prefix, budget.Token); else field.ReadExactly(prefix);
                Assert.Equal(Enumerable.Range(0, 128).Select(i => (byte)i), prefix);
                if (kind == "close")
                {
                    connection.Close(); Assert.Equal(ConnectionState.Closed, connection.State);
                    Assert.Throws<InvalidOperationException>(() => command.ExecuteNonQuery());
                }
                else
                {
                    async Task ReadRest()
                    {
                        if (async) await field.CopyToAsync(Stream.Null, caller.Token); else field.CopyTo(Stream.Null);
                        Assert.False(async ? await reader.ReadAsync(caller.Token) : reader.Read());
                    }
                    var reading = async ? ReadRest() : Task.Run(ReadRest);
                    Assert.False(reading.IsCompleted);
                    if (kind == "crc") finish.SetResult();
                    if (kind == "cancel") { if (async) caller.Cancel(); else command.Cancel(); }
                    var error = await Assert.ThrowsAnyAsync<DbException>(() => reading.WaitAsync(budget.Token));
                    Assert.Equal("OceanBaseConnector.OceanBaseException", error.GetType().FullName);
                    Assert.Equal(true, error.GetType().GetProperty("IsExecutionOutcomeUnknown")!.GetValue(error));
                    if (kind == "cancel")
                    {
                        var canceled = Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
                        if (async) Assert.Equal(caller.Token, canceled.CancellationToken);
                    }
                    if (kind == "timeout") Assert.IsType<TimeoutException>(error.InnerException);
                    Assert.Equal(ConnectionState.Broken, connection.State);
                }
                await serving.WaitAsync(budget.Token);
            }
            finally { budget.Cancel(); listener.Stop(); context.Unload(); }
        }
    }
}
