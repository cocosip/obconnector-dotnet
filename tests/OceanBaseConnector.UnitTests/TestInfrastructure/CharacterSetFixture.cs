using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests.TestInfrastructure
{
    internal static class CharacterSetFixture
    {
        internal static readonly (ushort Collation, string Text, string Hex, int Width)[] Samples =
        {
            (11, "ABC\0  ", "414243002020", 1),
            (8, "é€‘Ÿ\0  ", "E980919F002020", 1),
            (28, "中文\0  ", "D6D0CEC4002020", 2),
            (248, "ḿ\ue7c7中文😀\0  ", "A8BC8135F437D6D0CEC49439FC36002020", 4),
            (54, "\ufeff中😀\0  ", "FEFF4E2DD83DDE00000000200020", 4),
            (56, "\ufeff中😀\0  ", "FFFE2D4E3DD800DE000020002000", 4),
            (192, "中é  ", "E4B8ADC3A92020", 3),
            (309, "中é😀  ", "E4B8ADC3A9F09F98802020", 4),
        };

        internal static async Task Verify(Type connectionType, byte[] greeting, Func<PacketChannel, CancellationToken, Task<byte[]>> readCommand,
            int sample, bool oracle, bool asynchronous, bool binary, bool sequential)
        {
            var (collation, part, hex, width) = Samples[sample];
            var expected = string.Concat(Enumerable.Repeat(part, 400));
            var field = Enumerable.Range(0, 400).SelectMany(_ => Convert.FromHexString(hex)).ToArray();
            var eof = new byte[] { 254, 0, 0, oracle ? (byte)6 : (byte)2, 0 };
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            using var connection = (DbConnection)Activator.CreateInstance(connectionType)!;
            connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode=Disabled;Command Timeout=10";
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token);
                var channel = new PacketChannel(peer.GetStream());
                await channel.WritePacketAsync(greeting, budget.Token); await channel.ReadPacketAsync(budget.Token);
                await channel.WritePacketAsync(new byte[] { 0, 0, 0, eof[3], 0, 0, 0 }, budget.Token);
                await ServerIdentity.ReplyAsync(channel, oracle: oracle, token: budget.Token); channel.BeginCommand();
                var request = await readCommand(channel, budget.Token);
                Assert.Equal(binary ? 22 : 3, request[0]);
                Assert.Equal("字符查询", Encoding.UTF8.GetString(request, 1, request.Length - 1));
                if (binary)
                {
                    await channel.WritePacketAsync(new byte[] { 0, 71, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, budget.Token);
                    channel.BeginCommand(); Assert.Equal(23, (await readCommand(channel, budget.Token))[0]);
                }
                await channel.WritePacketAsync(new byte[] { 5 }, budget.Token);
                for (var i = 0; i < 5; i++)
                    await channel.WritePacketAsync(Column(i == 4 ? (byte)9 : (byte)253, i == 4 ? (ushort)63 : collation), budget.Token);
                await channel.WritePacketAsync(eof, budget.Token);
                using var row = new MemoryStream();
                if (binary) row.Write(new byte[] { 0, 32 });
                for (var i = 0; i < 3; i++) WriteField(row, field);
                if (!binary) row.WriteByte(251);
                if (binary) row.Write(new byte[] { 42, 0, 0, 0 }); else row.Write(new byte[] { 2, 52, 50 });
                await channel.WritePacketAsync(row.ToArray(), budget.Token); await channel.WritePacketAsync(eof, budget.Token);
                channel.BeginCommand();
                if (binary) { Assert.Equal(25, (await readCommand(channel, budget.Token))[0]); channel.BeginCommand(); }
                request = await readCommand(channel, budget.Token);
                Assert.Equal(3, request[0]); Assert.Equal("reuse", Encoding.UTF8.GetString(request, 1, request.Length - 1));
                await channel.WritePacketAsync(new byte[] { 0, 1, 0, eof[3], 0, 0, 0 }, budget.Token);
            });
            Exception? failure = null;
            try
            {
                if (asynchronous) await connection.OpenAsync(budget.Token); else connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "字符查询";
                    if (binary) { if (asynchronous) await command.PrepareAsync(budget.Token); else command.Prepare(); }
                    using var reader = asynchronous ? await command.ExecuteReaderAsync(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default, budget.Token)
                        : command.ExecuteReader(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default);
                    Assert.Equal(oracle ? 120 : 120 / width, reader.GetColumnSchema()[0].ColumnSize);
                    Assert.Equal(typeof(string), reader.GetFieldType(0)); Assert.Equal("字符列", reader.GetName(0));
                    Assert.True(asynchronous ? await reader.ReadAsync(budget.Token) : reader.Read());
                    using (var text = reader.GetTextReader(0))
                        Assert.Equal(expected, asynchronous ? await text.ReadToEndAsync() : text.ReadToEnd());
                    var buffer = new char[expected.Length];
                    Assert.Equal(buffer.Length, reader.GetChars(1, 0, buffer, 0, buffer.Length));
                    Assert.Equal(expected, new string(buffer));
                    Assert.Equal(expected, reader.GetString(2));
                    Assert.True(reader.IsDBNull(3)); Assert.Equal(DBNull.Value, reader.GetValue(3));
                    Assert.Equal(42, reader.GetInt32(4));
                    Assert.False(asynchronous ? await reader.ReadAsync(budget.Token) : reader.Read());
                }
                using var reuse = connection.CreateCommand(); reuse.CommandText = "reuse";
                Assert.Equal(1, asynchronous ? await reuse.ExecuteNonQueryAsync(budget.Token) : reuse.ExecuteNonQuery());
            }
            catch (Exception error) { failure = error; throw; }
            finally
            {
                connection.Close();
                if (failure != null) budget.Cancel();
                try { await serving.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch when (failure != null) { }
            }
        }

        private static byte[] Column(byte type, ushort collation)
        {
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
            foreach (var text in new[] { "def", "库", "t", "t", "字符列", "c" }) WriteField(stream, Encoding.UTF8.GetBytes(text));
            writer.Write((byte)12); writer.Write(collation); writer.Write(120U); writer.Write(type);
            writer.Write((ushort)0); writer.Write(new byte[3]); return stream.ToArray();
        }
        private static void WriteField(Stream stream, byte[] bytes)
        {
            if (bytes.Length < 251) stream.WriteByte((byte)bytes.Length);
            else { stream.WriteByte(252); stream.WriteByte((byte)bytes.Length); stream.WriteByte((byte)(bytes.Length >> 8)); }
            stream.Write(bytes);
        }
    }
}
