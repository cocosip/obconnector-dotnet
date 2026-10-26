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
            (217, "ḿ\ue7c7龴\ue81e\ufe10\ue78d中😀\0  ", "A8BC8135F437FE5982359037A6D984318236D6D09439FC36002020", 4),
            (3, "\u0080\u009f¤ŒŸœÿ\0  ", "809FA8D7DDF7FD002020", 1),
            (18, "\u0080\u009fกฺ฿๛\0  ", "809FA1DADFFB002020", 1),
            (13, "日本語ｶﾅ\\〜‖−¢£¬\0  ", "93FA967B8CEAB6C5815F81608161817C8191819281CA002020", 2),
            (1, "中•∼¥ヾぁЁ①\0  ", "A4A4A145A1E3A244C6A1C6A5C7B3C7E9002020", 2),
        };

        internal static async Task Verify(Type connectionType, byte[] greeting, Func<PacketChannel, CancellationToken, Task<byte[]>> readCommand,
            int sample, bool oracle, bool asynchronous, bool binary, bool sequential, byte type = 253, bool raw = false)
        {
            var (collation, part, hex, width) = Samples[sample];
            var extended = type >= 247 && type <= 251 || type == 255 || type == 209;
            var repetitions = type == 249 ? 10 : 400;
            var expected = string.Concat(Enumerable.Repeat(part, repetitions));
            var field = Enumerable.Range(0, repetitions).SelectMany(_ => Convert.FromHexString(hex)).ToArray();
            if (raw) collation = 63;
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
                    await channel.WritePacketAsync(Column(i == 4 ? (byte)9 : type, i == 4 ? (ushort)63 : collation), budget.Token);
                await channel.WritePacketAsync(eof, budget.Token);
                using var row = new MemoryStream();
                if (binary) row.Write(new byte[] { 0, 32 });
                for (var i = 0; i < 3; i++) WriteField(row, field);
                if (!binary) row.WriteByte(251);
                if (binary) row.Write(new byte[] { 42, 0, 0, 0 }); else row.Write(new byte[] { 2, 52, 50 });
                await channel.WritePacketAsync(row.ToArray(), budget.Token);
                if (extended)
                    await channel.WritePacketAsync(binary ? new byte[] { 0, 32, 0, 0, 0, 42, 0, 0, 0 } : new byte[] { 0, 0, 0, 251, 2, 52, 50 }, budget.Token);
                await channel.WritePacketAsync(eof, budget.Token);
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
                    Assert.Equal(oracle || raw ? 120 : 120 / width, reader.GetColumnSchema()[0].ColumnSize);
                    Assert.Equal(raw ? typeof(byte[]) : typeof(string), reader.GetFieldType(0)); Assert.Equal("字符列", reader.GetName(0));
                    if (extended)
                    {
                        Assert.Equal(type == 209 ? "ROWID" : type == 247 ? "ENUM" : type == 248 ? "SET" : type == 255 ? "GEOMETRY"
                            : (type == 249 ? "TINY" : type == 250 ? "MEDIUM" : "LONG") + (raw ? "BLOB" : "TEXT"), reader.GetDataTypeName(0));
                        Assert.Equal(type >= 249 && type <= 251, reader.GetColumnSchema()[0].IsLong);
                        Assert.Equal((int)(raw ? OceanBaseDbType.Binary : OceanBaseDbType.String), reader.GetColumnSchema()[0]["ProviderType"]);
                    }
                    Assert.True(asynchronous ? await reader.ReadAsync(budget.Token) : reader.Read());
                    if (raw)
                    {
                        using (var stream = reader.GetStream(0))
                        using (var copy = new MemoryStream())
                        {
                            if (asynchronous) await stream.CopyToAsync(copy, budget.Token); else stream.CopyTo(copy);
                            Assert.Equal(field, copy.ToArray());
                        }
                        var buffer = new byte[field.Length];
                        Assert.Equal(buffer.Length, reader.GetBytes(1, 0, buffer, 0, buffer.Length));
                        Assert.Equal(field, buffer); Assert.Equal(field, reader.GetValue(2));
                    }
                    else
                    {
                        using (var text = reader.GetTextReader(0))
                            Assert.Equal(expected, asynchronous ? await text.ReadToEndAsync() : text.ReadToEnd());
                        var buffer = new char[expected.Length];
                        Assert.Equal(buffer.Length, reader.GetChars(1, 0, buffer, 0, buffer.Length));
                        Assert.Equal(expected, new string(buffer));
                        Assert.Equal(expected, reader.GetString(2));
                    }
                    Assert.True(reader.IsDBNull(3)); Assert.Equal(DBNull.Value, reader.GetValue(3));
                    Assert.Equal(42, reader.GetInt32(4));
                    if (extended)
                    {
                        Assert.True(asynchronous ? await reader.ReadAsync(budget.Token) : reader.Read());
                        for (var i = 0; i < 3; i++)
                            Assert.Equal(raw ? (object)Array.Empty<byte>() : "", reader.GetValue(i));
                        Assert.True(reader.IsDBNull(3)); Assert.Equal(42, reader.GetInt32(4));
                    }
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
