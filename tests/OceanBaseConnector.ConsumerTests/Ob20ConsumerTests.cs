using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.Loader;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.ConsumerTests
{
    public sealed partial class Ob20ConsumerTests
    {

        [Theory]
        [MemberData(nameof(Cases))]
        public async Task EveryAssetNegotiatesOb20OrKeepsStandardFramesThroughPublicApis(string target, bool async, string kind)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var serving = Serve(listener, kind, budget.Token);
            var context = new AssemblyLoadContext("ob20-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                using var connection = (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!)!;
                connection.ConnectionString = new DbConnectionStringBuilder
                {
                    ["Server"] = "127.0.0.1",
                    ["Port"] = ((IPEndPoint)listener.LocalEndpoint).Port,
                    ["User ID"] = "u",
                    ["TLS Mode"] = "Disabled",
                    ["Use OB20"] = kind != "unrequested",
                }.ConnectionString;
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

        [Theory]
        [InlineData(false, "crc")]
        [InlineData(true, "crc")]
        [InlineData(false, "last")]
        [InlineData(true, "last")]
        [InlineData(false, "trailing")]
        [InlineData(true, "trailing")]
        [InlineData(false, "timeout")]
        [InlineData(true, "timeout")]
        [InlineData(false, "cancel")]
        [InlineData(true, "cancel")]
        [InlineData(false, "close")]
        [InlineData(true, "close")]
        public async Task Ob20FaultsBreakSessionWithoutReplayingSql(bool async, string kind)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var caller = new CancellationTokenSource();
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token); var wire = peer.GetStream(); var channel = new PacketChannel(wire);
                await Handshake(channel, wire, "enabled", budget.Token);
                var request = await ReadRequest(wire, channel, true, 2, budget.Token);
                Assert.Equal("update t set x=1", Encoding.UTF8.GetString(request, 1, request.Length - 1)); received.SetResult();
                if (kind == "crc" || kind == "last" || kind == "trailing")
                {
                    var data = Frame(1, Report(false, 1));
                    if (kind == "trailing") data = data.Concat(Frame(2, new byte[] { 0 })).ToArray();
                    var bytes = Ob20Packet.Build(1234, 2, 1, data, flags: kind == "last" ? 0u : 2u);
                    if (kind == "crc") bytes[bytes.Length - 1] ^= 1;
                    await wire.WriteAsync(bytes, budget.Token);
                }
                await ObserveClose(wire, budget.Token);
            });
            using var connection = new OceanBaseConnection($"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode=Disabled;Use OB20=true");
            using var command = connection.CreateCommand(); command.CommandText = "update t set x=1"; command.CommandTimeout = kind == "timeout" ? 1 : 5;
            try
            {
                if (async) await connection.OpenAsync(budget.Token); else connection.Open();
                var executing = async ? command.ExecuteNonQueryAsync(caller.Token) : Task.Run(command.ExecuteNonQuery);
                await received.Task.WaitAsync(budget.Token);
                if (kind == "cancel") { if (async) caller.Cancel(); else command.Cancel(); }
                if (kind == "close") connection.Close();
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => executing.WaitAsync(budget.Token));
                Assert.True(error.IsExecutionOutcomeUnknown);
                if (kind == "timeout") Assert.IsType<TimeoutException>(error.InnerException);
                if (kind == "cancel") Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
                Assert.Equal(kind == "close" ? System.Data.ConnectionState.Closed : System.Data.ConnectionState.Broken, connection.State);
                await serving.WaitAsync(budget.Token);
            }
            finally { budget.Cancel(); listener.Stop(); }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Ob20TrustedServerErrorEndsResponseAndAllowsNextCommand(bool async)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token); var wire = peer.GetStream(); var channel = new PacketChannel(wire);
                await Handshake(channel, wire, "enabled", budget.Token);
                for (uint request = 0; request < 4; request++)
                {
                    var command = await ReadRequest(wire, channel, true, request + 2, budget.Token); Assert.Equal(3, command[0]);
                    var sql = Encoding.UTF8.GetString(command, 1, command.Length - 1);
                    byte[] response;
                    if (request == 1)
                    { Assert.Equal("SELECT SESSIONTIMEZONE FROM DUAL", sql); response = TextResult("+00:00"); }
                    else if (request == 2)
                    {
                        Assert.Equal("SELECT @@NCHARACTER_SET_CONNECTION, @@CHARACTER_SET_RESULTS, @@CHARACTER_SET_CONNECTION FROM DUAL", sql);
                        response = TextResult("utf8mb4", "utf8mb4", "utf8mb4");
                    }
                    else
                    {
                        Assert.Equal("update t set x=1", sql);
                        response = Frame(1, request == 0 ? new byte[] { 255, 122, 4, 35, 52, 50, 83, 48, 50, 101, 114, 114 } : Report(false, 1));
                    }
                    await wire.WriteAsync(Ob20Packet.Build(1234, request + 2, 1, response), budget.Token);
                }
                await ObserveClose(wire, budget.Token);
            });
            using var connection = new OceanBaseConnection($"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode=Disabled;Use OB20=true");
            try
            {
                if (async) await connection.OpenAsync(budget.Token); else connection.Open();
                using var command = connection.CreateCommand(); command.CommandText = "update t set x=1";
                var error = await Assert.ThrowsAsync<OceanBaseException>(async () =>
                { if (async) await command.ExecuteNonQueryAsync(budget.Token); else command.ExecuteNonQuery(); });
                Assert.False(error.IsExecutionOutcomeUnknown); Assert.Equal("42S02", error.SqlState);
                Assert.Equal(System.Data.ConnectionState.Open, connection.State);
                Assert.Equal(1, async ? await command.ExecuteNonQueryAsync(budget.Token) : command.ExecuteNonQuery());
                connection.Close(); await serving.WaitAsync(budget.Token);
            }
            finally { budget.Cancel(); listener.Stop(); }
        }

        [Theory]
        [InlineData(false, "success")]
        [InlineData(true, "success")]
        [InlineData(false, "crc")]
        [InlineData(true, "crc")]
        [InlineData(false, "last")]
        [InlineData(true, "last")]
        [InlineData(false, "cancel")]
        [InlineData(true, "cancel")]
        public async Task Ob20CursorPagesValidateEachOuterBoundary(bool async, string kind)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var caller = new CancellationTokenSource();
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var column = new byte[] { 3, 100, 101, 102, 0, 0, 0, 1, 110, 1, 110, 12, 45, 0, 255, 0, 0, 0, 8, 0, 0, 0, 0, 0 };
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token); var wire = peer.GetStream(); var channel = new PacketChannel(wire);
                await Handshake(channel, wire, "enabled", budget.Token);
                Assert.Equal(22, (await ReadRequest(wire, channel, true, 2, budget.Token))[0]);
                var prepared = Frame(1, new byte[] { 0, 80, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 })
                    .Concat(Frame(2, column)).Concat(Frame(3, new byte[] { 254, 0, 0, 6, 0 })).ToArray();
                await wire.WriteAsync(Ob20Packet.Build(1234, 2, 1, prepared), budget.Token);
                Assert.Equal(new byte[] { 23, 80, 0, 0, 0, 1, 1, 0, 0, 0 }, await ReadRequest(wire, channel, true, 3, budget.Token));
                var initial = Frame(1, new byte[] { 1 }).Concat(Frame(2, column)).Concat(Frame(3, new byte[] { 254, 0, 0, 70, 0 })).ToArray();
                await wire.WriteAsync(Ob20Packet.Build(1234, 3, 1, initial), budget.Token);
                for (var page = 0; page < 3; page++)
                {
                    Assert.Equal(new byte[] { 28, 80, 0, 0, 0, 1, 0, 0, 0 }, await ReadRequest(wire, channel, true, (uint)(page + 4), budget.Token));
                    if (page == 1 && kind == "cancel") { waiting.SetResult(); break; }
                    var row = new byte[10]; BitConverter.GetBytes((long)(page + 42)).CopyTo(row, 2);
                    var data = Frame(1, new byte[] { 1 }).Concat(Frame(2, column)).Concat(Frame(3, new byte[] { 254, 0, 0, 6, 0 }))
                        .Concat(Frame(4, row)).Concat(Frame(5, new byte[] { 254, 0, 0, page == 2 ? (byte)134 : (byte)70, 0 })).ToArray();
                    var bytes = Ob20Packet.Build(1234, (uint)(page + 4), 1, data, flags: page == 1 && kind == "last" ? 0u : 2u);
                    if (page == 1 && kind == "crc") bytes[bytes.Length - 1] ^= 1;
                    await wire.WriteAsync(bytes, budget.Token);
                    if (page == 1 && kind != "success") break;
                }
                if (kind == "success") Assert.Equal(new byte[] { 25, 80, 0, 0, 0 }, await ReadRequest(wire, channel, true, 7, budget.Token));
                await ObserveClose(wire, budget.Token);
            });
            using var connection = new OceanBaseConnection($"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode=Disabled;Use OB20=true");
            try
            {
                if (async) await connection.OpenAsync(budget.Token); else connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "select n"; ((OceanBaseCommand)command).FetchSize = 1;
                    using var reader = async ? await command.ExecuteReaderAsync(budget.Token) : command.ExecuteReader();
                    Assert.True(async ? await reader.ReadAsync(budget.Token) : reader.Read()); Assert.Equal(42L, reader.GetInt64(0));
                    if (kind == "success")
                    {
                        for (var value = 43; value < 45; value++)
                        { Assert.True(async ? await reader.ReadAsync(budget.Token) : reader.Read()); Assert.Equal(value, reader.GetInt64(0)); }
                        Assert.False(async ? await reader.ReadAsync(budget.Token) : reader.Read());
                        Assert.Equal(System.Data.ConnectionState.Open, connection.State);
                    }
                    else
                    {
                        var reading = async ? reader.ReadAsync(caller.Token) : Task.Run(reader.Read);
                        if (kind == "cancel")
                        {
                            await waiting.Task.WaitAsync(budget.Token); Assert.False(reading.IsCompleted);
                            if (async) caller.Cancel(); else command.Cancel();
                        }
                        var error = await Assert.ThrowsAsync<OceanBaseException>(async () =>
                        {
                            // A missing last marker is detected when the next fetch would begin.
                            if (await reading.WaitAsync(budget.Token))
                                if (async) await reader.ReadAsync(budget.Token); else reader.Read();
                        });
                        Assert.True(error.IsExecutionOutcomeUnknown);
                        if (kind == "cancel")
                        {
                            var canceled = Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
                            if (async) Assert.Equal(caller.Token, canceled.CancellationToken);
                        }
                        Assert.Equal(System.Data.ConnectionState.Broken, connection.State);
                    }
                }
                connection.Close(); await serving.WaitAsync(budget.Token);
            }
            finally { budget.Cancel(); listener.Stop(); }
        }
    }
}
