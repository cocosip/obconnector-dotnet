using System;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Loader;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.ConsumerTests;

public sealed partial class Ob20ConsumerTests
{
    [Theory]
    [MemberData(nameof(ComplexDictionaryAssets))]
    public async Task EveryAssetBindsUrowidThroughPublicApis(string target, bool asynchronous, bool ob20)
    {
        const string shortValue = "AAAVhPAAEAAAACXAAA";
        var longValue = new string('A', 300);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var serving = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(budget.Token);
            var stream = peer.GetStream(); var channel = new PacketChannel(stream);
            await Handshake(channel, stream, ob20 ? "enabled" : "unrequested", budget.Token, deprecated: true);
            var prepare = await ReadRequest(stream, channel, ob20, 2, budget.Token);
            Assert.Equal(22, prepare[0]); Assert.Equal("INSERT INTO t VALUES (?)", Encoding.UTF8.GetString(prepare, 1, prepare.Length - 1));
            var response = Frame(1, new byte[] { 0, 91, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0 })
                .Concat(Frame(2, new byte[] { 3, 100, 101, 102, 0, 0, 0, 1, 112, 1, 112, 12, 45, 0, 255, 15, 0, 0, 209, 0, 0, 0, 0, 0 })).ToArray();
            await stream.WriteAsync(ob20 ? Ob20Packet.Build(1234, 2, 1, response) : response, budget.Token);
            if (!ob20) channel.BeginCommand();
            for (uint i = 0; i < 4; i++)
            {
                var request = await ReadRequest(stream, channel, ob20, i + 3, budget.Token);
                var cursor = new ByteCursor(request);
                Assert.Equal(23, cursor.ReadUInt8()); Assert.Equal(91u, cursor.ReadUInt32());
                Assert.Equal(0, cursor.ReadUInt8()); Assert.Equal(1u, cursor.ReadUInt32());
                var isNull = i == 0 || i == 2;
                Assert.Equal(isNull ? 1 : 0, cursor.ReadUInt8()); Assert.Equal(1, cursor.ReadUInt8());
                Assert.Equal(isNull ? 6 : 209, cursor.ReadUInt16());
                if (!isNull) Assert.Equal(i == 1 ? shortValue : longValue, Encoding.UTF8.GetString(cursor.ReadLengthEncodedBytes()));
                Assert.False(cursor.HasRemaining);
                response = Frame(1, Report(false, 1));
                await stream.WriteAsync(ob20 ? Ob20Packet.Build(1234, i + 3, 1, response) : response, budget.Token);
                if (!ob20) channel.BeginCommand();
            }
            Assert.Equal(new byte[] { 25, 91, 0, 0, 0 }, await ReadRequest(stream, channel, ob20, 7, budget.Token));
            await ObserveClose(stream, budget.Token);
        });
        var context = new AssemblyLoadContext("urowid-" + target, isCollectible: true);
        try
        {
            var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
            using var connection = (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!)!;
            connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode=Disabled;Use OB20={ob20};Pooling=false";
            if (asynchronous) await connection.OpenAsync(budget.Token); else connection.Open();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "INSERT INTO t VALUES (:p)";
                var parameter = command.CreateParameter(); parameter.ParameterName = "p"; parameter.Size = 1;
                parameter.GetType().GetProperty("OceanBaseDbType")!.SetValue(parameter, Enum.Parse(assembly.GetType("OceanBaseConnector.OceanBaseDbType")!, "Urowid"));
                Assert.Equal(DbType.String, parameter.DbType); command.Parameters.Add(parameter);
                if (asynchronous) await command.PrepareAsync(budget.Token); else command.Prepare();
                foreach (var value in new object?[] { null, shortValue, DBNull.Value, longValue })
                {
                    parameter.Value = value!;
                    Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync(budget.Token) : command.ExecuteNonQuery());
                }
            }
            connection.Close(); await serving.WaitAsync(budget.Token);
        }
        finally
        {
            budget.Cancel(); listener.Stop(); context.Unload();
            try { await serving.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) when (budget.IsCancellationRequested) { }
        }
    }
}
