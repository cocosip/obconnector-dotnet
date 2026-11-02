using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        public static IEnumerable<object[]> BatchComplexLobResultCases()
        {
            foreach (var asynchronous in new[] { false, true })
                foreach (var collection in new[] { false, true })
                    foreach (var character in new[] { false, true })
                        for (var api = 0; api < 3; api++) yield return new object[] { asynchronous, collection, character, api };
        }

        private static IEnumerable<OceanBaseLob> ComplexResultLobs(OceanBaseComplexValue value)
        {
            foreach (var member in value.GetValues())
            {
                if (member is OceanBaseLob lob) yield return lob;
                else if (member is OceanBaseComplexValue nested)
                    foreach (var child in ComplexResultLobs(nested)) yield return child;
            }
        }

        private static async Task PrepareComplexLobResult(PacketChannel channel, byte id)
        {
            await StatementCommand(channel, 22);
            await channel.WritePacketAsync(new byte[] { 0, id, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
            await channel.WritePacketAsync(ComplexColumn()); await channel.WritePacketAsync(Eof());
        }

        private static async Task ReplyComplexLobResult(PacketChannel channel, byte[]? value)
        {
            channel.BeginResponse(); await channel.WritePacketAsync(new byte[] { 1 });
            await channel.WritePacketAsync(ComplexColumn()); await channel.WritePacketAsync(Eof());
            await channel.WritePacketAsync(value == null ? new byte[] { 0, 4 } : new byte[] { 0, 0 }.Concat(value).ToArray());
            await channel.WritePacketAsync(Eof());
        }

        [Theory]
        [MemberData(nameof(BatchComplexLobResultCases))]
        public async Task BatchMultiSendComplexLobResultsPreserveHandlesAfterDraining(bool asynchronous, bool collection, bool character, int api)
        {
            var data = Encoding.UTF8.GetBytes("患者🙂");
            var field = (collection ? new byte[] { 3, 8 } : new byte[] { 0 }).Concat(WithLength(InlineLob(data)))
                .Concat(collection ? WithLength(InlineLob(Array.Empty<byte>())) : Array.Empty<byte>()).ToArray();
            var server = Server(async channel =>
            {
                await ComplexScalarType(channel, scalar: character ? "CLOB" : "BLOB", collection: collection);
                for (byte id = 90; id < 93; id++) await PrepareComplexLobResult(channel, id);
                await StatementCommand(channel, 23); await StatementCommand(channel, 23);
                await ReplyComplexLobResult(channel, field); await ReplyComplexLobResult(channel, null);
                await StatementCommand(channel, 23);
                await ReplyComplexLobResult(channel, collection ? new byte[] { 1, 4 } : new byte[] { 4 });
                for (var i = 0; i < 3; i++) await StatementCommand(channel, 25);
                channel.BeginCommand(); await Query(channel, "after"); await channel.WritePacketAsync(Ok(7));
            }, lobLocators: true);
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port) + ";Use Batch Multi Send=true;Batch Multi Send Number=2");
            var handles = new List<OceanBaseLob>();
            try
            {
                await connection.OpenAsync(); var type = await connection.GetComplexTypeAsync("Owner", "Thing");
                var session = connection.GetOpenSession();
                using (var batch = connection.CreateBatch())
                {
                    for (var i = 0; i < 3; i++) BatchItem(batch, "select value from t" + i);
                    if (api == 0)
                    {
                        using var reader = asynchronous ? await batch.ExecuteReaderAsync() : batch.ExecuteReader();
                        for (var i = 0; i < 3; i++)
                        {
                            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                            if (i == 1) Assert.True(reader.IsDBNull(0));
                            else
                            {
                                var value = reader.GetFieldValue<OceanBaseComplexValue>(0); Assert.Same(type, value.Type);
                                var lobs = ComplexResultLobs(value).ToArray();
                                if (i == 2) Assert.Empty(lobs); else handles.AddRange(lobs);
                                foreach (var lob in lobs)
                                    Assert.Throws<InvalidOperationException>(() => { if (character) lob.OpenTextReader(); else lob.OpenRead(); });
                            }
                            Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                            Assert.Equal(i < 2, asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                        }
                    }
                    else if (api == 1)
                        handles.AddRange(ComplexResultLobs(Assert.IsType<OceanBaseComplexValue>(asynchronous ? await batch.ExecuteScalarAsync() : batch.ExecuteScalar())));
                    else Assert.Equal(-1, asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery());
                }
                connection.ClearComplexTypeCache();
                Assert.Equal(api != 2, session.RequiresDiscardOnReturn);
                for (var i = 0; i < handles.Count; i++)
                {
                    using var lob = handles[i]; var expected = i == 0 ? data : Array.Empty<byte>();
                    if (character) { using var source = lob.OpenTextReader(); Assert.Equal(Encoding.UTF8.GetString(expected), await source.ReadToEndAsync()); }
                    else
                    {
                        using var source = lob.OpenRead(); using var target = new MemoryStream();
                        await source.CopyToAsync(target); Assert.Equal(expected, target.ToArray());
                    }
                }
                Assert.False(session.RequiresDiscardOnReturn);
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(7, await after.ExecuteNonQueryAsync());
            }
            finally { foreach (var lob in handles) lob.Dispose(); connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        private static async Task ComplexNestedLobType(PacketChannel channel)
        {
            await ComplexDictionaryReply(channel, 61, "Owner", "Thing", true, new[] { "Owner", "Thing", "OBJECT", "2" });
            await StatementCommand(channel, 25);
            await ComplexDictionaryReply(channel, 62, "Owner", "Thing", false,
                new[] { "A", "1", "Owner", "Leaf", "OBJECT" }, new[] { "B", "2", null, "BLOB", "BLOB" });
            await StatementCommand(channel, 25);
            await ComplexScalarType(channel, name: "Leaf", scalar: "BLOB", collection: false);
        }

        private static byte[] ComplexLobPair(bool nested, byte[] second)
            => (nested ? new byte[] { 0, 0 } : new byte[] { 2, 0 })
                .Concat(WithLength(InlineLob(new byte[] { 1 }))).Concat(WithLength(second)).ToArray();

        [Theory]
        [InlineData(false)] [InlineData(true)]
        public async Task ComplexLobTrailingDataReleasesDecodedMembers(bool nested)
        {
            var server = Server(async channel =>
            {
                if (nested) await ComplexNestedLobType(channel); else await ComplexScalarType(channel, scalar: "BLOB");
                channel.BeginCommand(); await Query(channel, "after"); await channel.WritePacketAsync(Ok(7));
            }, lobLocators: true);
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port));
            try
            {
                await connection.OpenAsync(); await connection.GetComplexTypeAsync("Owner", "Thing"); var session = connection.GetOpenSession();
                using (var exchange = connection.StartCommand(session, null, 30, default, () => { }))
                {
                    var column = ResultColumn.Parse(ComplexColumn(), OceanBaseCompatibilityMode.Oracle).WithContext(exchange);
                    Assert.Throws<ProtocolException>(() => column.DecodeBinary(ComplexLobPair(nested, InlineLob(new byte[] { 2 })).Concat(new byte[] { 99 }).ToArray()));
                    Assert.False(session.RequiresDiscardOnReturn); exchange.Complete();
                }
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(7, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)] [InlineData(true, false)]
        [InlineData(false, true)] [InlineData(true, true)]
        public async Task ComplexLobDecodeFailureReleasesEarlierMembers(bool batchMode, bool nested)
        {
            var server = Server(async channel =>
            {
                if (nested) await ComplexNestedLobType(channel); else await ComplexScalarType(channel, scalar: "BLOB");
                await PrepareComplexLobResult(channel, 90); await StatementCommand(channel, 23);
                await ReplyComplexLobResult(channel, ComplexLobPair(nested, new byte[] { 1, 2, 3 }));
                try { Assert.Equal(0, await channelStreamRead(channel)); } catch (IOException) { }
            }, lobLocators: true);
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port) + ";Use Batch Multi Send=true");
            try
            {
                await connection.OpenAsync(); await connection.GetComplexTypeAsync("Owner", "Thing");
                var session = connection.GetOpenSession();
                using var command = connection.CreateCommand(); command.CommandText = "select value from t";
                using var batch = connection.CreateBatch(); BatchItem(batch, command.CommandText);
                if (!batchMode) command.Prepare();
                using var reader = batchMode ? await batch.ExecuteReaderAsync() : await command.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Throws<OceanBaseException>(() => reader.GetFieldValue<OceanBaseComplexValue>(0));
                Assert.Equal(ConnectionState.Broken, connection.State);
                Assert.False(session.RequiresDiscardOnReturn);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)] [InlineData(true, false)]
        [InlineData(false, true)] [InlineData(true, true)]
        public async Task BatchMultiSendComplexScalarFailureReleasesEveryLob(bool asynchronous, bool nested)
        {
            var server = Server(async channel =>
            {
                if (nested) await ComplexNestedLobType(channel); else await ComplexScalarType(channel, scalar: "BLOB");
                await PrepareComplexLobResult(channel, 90); await PrepareComplexLobResult(channel, 91);
                await StatementCommand(channel, 23); await StatementCommand(channel, 23);
                await ReplyComplexLobResult(channel, ComplexLobPair(nested, InlineLob(new byte[] { 2 })));
                channel.BeginResponse(); await channel.WritePacketAsync(Error());
                await StatementCommand(channel, 25); await StatementCommand(channel, 25);
                channel.BeginCommand(); await Query(channel, "after"); await channel.WritePacketAsync(Ok(7));
            }, lobLocators: true);
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port) + ";Use Batch Multi Send=true;Batch Multi Send Number=2");
            try
            {
                await connection.OpenAsync(); await connection.GetComplexTypeAsync("Owner", "Thing"); var session = connection.GetOpenSession();
                using (var batch = connection.CreateBatch())
                {
                    BatchItem(batch, "select first"); BatchItem(batch, "select second");
                    var error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? batch.ExecuteScalarAsync() : Task.FromResult(batch.ExecuteScalar()));
                    Assert.Same(batch.BatchCommands[1], error.BatchCommand);
                }
                Assert.False(session.RequiresDiscardOnReturn); Assert.Equal(ConnectionState.Open, connection.State);
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(7, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
