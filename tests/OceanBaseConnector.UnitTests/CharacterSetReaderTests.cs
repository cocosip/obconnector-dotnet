using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        public static IEnumerable<object[]> CharacterSetReaderCases()
        {
            for (var sample = 0; sample < CharacterSetFixture.Samples.Length; sample++)
                foreach (var oracle in new[] { false, true })
                    foreach (var asynchronous in new[] { false, true })
                        foreach (var binary in new[] { false, true })
                            foreach (var sequential in new[] { false, true })
                                yield return new object[] { sample, oracle, asynchronous, binary, sequential };
        }

        [Theory]
        [MemberData(nameof(CharacterSetReaderCases))]
        public Task CharacterSetPublicReadersPreserveValuesNullsAndSessionReuse(int sample, bool oracle, bool asynchronous, bool binary, bool sequential)
            => CharacterSetFixture.Verify(typeof(OceanBaseConnection), ProtocolFixture.Load("initial-handshake-oceanbase").Payload,
                (channel, _) => ReadClientCommand(channel), sample, oracle, asynchronous, binary, sequential);

        [Theory]
        [InlineData(11, "80")]
        [InlineData(3, "41A4")]
        [InlineData(18, "A1DB")]
        [InlineData(13, "815F81")]
        [InlineData(88, "8740")]
        [InlineData(1, "A4A4A1")]
        [InlineData(84, "A15A")]
        [InlineData(28, "80")]
        [InlineData(28, "FF")]
        [InlineData(28, "81")]
        [InlineData(248, "8431A530")]
        [InlineData(248, "A8BC8135F4")]
        [InlineData(216, "FE59823590")]
        [InlineData(222, "E3329A36")]
        [InlineData(54, "D800")]
        [InlineData(56, "00D8")]
        public async Task CharacterSetSequentialTextRejectsInvalidOrTruncatedBytes(ushort collation, string hex)
        {
            var server = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)));
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            Exception? failure = null;
            try
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand(); command.CommandText = "malformed";
                using var bytes = new MemoryStream();
                var packets = new[] { new byte[] { 1 }, Column("text", 253, collation), Eof(), WithLength(Convert.FromHexString(hex)), Eof() };
                for (var i = 0; i < packets.Length; i++) bytes.Write(ResponseFrame(packets[i], (byte)(i + 1)));
                InstallFaultStream(connection, new ProtocolFaultStream(bytes.ToArray()));
                using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess);
                Assert.True(await reader.ReadAsync());
                using var text = reader.GetTextReader(0);
                await Assert.ThrowsAsync<DecoderFallbackException>(() => text.ReadToEndAsync());
                Assert.False(await reader.ReadAsync());
                Assert.Equal(ConnectionState.Open, connection.State);
            }
            catch (Exception error) { failure = error; throw; }
            finally
            {
                connection.Close(); server.Stop();
                try { await server.Completion.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch when (failure != null) { }
            }
        }
    }
}
