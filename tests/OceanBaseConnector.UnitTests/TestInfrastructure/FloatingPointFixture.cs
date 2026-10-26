using System;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests.TestInfrastructure
{
    internal static class FloatingPointFixture
    {
        internal static async Task Verify(Type connectionType, byte[] greeting, Func<PacketChannel, CancellationToken, Task<byte[]>> readCommand,
            byte type, bool asynchronous, bool binary, bool sequential)
        {
            var texts = new[] { "Inf", "-Inf", "NaN", "inf", "-inf", "nan", "1.5", "-0" };
            var values = new[] { double.PositiveInfinity, double.NegativeInfinity, double.NaN, double.PositiveInfinity,
                double.NegativeInfinity, double.NaN, 1.5, BitConverter.Int64BitsToDouble(long.MinValue) };
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
                await channel.WritePacketAsync(Column(type), budget.Token); await channel.WritePacketAsync(Column(9), budget.Token);
                var eof = new byte[] { 254, 0, 0, 6, 0 };
                await channel.WritePacketAsync(eof, budget.Token);
                for (var i = 0; i < values.Length; i++)
                {
                    using var row = new MemoryStream();
                    if (binary)
                    {
                        row.Write(new byte[] { 0, 0 });
                        var bytes = type == 4 ? BitConverter.GetBytes((float)values[i]) : BitConverter.GetBytes(values[i]);
                        if (!BitConverter.IsLittleEndian) Array.Reverse(bytes);
                        row.Write(bytes); row.Write(new byte[] { 42, 0, 0, 0 });
                    }
                    else
                    {
                        var bytes = Encoding.ASCII.GetBytes(texts[i]); row.WriteByte((byte)bytes.Length); row.Write(bytes);
                        row.Write(new byte[] { 2, 52, 50 });
                    }
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
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "select floating values";
                    if (binary) { if (asynchronous) await command.PrepareAsync(budget.Token); else command.Prepare(); }
                    using var reader = asynchronous ? await command.ExecuteReaderAsync(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default, budget.Token)
                        : command.ExecuteReader(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default);
                    Assert.Equal(type == 4 ? "BINARY_FLOAT" : "BINARY_DOUBLE", reader.GetDataTypeName(0));
                    Assert.Equal(type == 4 ? typeof(float) : typeof(double), reader.GetFieldType(0));
                    Assert.Equal((int)(type == 4 ? OceanBaseDbType.Single : OceanBaseDbType.Double), reader.GetColumnSchema()[0]["ProviderType"]);
                    foreach (var expected in values)
                    {
                        Assert.True(asynchronous ? await reader.ReadAsync(budget.Token) : reader.Read());
                        var value = reader.GetValue(0);
                        if (type == 4) Assert.IsType<float>(value); else Assert.IsType<double>(value);
                        var actual = Convert.ToDouble(value);
                        if (double.IsNaN(expected)) Assert.True(double.IsNaN(actual)); else Assert.Equal(expected, actual);
                        var typed = type == 4 ? reader.GetFloat(0) : reader.GetDouble(0);
                        if (double.IsNaN(expected)) Assert.True(double.IsNaN(typed)); else Assert.Equal(expected, typed);
                        if (expected == 0) Assert.Equal(BitConverter.DoubleToInt64Bits(expected), BitConverter.DoubleToInt64Bits(actual));
                        Assert.Equal(42, reader.GetInt32(1));
                    }
                    Assert.True(asynchronous ? await reader.ReadAsync(budget.Token) : reader.Read());
                    Assert.True(reader.IsDBNull(0)); Assert.Equal(DBNull.Value, reader.GetValue(0)); Assert.Equal(42, reader.GetInt32(1));
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
            writer.Write((byte)12); writer.Write((ushort)63); writer.Write(32U); writer.Write(type); writer.Write((ushort)0); writer.Write(new byte[3]);
            return stream.ToArray();
        }
    }
}
