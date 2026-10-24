using System;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        private static string LobConfiguration(int port) => Configuration(port) + ";Use Oracle Prepare Execute=true;Use Lob Locators=true";
        private static byte[] InlineLob(byte[] data)
        {
            var result = new byte[40 + data.Length];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(result, 0x4c4f4221);
            result[4] = 1; result[28] = 1;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(36), (uint)data.Length);
            data.CopyTo(result, 40); return result;
        }
        private static byte[] ExternalLob(int length)
        {
            var result = new byte[92];
            new byte[] { 1, 132, 1, 0, 2, 1, 0, 0, 1, 129, 0, 0 }.CopyTo(result, 0);
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(28), length); return result;
        }
        private static async Task LobQueryReply(PacketChannel channel, byte type, byte[] locator)
        {
            await Query(channel, "select lob_value");
            await channel.WritePacketAsync(new byte[] { 1 }); await channel.WritePacketAsync(Column("lob", type));
            await channel.WritePacketAsync(Eof()); await channel.WritePacketAsync(WithLength(locator));
            await channel.WritePacketAsync(Eof()); channel.BeginCommand();
        }

        [Theory]
        [InlineData(false, false, false)] [InlineData(true, false, false)]
        [InlineData(false, true, false)] [InlineData(true, true, false)]
        [InlineData(false, false, true)] [InlineData(true, false, true)]
        [InlineData(false, true, true)] [InlineData(true, true, true)]
        public async Task LobInlineReaderReturnsBoundHandleAndStreamsWithoutLocatorBytes(bool async, bool character, bool sequential)
        {
            var data = Encoding.UTF8.GetBytes("患者😀abc");
            var server = Server(async channel =>
            {
                await LobQueryReply(channel, character ? (byte)211 : (byte)210, InlineLob(data));
                await Query(channel, "after"); await channel.WritePacketAsync(Ok(1));
            }, lobLocators: true);
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "select lob_value";
                using var reader = async ? await command.ExecuteReaderAsync(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default)
                    : command.ExecuteReader(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default);
                Assert.Equal(typeof(OceanBaseLob), reader.GetFieldType(0));
                Assert.Equal(character ? "CLOB" : "BLOB", reader.GetDataTypeName(0));
                Assert.True(async ? await reader.ReadAsync() : reader.Read());
                using var lob = reader.GetFieldValue<OceanBaseLob>(0);
                Assert.Equal(data.Length, lob.ByteLength); Assert.Equal(character, lob.IsCharacter);
                Assert.Throws<InvalidOperationException>(() => { if (character) lob.OpenTextReader(); else lob.OpenRead(); });
                Assert.False(async ? await reader.ReadAsync() : reader.Read());
                if (character)
                {
                    using var text = lob.OpenTextReader();
                    Assert.Equal("患者😀abc", async ? await text.ReadToEndAsync() : text.ReadToEnd());
                }
                else
                {
                    using var stream = lob.OpenRead(); using var target = new MemoryStream();
                    if (async) await stream.CopyToAsync(target); else stream.CopyTo(target);
                    Assert.Equal(data, target.ToArray()); Assert.False(stream.CanSeek);
                }
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
        public async Task LobExternalReadUsesBoundedReadCallsWithByteOrScalarOffsets(bool async, bool character)
        {
            var text = string.Concat(Enumerable.Repeat("患者😀a", 9000)); var data = character ? Encoding.UTF8.GetBytes(text) : Enumerable.Range(0, 80000).Select(i => (byte)i).ToArray();
            var locator = ExternalLob(data.Length);
            var server = Server(async channel =>
            {
                await LobQueryReply(channel, character ? (byte)211 : (byte)210, locator);

                long offset = 1; var sent = 0;
                while (sent < data.Length)
                {
                    var packet = await StatementCommand(channel, 0xa1);
                    var request = new ByteCursor(packet); request.Skip(10);
                    Assert.Equal("BEGIN DBMS_LOB.READ(?, ?, ?, ?); END;", ResultColumn.Utf8.GetString(request.ReadLengthEncodedBytes()));
                    Assert.Equal(4u, request.ReadUInt32()); Assert.Equal(8, request.ReadUInt8()); Assert.Equal(1, request.ReadUInt8());
                    Assert.Equal(character ? (ushort)211 : (ushort)210, request.ReadUInt16()); Assert.Equal((ushort)3, request.ReadUInt16());
                    Assert.Equal((ushort)8, request.ReadUInt16()); Assert.Equal((ushort)6, request.ReadUInt16());
                    Assert.Equal(locator, request.ReadLengthEncodedBytes());
                    var amount = (int)request.ReadUInt32(); Assert.Equal((ulong)offset, request.ReadUInt64());
                    Assert.InRange(amount, 1, character ? 8191 : 32767);
                    var count = Math.Min(amount, character ? 4 * 9000 - (int)(offset - 1) : data.Length - sent);
                    byte[] chunk;
                    if (character)
                    {
                        var scalars = text.EnumerateRunes().Skip((int)offset - 1).Take(count);
                        chunk = Encoding.UTF8.GetBytes(string.Concat(scalars.Select(r => r.ToString())));
                    }
                    else chunk = data.AsSpan(sent, count).ToArray();
                    sent += chunk.Length; offset += count;
                    await LobReadReply(channel, count, chunk, character);
                    await StatementCommand(channel, 25); channel.BeginCommand();
                }
                await Query(channel, "after"); await channel.WritePacketAsync(Ok(1));
            }, lobLocators: true, serverVersion: "4.3.5.0");
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "select lob_value";
                using var lob = (OceanBaseLob)(async ? await command.ExecuteScalarAsync() : command.ExecuteScalar())!;
                if (character)
                {
                    using var source = lob.OpenTextReader();
                    Assert.Equal(text, async ? await source.ReadToEndAsync() : source.ReadToEnd());
                }
                else
                {
                    using var source = lob.OpenRead(); using var target = new MemoryStream();
                    if (async) await source.CopyToAsync(target); else source.CopyTo(target);
                    Assert.Equal(data, target.ToArray());
                }
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, after.ExecuteNonQuery());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)] [InlineData(true)]
        public async Task UnreleasedLobDiscardsPooledSession(bool async)
        {
            var oldServer = Server(async channel =>
            {
                await LobQueryReply(channel, 210, InlineLob(new byte[] { 1, 2, 3 }));
                Assert.Equal(0, await channelStreamRead(channel));
            }, lobLocators: true);
            var newServer = Server(async channel => { await Query(channel, "after"); await channel.WritePacketAsync(Ok(1)); }, lobLocators: true);
            using var connection = new OceanBaseConnection(LobConfiguration(oldServer.Port) + ";Pooling=true");
            OceanBaseLob? lob = null;
            try
            {
                if (async) await connection.OpenAsync(); else connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "select lob_value";
                    lob = (OceanBaseLob)(async ? await command.ExecuteScalarAsync() : command.ExecuteScalar())!;
                }

                connection.Close();
                await oldServer.Completion.WaitAsync(TimeSpan.FromSeconds(10));
                connection.ConnectionString = LobConfiguration(newServer.Port) + ";Pooling=true";
                if (async) await connection.OpenAsync(); else connection.Open();
                Assert.Throws<InvalidOperationException>(() => lob!.OpenRead());
                using var after = connection.CreateCommand(); after.CommandText = "after";
                Assert.Equal(1, async ? await after.ExecuteNonQueryAsync() : after.ExecuteNonQuery());
            }
            finally
            {
                lob?.Dispose(); connection.Close(); oldServer.Stop(); newServer.Stop();
            }
            await newServer.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        private static async Task LobReadReply(PacketChannel channel, int amount, byte[] chunk, bool character, string? number = null, byte precision = 38)
        {
            await channel.WritePacketAsync(OracleExecuteHeader(4, 2, 4));
            for (var i = 0; i < 4; i++) await channel.WritePacketAsync(Column("p" + i, 253));
            await channel.WritePacketAsync(Eof());
            var amountColumn = Column("amount", number == null ? (byte)8 : (byte)246); amountColumn[amountColumn.Length - 2] = precision;
            await channel.WritePacketAsync(amountColumn);
            await channel.WritePacketAsync(Column("buffer", character ? (byte)253 : (byte)203, character ? (ushort)45 : (ushort)63));
            await channel.WritePacketAsync(Eof());
            var row = new MemoryStream(); row.Write(new byte[] { 0, 0 });
            row.Write(number == null ? BitConverter.GetBytes((long)amount) : WithLength(Encoding.ASCII.GetBytes(number))); row.Write(WithLength(chunk));
            await channel.WritePacketAsync(row.ToArray()); await channel.WritePacketAsync(Eof()); await channel.WritePacketAsync(Ok());
        }
    }
}
