using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        [Theory]
        [InlineData("ascii", "é", false)]
        [InlineData("ascii", "é", true)]
        [InlineData("latin1", "中", false)]
        [InlineData("latin1", "中", true)]
        [InlineData("gbk", "€", false)]
        [InlineData("gbk", "€", true)]
        [InlineData("utf8mb3", "😀", false)]
        [InlineData("utf8mb3", "😀", true)]
        [InlineData("sjis", "😀", false)]
        [InlineData("sjis", "😀", true)]
        [InlineData("hkscs", "\u2593", false)]
        [InlineData("hkscs", "\u2593", true)]
        public async Task ClobRejectsUnrepresentableSuffixBeforeWritingPrefixAndPreservesHandle(string charset, string suffix, bool asynchronous)
        {
            var writes = 0;
            byte[]? buffer = null;
            uint amount = 0;
            ulong offset = 0;
            var server = Server(async channel =>
            {
                await ClientCharacterSetFixture.Initialize(channel, charset, true, false, CancellationToken.None);
                channel.BeginCommand();
                await LobQueryReply(channel, 211, InlineLob(Encoding.UTF8.GetBytes("old")));
                var request = new ByteCursor(await ClientCharacterSetFixture.Read(channel, true, charset, false, CancellationToken.None));
                Assert.Equal(0xa1, request.ReadUInt8()); request.Skip(9);
                Assert.Equal("BEGIN DBMS_LOB.WRITE(?, ?, ?, ?); END;", Encoding.ASCII.GetString(request.ReadLengthEncodedBytes()));
                Assert.Equal(4u, request.ReadUInt32()); request.Skip(10);
                Assert.Equal(InlineLob(Encoding.UTF8.GetBytes("old")), request.ReadLengthEncodedBytes());
                amount = request.ReadUInt32(); offset = request.ReadUInt64(); buffer = request.ReadLengthEncodedBytes();
                Interlocked.Increment(ref writes);
                await LobMutationReply(channel, 211, InlineLob(Encoding.UTF8.GetBytes("OK")));
                await StatementCommand(channel, 25);
            }, lobLocators: true, serverVersion: "4.3.5.0");
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port) + ";Character Set=" + charset);
            try
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand(); command.CommandText = "select lob_value";
                using var lob = (OceanBaseLob)(await command.ExecuteScalarAsync())!;
                var text = new string('x', 8191) + suffix;
                await Assert.ThrowsAsync<EncoderFallbackException>(() => asynchronous ? lob.WriteTextAsync(0, text) : Task.Run(() => lob.WriteText(0, text)));
                Assert.Equal(0, Volatile.Read(ref writes)); Assert.Equal(3, lob.ByteLength);
                if (asynchronous) await lob.WriteTextAsync(0, "OK"); else lob.WriteText(0, "OK");
                Assert.Equal(1, Volatile.Read(ref writes)); Assert.Equal(2u, amount); Assert.Equal(1ul, offset);
                Assert.Equal(new byte[] { 79, 75 }, buffer); Assert.Equal(2, lob.ByteLength);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData("utf8mb4", "F09F9880", "E4B8AD", false)]
        [InlineData("utf8mb4", "F09F9880", "E4B8AD", true)]
        [InlineData("gb18030_2022", "9439FC36", "D6D0", false)]
        [InlineData("gb18030_2022", "9439FC36", "D6D0", true)]
        public async Task ClobPrevalidationAndWritesKeepSupplementaryScalarsWholeAtChunkBoundary(string charset, string supplementaryHex, string lastHex, bool asynchronous)
        {
            var server = Server(async channel =>
            {
                if (charset != "utf8mb4")
                {
                    await ClientCharacterSetFixture.Initialize(channel, charset, true, false, CancellationToken.None);
                    channel.BeginCommand();
                }
                var locator = InlineLob(Encoding.UTF8.GetBytes("old"));
                await LobQueryReply(channel, 211, locator);
                for (var chunk = 0; chunk < 2; chunk++)
                {
                    var packet = await ClientCharacterSetFixture.Read(channel, true, charset, false, CancellationToken.None);
                    if (packet[0] == 3)
                    {
                        Assert.Equal(ClientCharsetStateSql, Encoding.ASCII.GetString(packet, 1, packet.Length - 1));
                        await ClientCharacterSetFixture.State(channel, charset, true, false, CancellationToken.None);
                        packet = await ClientCharacterSetFixture.Read(channel, true, charset, false, CancellationToken.None);
                    }
                    var request = new ByteCursor(packet); Assert.Equal(0xa1, request.ReadUInt8()); request.Skip(9);
                    Assert.Equal("BEGIN DBMS_LOB.WRITE(?, ?, ?, ?); END;", Encoding.ASCII.GetString(request.ReadLengthEncodedBytes()));
                    Assert.Equal(4u, request.ReadUInt32()); request.Skip(10);
                    Assert.Equal(locator, request.ReadLengthEncodedBytes());
                    Assert.Equal(chunk == 0 ? 8191u : 1u, request.ReadUInt32());
                    Assert.Equal(chunk == 0 ? 3ul : 8194ul, request.ReadUInt64());
                    var expected = Convert.FromHexString(chunk == 0 ? supplementaryHex : lastHex);
                    if (chunk == 0)
                    {
                        var full = new byte[8190 + expected.Length]; Array.Fill(full, (byte)'a', 0, 8190);
                        expected.CopyTo(full, 8190); expected = full;
                    }
                    Assert.Equal(expected, request.ReadLengthEncodedBytes());
                    locator = InlineLob(new byte[] { (byte)(chunk + 1) });
                    await LobMutationReply(channel, 211, locator);
                }
                await StatementCommand(channel, 25);
            }, lobLocators: true, serverVersion: "4.3.5.0");
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port) + ";Character Set=" + charset);
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "select lob_value";
                using var lob = (OceanBaseLob)(await command.ExecuteScalarAsync())!;
                var text = new string('a', 8190) + "😀中";
                if (asynchronous) await lob.WriteTextAsync(2, text); else lob.WriteText(2, text);
                Assert.Equal(1, lob.ByteLength);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
