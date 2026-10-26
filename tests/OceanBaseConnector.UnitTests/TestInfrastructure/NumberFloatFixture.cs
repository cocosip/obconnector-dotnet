using System;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests.TestInfrastructure
{
    internal static class NumberFloatFixture
    {
        internal static async Task Verify(Type connectionType, byte[] greeting, Func<PacketChannel, CancellationToken, Task<byte[]>> readCommand,
            bool asynchronous, bool binary, bool sequential)
        {
            var texts = new[] { "7.2500", "12345678901234567890123456789012345678", "1E-130", "1E125" };
            var coefficients = new[] { new BigInteger(72500), BigInteger.Parse(texts[1]), BigInteger.One, BigInteger.One };
            var scales = new[] { 4, 0, 130, -125 };
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            using var connection = (DbConnection)Activator.CreateInstance(connectionType)!;
            connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode=Disabled;Command Timeout=10";
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token);
                var channel = new PacketChannel(peer.GetStream());
                await channel.WritePacketAsync(greeting, budget.Token); await channel.ReadPacketAsync(budget.Token);
                await channel.WritePacketAsync(new byte[] { 0, 0, 0, 6, 0, 0, 0 }, budget.Token);
                await ServerIdentity.ReplyAsync(channel, token: budget.Token); channel.BeginCommand();
                var request = await readCommand(channel, budget.Token);
                Assert.Equal(binary ? 22 : 3, request[0]);
                if (binary)
                {
                    await channel.WritePacketAsync(new byte[] { 0, 71, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, budget.Token);
                    channel.BeginCommand(); Assert.Equal(23, (await readCommand(channel, budget.Token))[0]);
                }
                await channel.WritePacketAsync(new byte[] { 2 }, budget.Token);
                await channel.WritePacketAsync(Column(206), budget.Token); await channel.WritePacketAsync(Column(9), budget.Token);
                var eof = new byte[] { 254, 0, 0, 6, 0 };
                await channel.WritePacketAsync(eof, budget.Token);
                foreach (var text in texts)
                {
                    using var row = new MemoryStream();
                    if (binary) row.Write(new byte[] { 0, 0 });
                    var bytes = Encoding.ASCII.GetBytes(text); row.WriteByte((byte)bytes.Length); row.Write(bytes);
                    row.Write(binary ? new byte[] { 42, 0, 0, 0 } : new byte[] { 2, 52, 50 });
                    await channel.WritePacketAsync(row.ToArray(), budget.Token);
                }
                await channel.WritePacketAsync(binary ? new byte[] { 0, 4, 42, 0, 0, 0 } : new byte[] { 251, 2, 52, 50 }, budget.Token);
                await channel.WritePacketAsync(eof, budget.Token); channel.BeginCommand();
                if (binary) { Assert.Equal(25, (await readCommand(channel, budget.Token))[0]); channel.BeginCommand(); }
                request = await readCommand(channel, budget.Token);
                Assert.Equal(3, request[0]); Assert.Equal("reuse", Encoding.UTF8.GetString(request, 1, request.Length - 1));
                await channel.WritePacketAsync(new byte[] { 0, 1, 0, 6, 0, 0, 0 }, budget.Token);
            });
            Exception? failure = null;
            try
            {
                if (asynchronous) await connection.OpenAsync(budget.Token); else connection.Open();
                using (var types = connection.GetSchema("DataTypes"))
                {
                    var floating = Assert.Single(types.Rows.Cast<DataRow>(), row => (string)row["TypeName"] == "FLOAT");
                    Assert.Equal("FLOAT({0})", floating["CreateFormat"]);
                    Assert.Equal(1, floating["MinimumPrecision"]); Assert.Equal(126, floating["MaximumPrecision"]);
                    Assert.Equal(DBNull.Value, floating["MinimumScale"]); Assert.Equal(DBNull.Value, floating["MaximumScale"]);
                    Assert.Equal("OceanBaseConnector.OceanBaseDecimal", floating["DataType"]);
                }
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "select number float values";
                    if (binary) { if (asynchronous) await command.PrepareAsync(budget.Token); else command.Prepare(); }
                    using var reader = asynchronous ? await command.ExecuteReaderAsync(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default, budget.Token)
                        : command.ExecuteReader(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default);
                    var schema = reader.GetColumnSchema()[0];
                    Assert.Equal("FLOAT", reader.GetDataTypeName(0)); Assert.Equal("FLOAT", schema.DataTypeName);
                    Assert.Equal("OceanBaseConnector.OceanBaseDecimal", reader.GetFieldType(0).FullName);
                    Assert.Equal(reader.GetFieldType(0), reader.GetProviderSpecificFieldType(0));
                    Assert.Equal(126, schema.NumericPrecision); Assert.Null(schema.NumericScale);
                    Assert.Equal((int)OceanBaseDbType.Decimal, schema["ProviderType"]);
                    for (var i = 0; i < texts.Length; i++)
                    {
                        Assert.True(asynchronous ? await reader.ReadAsync(budget.Token) : reader.Read());
                        var value = reader.GetValue(0); var type = value.GetType();
                        Assert.Equal(reader.GetFieldType(0), type);
                        Assert.Equal(coefficients[i], type.GetProperty("Coefficient")!.GetValue(value));
                        Assert.Equal(scales[i], type.GetProperty("Scale")!.GetValue(value));
                        Assert.Equal(value, reader.GetProviderSpecificValue(0));
                        if (i == 0)
                        {
                            Assert.Equal(7.25m, reader.GetDecimal(0));
                            Assert.Equal(7.25m, reader.GetFieldValue<decimal>(0));
                        }
                        else Assert.Throws<OverflowException>(() => reader.GetDecimal(0));
                        Assert.Equal(42, reader.GetInt32(1));
                    }
                    Assert.True(asynchronous ? await reader.ReadAsync(budget.Token) : reader.Read());
                    Assert.True(reader.IsDBNull(0)); Assert.Equal(DBNull.Value, reader.GetValue(0));
                    Assert.Equal(DBNull.Value, reader.GetProviderSpecificValue(0)); Assert.Equal(42, reader.GetInt32(1));
                    Assert.False(asynchronous ? await reader.ReadAsync(budget.Token) : reader.Read());
                }
                using var reuse = connection.CreateCommand(); reuse.CommandText = "reuse";
                Assert.Equal(1, asynchronous ? await reuse.ExecuteNonQueryAsync(budget.Token) : reuse.ExecuteNonQuery());
            }
            catch (Exception error) { failure = error; throw; }
            finally
            {
                connection.Close(); if (failure != null) budget.Cancel();
                try { await serving.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch when (failure != null) { }
            }
        }

        private static byte[] Column(byte type)
        {
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
            foreach (var name in new[] { "def", "", "", "", "v", "v" })
            { var bytes = Encoding.UTF8.GetBytes(name); writer.Write((byte)bytes.Length); writer.Write(bytes); }
            writer.Write((byte)12); writer.Write((ushort)63); writer.Write(126U); writer.Write(type); writer.Write((ushort)0);
            writer.Write(type == 206 ? new byte[] { 171, 126, 0 } : new byte[3]);
            return stream.ToArray();
        }
    }
}
