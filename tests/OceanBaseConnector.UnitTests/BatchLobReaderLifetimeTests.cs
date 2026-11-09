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
        public static IEnumerable<object[]> ClosingLobReaderCases()
        {
            foreach (var asynchronous in new[] { false, true })
                foreach (var character in new[] { false, true })
                    foreach (var shape in new[] { "root", "named", "anonymous" })
                        foreach (var owned in new[] { false, true })
                            foreach (var sequential in new[] { false, true })
                                if (shape != "named" || !owned) yield return new object[] { asynchronous, character, shape, owned, sequential };
        }

        [Theory]
        [MemberData(nameof(ClosingLobReaderCases))]
        public async Task ClosingMultiSendReaderKeepsLobsReadableBetweenDrainAndClose(bool asynchronous, bool character, string shape, bool owned, bool sequential)
        {
            var code = character ? (byte)211 : (byte)210;
            var data = Encoding.UTF8.GetBytes("患者🙂");
            byte[] ColumnMetadata() => shape == "root" ? Column("lob", code) : shape == "named" ? ComplexColumn() : ComplexColumn("", "", element: code);
            var server = Server(async channel =>
            {
                if (shape == "named") await ComplexScalarType(channel, scalar: character ? "CLOB" : "BLOB", collection: false);
                for (byte id = 90; id < 93; id++)
                {
                    await StatementCommand(channel, 22);
                    await channel.WritePacketAsync(new byte[] { 0, id, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
                    await channel.WritePacketAsync(ColumnMetadata()); await channel.WritePacketAsync(Eof());
                }
                for (var i = 0; i < 3; i++)
                {
                    if (i != 1) { await StatementCommand(channel, 23); if (i == 0) await StatementCommand(channel, 23); }
                    channel.BeginResponse(); await channel.WritePacketAsync(new byte[] { 1 });
                    await channel.WritePacketAsync(ColumnMetadata()); await channel.WritePacketAsync(Eof());
                    var field = WithLength(InlineLob(i == 0 ? data : Array.Empty<byte>()));
                    if (shape != "root") field = (shape == "named" ? new byte[] { 0 } : new byte[] { 1, 0 }).Concat(field).ToArray();
                    await channel.WritePacketAsync(i == 1 ? new byte[] { 0, 4 } : new byte[] { 0, 0 }.Concat(field).ToArray());
                    await channel.WritePacketAsync(Eof());
                }
                channel.BeginCommand(); await Query(channel, "after drain"); await channel.WritePacketAsync(Ok(7));
                Assert.Equal(0, await channelStreamRead(channel));
            }, lobLocators: true);
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port) + ";Use Batch Multi Send=true;Batch Multi Send Number=2");
            var handles = new List<OceanBaseLob>();
            try
            {
                if (!owned) await connection.OpenAsync();
                if (shape == "named") await connection.GetComplexTypeAsync("Owner", "Thing");
                using var batch = owned ? new OceanBaseBatch(connection) : connection.CreateBatch();
                for (var i = 0; i < 3; i++) BatchItem(batch, "select lob from t" + i);
                var behavior = CommandBehavior.CloseConnection | (sequential ? CommandBehavior.SequentialAccess : 0);
                using var reader = asynchronous ? await batch.ExecuteReaderAsync(behavior) : batch.ExecuteReader(behavior);
                for (var i = 0; i < 3; i++)
                {
                    Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                    if (i == 1) Assert.True(reader.IsDBNull(0));
                    else
                    {
                        var lob = shape == "root" ? reader.GetFieldValue<OceanBaseLob>(0) : Assert.Single(ComplexResultLobs(reader.GetFieldValue<OceanBaseComplexValue>(0)));
                        handles.Add(lob);
                        Assert.Throws<InvalidOperationException>(() => { if (character) lob.OpenTextReader(); else lob.OpenRead(); });
                    }
                    Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                    Assert.Equal(i != 2, asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                }
                Assert.False(connection.GetOpenSession().IsReservedForOther(null)); Assert.Equal(ConnectionState.Open, connection.State);
                for (var i = 0; i < handles.Count; i++)
                {
                    var expected = i == 0 ? data : Array.Empty<byte>();
                    if (character) { using var source = handles[i].OpenTextReader(); Assert.Equal(Encoding.UTF8.GetString(expected), await source.ReadToEndAsync()); }
                    else { using var source = handles[i].OpenRead(); using var target = new MemoryStream(); await source.CopyToAsync(target); Assert.Equal(expected, target.ToArray()); }
                }
                using var stale = new OceanBaseLob(handles[0].Context, handles[0].BindingBytes(), character);
                using (var query = connection.CreateCommand()) { query.CommandText = "after drain"; Assert.Equal(7, await query.ExecuteNonQueryAsync()); }
                if (asynchronous) await reader.CloseAsync(); else reader.Close();
                Assert.Equal(ConnectionState.Closed, connection.State);
                Assert.Throws<InvalidOperationException>(() => { if (character) stale.OpenTextReader(); else stale.OpenRead(); });
            }
            finally { foreach (var lob in handles) lob.Dispose(); connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)] [InlineData(true)]
        public async Task ClosingMultiSendLobReaderAbortsUnreadResponses(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await BatchLobPrepared(channel, 90, 210); await BatchLobPrepared(channel, 91, 210);
                await StatementCommand(channel, 23); await StatementCommand(channel, 23);
                // The reader is closed mid-response, so the client can reset the socket before this
                // reply finishes: an aborted write is the expected outcome of this scenario.
                try { await BatchLobReply(channel, 210, InlineLob(new byte[] { 42 })); Assert.Equal(0, await channelStreamRead(channel)); } catch (IOException) { }
            }, lobLocators: true);
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port) + ";Use Batch Multi Send=true");
            try
            {
                await connection.OpenAsync(); using var batch = connection.CreateBatch(); BatchItem(batch, "select first"); BatchItem(batch, "select second");
                using var reader = asynchronous ? await batch.ExecuteReaderAsync(CommandBehavior.CloseConnection) : batch.ExecuteReader(CommandBehavior.CloseConnection);
                Assert.True(await reader.ReadAsync()); using var lob = reader.GetFieldValue<OceanBaseLob>(0);
                if (asynchronous) await reader.CloseAsync(); else reader.Close();
                Assert.Equal(ConnectionState.Closed, connection.State); Assert.Throws<InvalidOperationException>(() => lob.OpenRead());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)] [InlineData(true)]
        public async Task ClosingMultiSendLobReaderCannotCloseReopenedConnection(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await BatchLobPrepared(channel, 90, 210); await StatementCommand(channel, 23);
                // The client closes while this reply is still being written, so an aborted write is
                // the expected outcome of this scenario.
                try { await BatchLobReply(channel, 210, InlineLob(new byte[] { 42 })); Assert.Equal(0, await channelStreamRead(channel)); } catch (IOException) { }
            }, lobLocators: true);
            var replacement = Server(async channel => { await Query(channel, "new lease"); await channel.WritePacketAsync(Ok(7)); });
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port) + ";Use Batch Multi Send=true");
            try
            {
                await connection.OpenAsync(); using var batch = connection.CreateBatch(); BatchItem(batch, "select lob from t");
                using var reader = asynchronous ? await batch.ExecuteReaderAsync(CommandBehavior.CloseConnection) : batch.ExecuteReader(CommandBehavior.CloseConnection);
                Assert.True(await reader.ReadAsync()); using var lob = reader.GetFieldValue<OceanBaseLob>(0);
                Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                connection.Close(); connection.ConnectionString = Configuration(replacement.Port); await connection.OpenAsync();
                if (asynchronous) await reader.CloseAsync(); else reader.Close();
                Assert.Equal(ConnectionState.Open, connection.State); Assert.Throws<InvalidOperationException>(() => lob.OpenRead());
                using var after = connection.CreateCommand(); after.CommandText = "new lease"; Assert.Equal(7, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); replacement.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10)); await replacement.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false, false)] [InlineData(true, false, false)]
        [InlineData(false, true, false)] [InlineData(true, true, false)]
        [InlineData(false, false, true)] [InlineData(true, false, true)]
        public async Task ClosingMultiSendReaderKeepsReturningLobsReadableAfterDrain(bool asynchronous, bool owned, bool complex)
        {
            var server = Server(async channel =>
            {
                if (complex) await ComplexScalarType(channel, scalar: "BLOB", collection: false);
                await BatchReturningRequest(channel, 1); await BatchReturningRequest(channel, 2);
                await ReturningResourceReply(channel, 90, complex ? (byte)160 : (byte)210, ReturningResourceField(new byte[] { 42 }, complex, false, true));
                await ReturningResourceReply(channel, 91, complex ? (byte)160 : (byte)210, ReturningResourceField(Array.Empty<byte>(), complex, false, true));
                Assert.Equal(0, await channelStreamRead(channel));
            }, lobLocators: true, serverVersion: "4.4.2.2");
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port) + ";Use Oracle Prepare Execute=true;Use Batch Multi Send=true");
            var handles = new List<OceanBaseLob>();
            try
            {
                if (!owned) await connection.OpenAsync();
                var type = complex ? await connection.GetComplexTypeAsync("Owner", "Thing") : null;
                using var batch = owned ? new OceanBaseBatch(connection) : connection.CreateBatch();
                var outputs = new[] { BatchReturningItem(batch, 1), BatchReturningItem(batch, 2) };
                foreach (var output in outputs)
                {
                    output.ResetDbType();
                    if (complex) output.ComplexType = type; else output.OceanBaseDbType = OceanBaseDbType.Blob;
                }
                using var reader = asynchronous ? await batch.ExecuteReaderAsync(CommandBehavior.CloseConnection) : batch.ExecuteReader(CommandBehavior.CloseConnection);
                Assert.False(await reader.ReadAsync());
                Assert.True(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                Assert.False(await reader.ReadAsync());
                Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                handles.AddRange(outputs.Select(output => complex ? Assert.Single(ComplexResultLobs(Assert.IsType<OceanBaseComplexValue>(output.Value))) : Assert.IsType<OceanBaseLob>(output.Value)));
                using (var source = handles[0].OpenRead()) Assert.Equal(42, source.ReadByte());
                if (asynchronous) await reader.CloseAsync(); else reader.Close();
                Assert.Equal(ConnectionState.Closed, connection.State); Assert.Throws<InvalidOperationException>(() => handles[1].OpenRead());
            }
            finally { foreach (var lob in handles) lob.Dispose(); connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
