using System;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests.TestInfrastructure;

internal static class ComplexNationalClientFixture
{
    internal static async Task Verify(Type connectionType, byte[] greeting, int sample, bool asynchronous, bool prepareExecute)
    {
        var (name, text, hex) = ClientCharacterSetFixture.Samples[sample];
        var dbcs = ClientCharacterSetFixture.DbcsFallbackSamples.FirstOrDefault(value => value.Name == name);
        if (dbcs.Name != null) { text = dbcs.Text; hex = dbcs.Hex; }
        text += "\\'  ";
        var bytes = Convert.FromHexString(hex).Concat(new byte[] { 92, 39, 32, 32 }).ToArray();
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var connection = (DbConnection)Activator.CreateInstance(connectionType)!;
        connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode=Disabled;Character Set={name};Use Oracle Prepare Execute={prepareExecute};Connection Timeout=5;Command Timeout=5";
        var serving = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(budget.Token);
            var channel = new PacketChannel(peer.GetStream());
            await channel.WritePacketAsync(greeting, budget.Token); await channel.ReadPacketAsync(budget.Token);
            await channel.WritePacketAsync(ClientCharacterSetFixture.Ok(true), budget.Token);
            await ServerIdentity.ReplyAsync(channel, token: budget.Token);
            await ClientCharacterSetFixture.Initialize(channel, name, true, false, budget.Token);
            async Task<byte[]> Next()
            {
                while (true)
                {
                    var request = await ClientCharacterSetFixture.Read(channel, true, name, false, budget.Token);
                    if (request[0] != 3 || Encoding.ASCII.GetString(request, 1, request.Length - 1) != "SELECT @@character_set_client, @@character_set_connection, @@character_set_results, @@collation_connection FROM DUAL") return request;
                    await ClientCharacterSetFixture.State(channel, name, true, false, budget.Token);
                }
            }
            foreach (byte code in new byte[] { 207, 208 })
            {
                var expected = new byte[] { 0, 1, 160, 0, 0, 0, 0, code, 3, 2, (byte)bytes.Length }.Concat(bytes).Concat(new byte[] { 0 }).ToArray();
                var request = await Next();
                if (prepareExecute)
                {
                    var cursor = new ByteCursor(request); Assert.Equal(0xa1, cursor.ReadUInt8());
                    Assert.Equal(0u, cursor.ReadUInt32()); Assert.Equal(0, cursor.ReadUInt8()); Assert.Equal(1u, cursor.ReadUInt32());
                    Assert.Equal("BEGIN pkg.p(?); END;", Encoding.ASCII.GetString(cursor.ReadLengthEncodedBytes()));
                    Assert.Equal(1u, cursor.ReadUInt32()); Assert.Equal(expected, cursor.ReadBytes(expected.Length));
                    await channel.WritePacketAsync(new byte[] { 0, 42, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, budget.Token);
                    await channel.WritePacketAsync(ClientCharacterSetFixture.Column(), budget.Token);
                    await channel.WritePacketAsync(new byte[] { 254, 0, 0, 6, 0 }, budget.Token);
                }
                else
                {
                    Assert.Equal(22, request[0]); Assert.Equal("INSERT INTO t VALUES(?)", Encoding.ASCII.GetString(request, 1, request.Length - 1));
                    await channel.WritePacketAsync(new byte[] { 0, 42, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0 }, budget.Token);
                    await channel.WritePacketAsync(ClientCharacterSetFixture.Column(), budget.Token);
                    await channel.WritePacketAsync(new byte[] { 254, 0, 0, 6, 0 }, budget.Token);
                    request = await Next(); Assert.Equal(23, request[0]);
                    Assert.Equal(expected, request.AsSpan(10).ToArray());
                }
                await channel.WritePacketAsync(new byte[] { 0, 1, 0, 6, 0, 0, 0 }, budget.Token);
            }
            var after = await Next(); Assert.Equal(3, after[0]); Assert.Equal("SELECT 1", Encoding.ASCII.GetString(after, 1, after.Length - 1));
            await channel.WritePacketAsync(new byte[] { 0, 1, 0, 6, 0, 0, 0 }, budget.Token);
        });
        Exception? failure = null;
        try
        {
            if (asynchronous) await connection.OpenAsync(budget.Token); else connection.Open();
            foreach (var scalar in new[] { "NVARCHAR2", "NCHAR" })
            {
                var factory = connectionType.GetMethod("CreateArrayType", new[] { typeof(string), connectionType.Assembly.GetType("OceanBaseConnector.OceanBaseTransaction")! })!;
                var type = factory.Invoke(connection, new object?[] { scalar, null })!;
                var create = type.GetType().GetMethod("CreateValue")!;
                var value = create.Invoke(type, new object[] { new object?[] { text, null, "" } })!;
                using var command = connection.CreateCommand();
                command.CommandType = prepareExecute ? CommandType.StoredProcedure : CommandType.Text;
                command.CommandText = prepareExecute ? "pkg.p" : "INSERT INTO t VALUES(:p)";
                var parameter = command.CreateParameter(); parameter.ParameterName = "p"; parameter.Value = value; command.Parameters.Add(parameter);
                Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync(budget.Token) : command.ExecuteNonQuery());
                if (name == "gb18030" || name == "gb18030_2022")
                    Assert.IsAssignableFrom<ArgumentException>(Assert.Throws<TargetInvocationException>(() => create.Invoke(type, new object[] { new object?[] { "\ud800" } })).InnerException);
                else
                {
                    parameter.Value = create.Invoke(type, new object[] { new object?[] { "😀" } });
                    await Assert.ThrowsAsync<EncoderFallbackException>(() => asynchronous ? command.ExecuteNonQueryAsync(budget.Token) : Task.FromResult(command.ExecuteNonQuery()));
                }
                Assert.Equal(ConnectionState.Open, connection.State);
            }
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT 1";
                Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync(budget.Token) : command.ExecuteNonQuery());
            }
            connection.Close(); await serving.WaitAsync(budget.Token);
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            connection.Close(); budget.Cancel(); listener.Stop();
            try { await serving.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch when (failure != null) { }
        }
    }
}
