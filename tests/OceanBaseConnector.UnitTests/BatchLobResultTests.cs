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
        private static async Task BatchLobPrepared(PacketChannel channel, byte id, byte type)
        {
            await StatementCommand(channel, 22);
            await channel.WritePacketAsync(new byte[] { 0, id, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
            await channel.WritePacketAsync(Column("lob", type)); await channel.WritePacketAsync(Eof());
        }

        private static async Task BatchLobReply(PacketChannel channel, byte type, byte[]? locator)
        {
            channel.BeginResponse(); await channel.WritePacketAsync(new byte[] { 1 });
            await channel.WritePacketAsync(Column("lob", type)); await channel.WritePacketAsync(Eof());
            await channel.WritePacketAsync(locator == null ? new byte[] { 0, 4 } : new byte[] { 0, 0 }.Concat(WithLength(locator)).ToArray());
            await channel.WritePacketAsync(Eof());
        }

        [Theory]
        [InlineData(false, false, 0)] [InlineData(true, false, 0)]
        [InlineData(false, true, 0)] [InlineData(true, true, 0)]
        [InlineData(false, false, 1)] [InlineData(true, false, 1)]
        [InlineData(false, true, 1)] [InlineData(true, true, 1)]
        [InlineData(false, false, 2)] [InlineData(true, false, 2)]
        [InlineData(false, true, 2)] [InlineData(true, true, 2)]
        public async Task BatchMultiSendLobResultsRetainTheirSessionAfterDraining(bool asynchronous, bool character, int api)
        {
            var type = character ? (byte)211 : (byte)210;
            var data = Encoding.UTF8.GetBytes("国🙂");
            var server = Server(async channel =>
            {
                for (byte id = 90; id < 93; id++) await BatchLobPrepared(channel, id, type);
                await StatementCommand(channel, 23); await StatementCommand(channel, 23);
                await BatchLobReply(channel, type, InlineLob(data)); await BatchLobReply(channel, type, null);
                await StatementCommand(channel, 23); await BatchLobReply(channel, type, InlineLob(new byte[] { 97 }));
                for (var i = 0; i < 3; i++) await StatementCommand(channel, 25);
                channel.BeginCommand(); await Query(channel, "after"); await channel.WritePacketAsync(Ok(7));
            }, lobLocators: true);
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port) + ";Use Batch Multi Send=true;Batch Multi Send Number=2");
            var handles = new List<OceanBaseLob>();
            try
            {
                await connection.OpenAsync(); var session = connection.GetOpenSession();
                using (var batch = connection.CreateBatch())
                {
                    for (var i = 0; i < 3; i++) BatchItem(batch, "select lob from t" + i);
                    if (api == 0)
                    {
                        using var reader = asynchronous ? await batch.ExecuteReaderAsync() : batch.ExecuteReader();
                        for (var i = 0; i < 3; i++)
                        {
                            Assert.Equal(typeof(OceanBaseLob), reader.GetFieldType(0));
                            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                            if (i == 1) Assert.True(reader.IsDBNull(0));
                            else
                            {
                                var lob = reader.GetFieldValue<OceanBaseLob>(0); handles.Add(lob);
                                Assert.Throws<InvalidOperationException>(() => { if (character) lob.OpenTextReader(); else lob.OpenRead(); });
                            }
                            Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                            Assert.Equal(i < 2, asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                        }
                    }
                    else if (api == 1) handles.Add(Assert.IsType<OceanBaseLob>(asynchronous ? await batch.ExecuteScalarAsync() : batch.ExecuteScalar()));
                    else Assert.Equal(-1, asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery());
                }
                Assert.Equal(api != 2, session.RequiresDiscardOnReturn);
                for (var i = 0; i < handles.Count; i++)
                {
                    using var lob = handles[i];
                    var expected = i == 0 ? data : new byte[] { 97 };
                    if (character)
                    {
                        using var source = lob.OpenTextReader(); Assert.Equal(Encoding.UTF8.GetString(expected), await source.ReadToEndAsync());
                    }
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

        [Theory]
        [InlineData(false, false)] [InlineData(true, false)]
        [InlineData(false, true)] [InlineData(true, true)]
        public async Task BatchMultiSendLobFailureInvalidatesHandlesAndReleasesUnpublishedScalar(bool asynchronous, bool earlyClose)
        {
            var server = Server(async channel =>
            {
                await BatchLobPrepared(channel, 90, 210); await BatchLobPrepared(channel, 91, 210);
                await StatementCommand(channel, 23); await StatementCommand(channel, 23);
                await BatchLobReply(channel, 210, InlineLob(new byte[] { 1 }));
                if (!earlyClose)
                {
                    channel.BeginResponse(); await channel.WritePacketAsync(Error());
                    await StatementCommand(channel, 25); await StatementCommand(channel, 25);
                    channel.BeginCommand(); await Query(channel, "after"); await channel.WritePacketAsync(Ok(7));
                    return;
                }
                try { Assert.Equal(0, await channelStreamRead(channel)); } catch (IOException) { }
            }, lobLocators: true);
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port) + ";Use Batch Multi Send=true;Batch Multi Send Number=2");
            try
            {
                await connection.OpenAsync(); var session = connection.GetOpenSession();
                using var batch = connection.CreateBatch(); BatchItem(batch, "select first"); BatchItem(batch, "select second");
                if (earlyClose)
                {
                    using var reader = asynchronous ? await batch.ExecuteReaderAsync() : batch.ExecuteReader();
                    Assert.True(await reader.ReadAsync()); using var lob = reader.GetFieldValue<OceanBaseLob>(0);
                    if (asynchronous) await reader.CloseAsync(); else reader.Close();
                    Assert.Throws<InvalidOperationException>(() => lob.OpenRead());
                }
                else
                {
                    var error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? batch.ExecuteScalarAsync() : Task.FromResult(batch.ExecuteScalar()));
                    Assert.Same(batch.BatchCommands[1], error.BatchCommand);
                }
                Assert.Equal(earlyClose ? ConnectionState.Broken : ConnectionState.Open, connection.State);
                Assert.False(session.RequiresDiscardOnReturn);
                if (!earlyClose)
                {
                    batch.Dispose();
                    using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(7, await after.ExecuteNonQueryAsync());
                }
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, "owned")] [InlineData(true, "owned")]
        [InlineData(false, "capability")] [InlineData(true, "capability")]
        [InlineData(false, "charset")] [InlineData(true, "charset")]
        [InlineData(false, "owned-anonymous")] [InlineData(true, "owned-anonymous")]
        [InlineData(false, "capability-anonymous")] [InlineData(true, "capability-anonymous")]
        public async Task BatchMultiSendLobResultsValidateCapabilityAndRetainOwnedSessions(bool asynchronous, string boundary)
        {
            var anonymous = boundary.EndsWith("-anonymous", StringComparison.Ordinal);
            if (anonymous) boundary = boundary.Substring(0, boundary.Length - "-anonymous".Length);
            var complex = boundary.EndsWith("-complex", StringComparison.Ordinal);
            if (complex) boundary = boundary.Substring(0, boundary.Length - "-complex".Length);
            var server = Server(async channel =>
            {
                if (complex) await ComplexScalarType(channel, scalar: "BLOB", collection: false);
                await StatementCommand(channel, 22);
                await channel.WritePacketAsync(new byte[] { 0, 90, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
                await channel.WritePacketAsync(anonymous ? ComplexColumn("", "", element: 211) : complex ? ComplexColumn() : Column("lob", 211, boundary == "charset" ? (ushort)63 : (ushort)45));
                await channel.WritePacketAsync(Eof());
                if (boundary == "owned")
                {
                    await StatementCommand(channel, 23); await OwnedLobDrainReply(channel, 211, anonymous, true, false);
                }
                await StatementCommand(channel, 25);
                Assert.Equal(0, await channelStreamRead(channel));
            }, lobLocators: boundary != "capability");
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port) + ";Use Batch Multi Send=true");
            try
            {
                if (boundary != "owned") await connection.OpenAsync();
                if (complex) await connection.GetComplexTypeAsync("Owner", "Thing");
                using var batch = boundary == "owned" ? new OceanBaseBatch(connection) : connection.CreateBatch();
                BatchItem(batch, "select lob from t");
                if (boundary == "owned")
                {
                    var value = asynchronous ? await batch.ExecuteScalarAsync() : batch.ExecuteScalar();
                    using var lob = anonymous ? Assert.Single(ComplexResultLobs(Assert.IsType<OceanBaseComplexValue>(value))) : Assert.IsType<OceanBaseLob>(value);
                    using var text = lob.OpenTextReader(); Assert.Equal("a", await text.ReadToEndAsync());
                }
                else if (asynchronous) await Assert.ThrowsAsync<NotSupportedException>(() => batch.ExecuteScalarAsync());
                else Assert.Throws<NotSupportedException>(() => batch.ExecuteScalar());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
