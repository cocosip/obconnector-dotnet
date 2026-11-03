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
        public static IEnumerable<object[]> OwnedLobResultCases()
        {
            foreach (var asynchronous in new[] { false, true })
                foreach (var multiSend in new[] { false, true })
                    foreach (var character in new[] { false, true })
                        yield return new object[] { asynchronous, multiSend, character };
        }

        [Theory]
        [MemberData(nameof(OwnedLobResultCases))]
        public async Task OwnedBatchScalarRetainsItsLobUntilBatchDisposal(bool asynchronous, bool multiSend, bool character)
        {
            var code = character ? (byte)211 : (byte)210;
            var content = Encoding.UTF8.GetBytes("患者🙂");
            var server = Server(async channel =>
            {
                if (multiSend)
                {
                    await BatchLobPrepared(channel, 90, code); await StatementCommand(channel, 23);
                    await BatchLobReply(channel, code, InlineLob(content));
                    await StatementCommand(channel, 25);
                }
                else
                {
                    await Query(channel, "select lob from t");
                    await channel.WritePacketAsync(new byte[] { 1 }); await channel.WritePacketAsync(Column("lob", code)); await channel.WritePacketAsync(Eof());
                    await channel.WritePacketAsync(WithLength(InlineLob(content))); await channel.WritePacketAsync(Eof());
                }
                Assert.Equal(0, await channelStreamRead(channel));
            }, lobLocators: true);
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port) + ";Use Batch Multi Send=" + multiSend);
            try
            {
                using var batch = new OceanBaseBatch(connection); BatchItem(batch, "select lob from t");
                using var lob = Assert.IsType<OceanBaseLob>(asynchronous ? await batch.ExecuteScalarAsync() : batch.ExecuteScalar());
                Assert.Equal(ConnectionState.Open, connection.State);
                if (asynchronous)
                {
                    await Assert.ThrowsAsync<InvalidOperationException>(() => batch.ExecuteNonQueryAsync());
                    await Assert.ThrowsAsync<InvalidOperationException>(() => batch.ExecuteScalarAsync());
                    await Assert.ThrowsAsync<InvalidOperationException>(() => batch.ExecuteReaderAsync());
                    batch.RewriteBatchedInserts = true;
                    await Assert.ThrowsAsync<InvalidOperationException>(() => batch.ExecuteNonQueryAsync());
                }
                else
                {
                    Assert.Throws<InvalidOperationException>(() => batch.ExecuteNonQuery());
                    Assert.Throws<InvalidOperationException>(() => batch.ExecuteScalar());
                    Assert.Throws<InvalidOperationException>(() => batch.ExecuteReader());
                    batch.RewriteBatchedInserts = true; Assert.Throws<InvalidOperationException>(() => batch.ExecuteNonQuery());
                }
                Assert.Equal(ConnectionState.Open, connection.State);
                if (character) { using var text = lob.OpenTextReader(); Assert.Equal("患者🙂", await text.ReadToEndAsync()); }
                else { using var source = lob.OpenRead(); using var target = new MemoryStream(); await source.CopyToAsync(target); Assert.Equal(content, target.ToArray()); }
                using var stale = new OceanBaseLob(lob.Context, lob.BindingBytes(), character);
                if (asynchronous) await batch.DisposeAsync(); else batch.Dispose();
                Assert.Equal(ConnectionState.Closed, connection.State);
                Assert.Throws<InvalidOperationException>(() => { if (character) stale.OpenTextReader(); else stale.OpenRead(); });
            }
            catch (Exception error) when (server.Completion.IsFaulted) { throw new AggregateException(error, server.Completion.Exception!); }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)] [InlineData(true, false)]
        [InlineData(false, true)] [InlineData(true, true)]
        public async Task OwnedBatchNonQueryPublishesLobOutputsWithTheBatchLifetime(bool asynchronous, bool scalar)
        {
            var server = Server(async channel =>
            {
                await BatchReturningRequest(channel, 1);
                await ReturningResourceReply(channel, 90, 210, ReturningResourceField(new byte[] { 7 }, false, false, true));
                await StatementCommand(channel, 25); Assert.Equal(0, await channelStreamRead(channel));
            }, lobLocators: true, serverVersion: "4.4.2.2");
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port) + ";Use Oracle Prepare Execute=true;Use Batch Multi Send=true");
            try
            {
                using var batch = new OceanBaseBatch(connection); var output = BatchReturningItem(batch, 1);
                output.ResetDbType(); output.OceanBaseDbType = OceanBaseDbType.Blob; output.Value = "old";
                if (scalar) Assert.Null(asynchronous ? await batch.ExecuteScalarAsync() : batch.ExecuteScalar());
                else Assert.Equal(1, asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery());
                using var lob = Assert.IsType<OceanBaseLob>(output.Value); Assert.Equal(ConnectionState.Open, connection.State);
                using (var source = lob.OpenRead()) Assert.Equal(7, source.ReadByte());
                if (asynchronous) await batch.DisposeAsync(); else batch.Dispose();
                Assert.Equal(ConnectionState.Closed, connection.State);
            }
            catch (Exception error) when (server.Completion.IsFaulted) { throw new AggregateException(error, server.Completion.Exception!); }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)] [InlineData(true, false)]
        [InlineData(false, true)] [InlineData(true, true)]
        public async Task OwnedBatchScalarReleasesUndeliveredLobsWhenTheSuffixFails(bool asynchronous, bool multiSend)
        {
            var server = Server(async channel =>
            {
                if (multiSend)
                {
                    await BatchLobPrepared(channel, 90, 210);
                    await StatementCommand(channel, 22); await Prepared(channel, 91, 0);
                    await StatementCommand(channel, 23); await StatementCommand(channel, 23);
                    await BatchLobReply(channel, 210, InlineLob(new byte[] { 7 }));
                    channel.BeginResponse(); await channel.WritePacketAsync(Error());
                }
                else
                {
                    await LobQueryReply(channel, 210, InlineLob(new byte[] { 7 }));
                    await Query(channel, "select failure"); await channel.WritePacketAsync(Error());
                }
                Assert.Equal(0, await channelStreamRead(channel));
            }, lobLocators: true);
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port) + ";Use Batch Multi Send=" + multiSend);
            try
            {
                using var batch = new OceanBaseBatch(connection);
                BatchItem(batch, "select lob_value"); var suffix = BatchItem(batch, "select failure");
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? batch.ExecuteScalarAsync() : Task.FromResult(batch.ExecuteScalar()));
                Assert.Same(suffix, error.BatchCommand); Assert.Equal(ConnectionState.Closed, connection.State);
            }
            catch (Exception error) when (server.Completion.IsFaulted) { throw new AggregateException(error, server.Completion.Exception!); }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
