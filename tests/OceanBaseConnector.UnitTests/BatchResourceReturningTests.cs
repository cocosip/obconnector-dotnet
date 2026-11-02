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
        public static IEnumerable<object[]> ResourceReturningCases()
        {
            foreach (var asynchronous in new[] { false, true })
                foreach (var kind in new[] { "blob", "clob", "object-blob", "object-clob", "object-raw", "collection-blob", "collection-raw" })
                    foreach (var api in new[] { "nonquery", "scalar", "reader" })
                        yield return new object[] { asynchronous, kind, api };
        }

        private static byte[] ReturningResourceField(byte[] content, bool complex, bool collection, bool lob)
        {
            var field = WithLength(lob ? InlineLob(content) : content);
            return complex ? (collection ? new byte[] { 1, 0 } : new byte[] { 0 }).Concat(field).ToArray() : field;
        }

        private static async Task ReturningResourceReply(PacketChannel channel, byte id, byte code, byte[]? field, bool metadataMarker = false, bool fail = false, bool extraRow = false, bool tail = true)
        {
            channel.BeginResponse(); var header = OracleExecuteHeader(flags: metadataMarker ? 0u : 4u); header[1] = id;
            await channel.WritePacketAsync(header); await channel.WritePacketAsync(Column("in", 8)); await channel.WritePacketAsync(Column("out", 6)); await channel.WritePacketAsync(Eof());
            await channel.WritePacketAsync(code == 160 ? ComplexColumn("", "", element: 255) : Column("value", code));
            await channel.WritePacketAsync(metadataMarker ? new byte[] { 254, 0, 0, 0, 16 } : Eof());
            await channel.WritePacketAsync(field == null ? new byte[] { 0, 4 } : new byte[] { 0, 0 }.Concat(field).ToArray());
            if (extraRow) await channel.WritePacketAsync(new byte[] { 0, 0 }.Concat(field!).ToArray());
            await channel.WritePacketAsync(Eof()); if (tail) await channel.WritePacketAsync(fail ? Error() : Ok(1));
        }

        [Theory]
        [MemberData(nameof(ResourceReturningCases))]
        public async Task BatchMultiSendResourceReturningPreservesValuesAndHandles(bool asynchronous, string kind, string api)
        {
            var complex = kind.Contains('-'); var collection = kind.StartsWith("collection", StringComparison.Ordinal);
            var character = kind.EndsWith("clob", StringComparison.Ordinal); var lob = !kind.EndsWith("raw", StringComparison.Ordinal);
            var code = complex ? (byte)160 : character ? (byte)211 : (byte)210;
            var content = Encoding.UTF8.GetBytes("患者🙂");
            var server = Server(async channel =>
            {
                if (complex) await ComplexScalarType(channel, scalar: lob ? character ? "CLOB" : "BLOB" : "RAW", collection: collection);
                await BatchReturningRequest(channel, 1); await BatchReturningRequest(channel, 2);
                await ReturningResourceReply(channel, 90, code, ReturningResourceField(content, complex, collection, lob));
                await ReturningResourceReply(channel, 91, code, null, metadataMarker: true);
                await BatchReturningRequest(channel, 3);
                await ReturningResourceReply(channel, 92, code, ReturningResourceField(Array.Empty<byte>(), complex, collection, lob), metadataMarker: true);
                for (byte id = 90; id < 93; id++) Assert.Equal(id, (await StatementCommand(channel, 25))[1]);
                channel.BeginCommand(); await Query(channel, "after"); await channel.WritePacketAsync(Ok(1));
            }, lobLocators: true, serverVersion: "4.4.2.2");
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port) + ";Use Oracle Prepare Execute=true;Use Batch Multi Send=true;Batch Multi Send Number=2");
            var handles = new List<OceanBaseLob>();
            try
            {
                await connection.OpenAsync(); var type = complex ? await connection.GetComplexTypeAsync("Owner", "Thing") : null;
                using (var batch = connection.CreateBatch())
                {
                    var outputs = new[] { BatchReturningItem(batch, 1), BatchReturningItem(batch, 2), BatchReturningItem(batch, 3) };
                    foreach (var output in outputs)
                    {
                        output.ResetDbType(); output.Value = "old";
                        if (complex) output.ComplexType = type; else output.OceanBaseDbType = character ? OceanBaseDbType.Clob : OceanBaseDbType.Blob;
                    }
                    if (api == "nonquery") Assert.Equal(3, asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery());
                    else if (api == "scalar") Assert.Null(asynchronous ? await batch.ExecuteScalarAsync() : batch.ExecuteScalar());
                    else
                    {
                        using var reader = asynchronous ? await batch.ExecuteReaderAsync() : batch.ExecuteReader();
                        for (var i = 0; i < 3; i++)
                        {
                            Assert.Equal(0, reader.FieldCount); Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                            if (i == 0 && lob)
                            {
                                var handle = complex ? Assert.Single(ComplexResultLobs(Assert.IsType<OceanBaseComplexValue>(outputs[0].Value))) : Assert.IsType<OceanBaseLob>(outputs[0].Value);
                                Assert.Throws<InvalidOperationException>(() => { if (character) handle.OpenTextReader(); else handle.OpenRead(); });
                            }
                            Assert.Equal(i < 2, asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                        }
                        Assert.Equal(3, reader.RecordsAffected);
                    }
                    Assert.Equal(DBNull.Value, outputs[1].Value);
                    foreach (var i in new[] { 0, 2 })
                    {
                        var value = outputs[i].Value;
                        if (complex) { var structured = Assert.IsType<OceanBaseComplexValue>(value); Assert.Same(type, structured.Type); value = Assert.Single(structured.GetValues()); }
                        if (lob) handles.Add(Assert.IsType<OceanBaseLob>(value)); else Assert.Equal(i == 0 ? content : Array.Empty<byte>(), Assert.IsType<byte[]>(value));
                    }
                }
                connection.ClearComplexTypeCache();
                Assert.Equal(lob, connection.GetOpenSession().RequiresDiscardOnReturn);
                for (var i = 0; i < handles.Count; i++)
                {
                    var expected = i == 0 ? content : Array.Empty<byte>(); using var handle = handles[i];
                    if (character) { using var source = handle.OpenTextReader(); Assert.Equal(Encoding.UTF8.GetString(expected), await source.ReadToEndAsync()); }
                    else { using var source = handle.OpenRead(); using var target = new MemoryStream(); await source.CopyToAsync(target); Assert.Equal(expected, target.ToArray()); }
                }
                Assert.False(connection.GetOpenSession().RequiresDiscardOnReturn);
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            finally { foreach (var handle in handles) handle.Dispose(); connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false, false)] [InlineData(true, false, false)]
        [InlineData(false, true, false)] [InlineData(true, true, false)]
        [InlineData(false, false, true)] [InlineData(true, false, true)]
        [InlineData(false, true, true)] [InlineData(true, true, true)]
        public async Task ResourceReturningFailureReleasesOnlyUnpublishedOutputs(bool batchMode, bool complex, bool decodeFailure)
        {
            var server = Server(async channel =>
            {
                if (complex) await ComplexScalarType(channel, scalar: "BLOB", collection: false);
                await StatementCommand(channel, 0xa1); channel.BeginResponse(); var header = OracleExecuteHeader(2, 2); header[1] = 90;
                await channel.WritePacketAsync(header); await channel.WritePacketAsync(Column("a", 6)); await channel.WritePacketAsync(Column("b", 6)); await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(complex ? ComplexColumn("", "", element: 255) : Column("a", 210));
                await channel.WritePacketAsync(Column("b", 246)); await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(new byte[] { 0, 0 }.Concat(ReturningResourceField(new byte[] { 7 }, complex, false, true))
                    .Concat(WithLength(Encoding.UTF8.GetBytes(decodeFailure ? "bad" : "7"))).ToArray());
                if (decodeFailure) { try { Assert.Equal(0, await channelStreamRead(channel)); } catch (IOException) { } }
                else
                {
                    await channel.WritePacketAsync(Eof()); await channel.WritePacketAsync(Error());
                    Assert.Equal(90, (await StatementCommand(channel, 25))[1]); channel.BeginCommand(); await Query(channel, "after"); await channel.WritePacketAsync(Ok(1));
                }
            }, lobLocators: true, serverVersion: "4.4.2.2");
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port) + ";Use Oracle Prepare Execute=true;Use Batch Multi Send=true");
            try
            {
                await connection.OpenAsync(); var session = connection.GetOpenSession(); var type = complex ? await connection.GetComplexTypeAsync("Owner", "Thing") : null;
                using (var batch = connection.CreateBatch())
                using (var command = connection.CreateCommand())
                {
                    const string sql = "insert into t values (1) returning a,b into :a,:b";
                    command.CommandText = sql; var item = BatchItem(batch, sql);
                    var a = new OceanBaseParameter { ParameterName = "a", Direction = ParameterDirection.Output, Value = "old-a", ComplexType = type };
                    if (!complex) a.OceanBaseDbType = OceanBaseDbType.Blob;
                    var b = new OceanBaseParameter { ParameterName = "b", Direction = ParameterDirection.Output, Value = "old-b" };
                    if (batchMode) { item.Parameters.Add(a); item.Parameters.Add(b); }
                    else { command.Parameters.Add(a); command.Parameters.Add(b); }
                    await Assert.ThrowsAsync<OceanBaseException>(() => batchMode ? batch.ExecuteNonQueryAsync() : command.ExecuteNonQueryAsync());
                    Assert.Equal("old-a", a.Value); Assert.Equal("old-b", b.Value); Assert.False(session.RequiresDiscardOnReturn);
                    Assert.Equal(decodeFailure ? ConnectionState.Broken : ConnectionState.Open, connection.State);
                }
                if (!decodeFailure) { using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync()); }
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)] [InlineData(true, false)]
        [InlineData(false, true)] [InlineData(true, true)]
        public async Task BatchMultiSendResourceReturningRetainsSuccessfulPrefix(bool asynchronous, bool complex)
        {
            var server = Server(async channel =>
            {
                if (complex) await ComplexScalarType(channel, scalar: "BLOB", collection: false);
                await BatchReturningRequest(channel, 1); await BatchReturningRequest(channel, 2);
                var field = ReturningResourceField(new byte[] { 7 }, complex, false, true);
                await ReturningResourceReply(channel, 90, complex ? (byte)160 : (byte)210, field);
                await ReturningResourceReply(channel, 91, complex ? (byte)160 : (byte)210, field, fail: true);
                for (byte id = 90; id < 92; id++) Assert.Equal(id, (await StatementCommand(channel, 25))[1]);
                channel.BeginCommand(); await Query(channel, "after"); await channel.WritePacketAsync(Ok(1));
            }, lobLocators: true, serverVersion: "4.4.2.2");
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port) + ";Use Oracle Prepare Execute=true;Use Batch Multi Send=true");
            OceanBaseLob? retained = null;
            try
            {
                await connection.OpenAsync(); var type = complex ? await connection.GetComplexTypeAsync("Owner", "Thing") : null;
                using (var batch = connection.CreateBatch())
                {
                    var outputs = new[] { BatchReturningItem(batch, 1), BatchReturningItem(batch, 2) };
                    foreach (var output in outputs)
                    {
                        output.ResetDbType(); output.Value = "old";
                        if (complex) output.ComplexType = type; else output.OceanBaseDbType = OceanBaseDbType.Blob;
                    }
                    var error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? batch.ExecuteNonQueryAsync() : Task.FromResult(batch.ExecuteNonQuery()));
                    Assert.Same(batch.BatchCommands[1], error.BatchCommand); Assert.Equal("old", outputs[1].Value);
                    retained = complex ? Assert.Single(ComplexResultLobs(Assert.IsType<OceanBaseComplexValue>(outputs[0].Value))) : Assert.IsType<OceanBaseLob>(outputs[0].Value);
                }
                using (var source = retained.OpenRead()) Assert.Equal(7, source.ReadByte());
                retained.Dispose(); Assert.False(connection.GetOpenSession().RequiresDiscardOnReturn);
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            finally { retained?.Dispose(); connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData("owned", false)] [InlineData("close", false)] [InlineData("close", true)]
        public async Task BatchMultiSendResourceReturningRejectsClosingConnectionBeforeSending(string boundary, bool complex)
        {
            var server = Server(async channel =>
            {
                if (complex) await ComplexScalarType(channel, scalar: "BLOB", collection: false);
                Assert.Equal(0, await channelStreamRead(channel));
            }, lobLocators: true, serverVersion: "4.4.2.2");
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port) + ";Use Oracle Prepare Execute=true;Use Batch Multi Send=true");
            try
            {
                if (boundary != "owned") await connection.OpenAsync();
                var type = complex ? await connection.GetComplexTypeAsync("Owner", "Thing") : null;
                using var batch = boundary == "owned" ? new OceanBaseBatch(connection) : connection.CreateBatch();
                var output = BatchReturningItem(batch, 1); output.ResetDbType(); output.Value = DBNull.Value;
                if (complex) output.ComplexType = type; else output.OceanBaseDbType = OceanBaseDbType.Blob;
                if (boundary == "close") await Assert.ThrowsAsync<NotSupportedException>(() => batch.ExecuteReaderAsync(CommandBehavior.CloseConnection));
                else await Assert.ThrowsAsync<NotSupportedException>(() => batch.ExecuteNonQueryAsync());
                Assert.Equal(DBNull.Value, output.Value);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData("metadata")] [InlineData("rows")] [InlineData("cancel")]
        public async Task BatchMultiSendResourceReturningRejectsMalformedOrCancelledOutputWithoutLeaks(string failure)
        {
            var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = Server(async channel =>
            {
                await BatchReturningRequest(channel, 1);
                try
                {
                    await ReturningResourceReply(channel, 90, failure == "metadata" ? (byte)211 : (byte)210,
                        failure == "metadata" ? null : ReturningResourceField(new byte[] { 7 }, false, false, true), extraRow: failure == "rows", tail: false);
                    waiting.TrySetResult();
                }
                catch (IOException) when (failure != "cancel") { }
                try { Assert.Equal(0, await channelStreamRead(channel)); } catch (IOException) { }
            }, lobLocators: true, serverVersion: "4.4.2.2");
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port) + ";Use Oracle Prepare Execute=true;Use Batch Multi Send=true");
            try
            {
                await connection.OpenAsync(); var session = connection.GetOpenSession(); using var batch = connection.CreateBatch();
                var output = BatchReturningItem(batch, 1); output.OceanBaseDbType = OceanBaseDbType.Blob; output.Value = "old";
                using var cancellation = new System.Threading.CancellationTokenSource();
                var executing = batch.ExecuteNonQueryAsync(cancellation.Token);
                if (failure == "cancel")
                {
                    await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    Assert.True(System.Threading.SpinWait.SpinUntil(() => session.RequiresDiscardOnReturn, TimeSpan.FromSeconds(10)));
                    cancellation.Cancel();
                    var error = await Assert.ThrowsAsync<OceanBaseException>(() => executing); Assert.True(error.IsExecutionOutcomeUnknown);
                }
                else await Assert.ThrowsAsync<OceanBaseException>(() => executing);
                Assert.Equal("old", output.Value); Assert.False(session.RequiresDiscardOnReturn); Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
