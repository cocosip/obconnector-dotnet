using System;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        [Theory]
        [InlineData(false, false, "reader")] [InlineData(true, false, "reader")]
        [InlineData(false, true, "reader")] [InlineData(true, true, "reader")]
        [InlineData(false, false, "scalar")] [InlineData(true, false, "scalar")]
        [InlineData(false, true, "scalar")] [InlineData(true, true, "scalar")]
        [InlineData(false, false, "nonquery")] [InlineData(true, false, "nonquery")]
        [InlineData(false, true, "nonquery")] [InlineData(true, true, "nonquery")]
        public async Task BatchMultiSendNamedComplexResultsKeepTheirSourceContext(bool asynchronous, bool source, string api)
        {
            var server = Server(async channel =>
            {
                await ComplexScalarType(channel, "TypeOwner");
                for (byte i = 0; i < 2; i++)
                {
                    await StatementCommand(channel, 22);
                    await channel.WritePacketAsync(new byte[] { 0, (byte)(90 + i), 0, 0, 0, 3, 0, 0, 0, 0, 0, 0 });
                    await channel.WritePacketAsync(source ? SourceComplexColumn(table: "Source" + i, typeName: "") : ComplexColumn("TypeOwner"));
                    await channel.WritePacketAsync(Column("national", 207, charset: 54));
                    await channel.WritePacketAsync(ValueColumnPacket(201, scale: 9));
                    await channel.WritePacketAsync(Eof());
                    if (source)
                    {
                        await SourceTypeLookup(channel, table: "Source" + i, rows: new[] { new string?[] { "TypeOwner", "Thing" } });
                        await StatementCommand(channel, 25);
                        channel.BeginCommand(); var zone = await channel.ReadPacketAsync();
                        Assert.Equal(SessionTimeZoneSql, System.Text.Encoding.UTF8.GetString(zone, 1, zone.Length - 1));
                        await TimeZoneReply(channel, "+09:00");
                        channel.BeginCommand(); var national = await channel.ReadPacketAsync();
                        Assert.Equal(NationalCharacterSetSql, System.Text.Encoding.UTF8.GetString(national, 1, national.Length - 1));
                        await NationalCharacterSetReply(channel, "utf16");
                    }
                }
                Assert.Equal(90, (await StatementCommand(channel, 23))[1]);
                Assert.Equal(91, (await StatementCommand(channel, 23))[1]);
                for (byte i = 0; i < 2; i++)
                {
                    channel.BeginResponse();
                    await channel.WritePacketAsync(new byte[] { 3 });
                    await channel.WritePacketAsync(source ? SourceComplexColumn(table: "Source" + i) : ComplexColumn("TypeOwner"));
                    await channel.WritePacketAsync(Column("national", 207, charset: 54));
                    await channel.WritePacketAsync(ValueColumnPacket(201, scale: 9));
                    await channel.WritePacketAsync(Eof());
                    var encoding = source ? Internal.Protocol.NationalCharacterSet.Utf16 : System.Text.Encoding.UTF8;
                    var scalars = WithLength(encoding.GetBytes("中文🙂 "))
                        .Concat(WithLength(NanosPayload(123456789, 9)));
                    await channel.WritePacketAsync(new byte[] { 0, 0, 1, 0, 1, (byte)(42 + i) }.Concat(scalars).ToArray());
                    await channel.WritePacketAsync(new byte[] { 0, 4 }.Concat(scalars).ToArray());
                    await channel.WritePacketAsync(Eof());
                }
                await StatementCommand(channel, 25); await StatementCommand(channel, 25);
                channel.BeginCommand(); await Query(channel, "after"); await channel.WritePacketAsync(Ok(7));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Batch Multi Send=true;Batch Multi Send Number=2");
            try
            {
                await connection.OpenAsync();
                var type = await connection.GetComplexTypeAsync("TypeOwner", "Thing");
                OceanBaseComplexValue? retained = null;
                using (var batch = connection.CreateBatch())
                {
                    BatchItem(batch, "select value from first_table"); BatchItem(batch, "select value from second_table");
                    if (api == "reader")
                    {
                        using var reader = asynchronous ? await batch.ExecuteReaderAsync() : batch.ExecuteReader();
                        for (var i = 0; i < 2; i++)
                        {
                            Assert.Equal(typeof(OceanBaseComplexValue), reader.GetFieldType(0));
                            Assert.Equal("Thing", reader.GetDataTypeName(0));
                            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                            retained = reader.GetFieldValue<OceanBaseComplexValue>(0);
                            Assert.Same(type, retained.Type); Assert.Equal(new byte[] { (byte)(42 + i) }, retained.GetValues()[0]);
                            Assert.Equal("中文🙂 ", reader.GetString(1));
                            Assert.Equal(source ? "+09:00" : "+00:00", reader.GetFieldValue<OceanBaseTimestampLocalTimeZone>(2).SessionTimeZone);
                            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read()); Assert.True(reader.IsDBNull(0));
                            Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                            Assert.Equal(i == 0, asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                        }
                    }
                    else if (api == "scalar")
                    {
                        retained = Assert.IsType<OceanBaseComplexValue>(asynchronous ? await batch.ExecuteScalarAsync() : batch.ExecuteScalar());
                        Assert.Same(type, retained.Type); Assert.Equal(new byte[] { 42 }, retained.GetValues()[0]);
                    }
                    else Assert.Equal(-1, asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery());
                }
                connection.ClearComplexTypeCache();
                if (retained != null) Assert.NotEmpty(retained.GetValues());
                using var after = connection.CreateCommand(); after.CommandText = "after";
                Assert.Equal(7, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)] [InlineData(true, false)]
        [InlineData(false, true)] [InlineData(true, true)]
        public async Task BatchMultiSendRejectsUnexpectedResourceResultMetadata(bool asynchronous, bool complex)
        {
            var server = Server(async channel =>
            {
                if (complex) await ComplexScalarType(channel, scalar: "BLOB", collection: false);
                await StatementCommand(channel, 22); await Prepared(channel, 90, 0, 1);
                await StatementCommand(channel, 23);
                channel.BeginResponse(); await channel.WritePacketAsync(new byte[] { 1 });
                await channel.WritePacketAsync(complex ? ComplexColumn() : Column("lob", 210));
                // A forbidden live column must fail before waiting for EOF or allocating a locator.
                try { Assert.Equal(0, await channelStreamRead(channel)); } catch (IOException) { }
            }, lobLocators: true);
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port) + ";Use Batch Multi Send=true");
            try
            {
                await connection.OpenAsync();
                if (complex) await connection.GetComplexTypeAsync("Owner", "Thing");
                using var batch = connection.CreateBatch(); BatchItem(batch, "select value from t");
                var error = await Assert.ThrowsAsync<OceanBaseException>(async () =>
                {
                    using var reader = asynchronous ? await batch.ExecuteReaderAsync() : batch.ExecuteReader();
                });
                Assert.True(error.IsExecutionOutcomeUnknown); Assert.IsType<NotSupportedException>(error.InnerException);
                Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)] [InlineData(true)]
        public async Task BatchMultiSendNeverUsesAnotherRequestsComplexSource(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await ComplexScalarType(channel, "TypeOwner");
                for (byte i = 0; i < 2; i++)
                {
                    await StatementCommand(channel, 22);
                    await channel.WritePacketAsync(new byte[] { 0, (byte)(90 + i), 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
                    await channel.WritePacketAsync(SourceComplexColumn(table: "Source" + i)); await channel.WritePacketAsync(Eof());
                    await SourceTypeLookup(channel, table: "Source" + i, rows: new[] { new string?[] { "TypeOwner", "Thing" } });
                    await StatementCommand(channel, 25);
                }
                await StatementCommand(channel, 23); await StatementCommand(channel, 23);
                channel.BeginResponse(); await channel.WritePacketAsync(new byte[] { 1 });
                await channel.WritePacketAsync(SourceComplexColumn(table: "Source1"));
                try { Assert.Equal(0, await channelStreamRead(channel)); } catch (IOException) { }
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Batch Multi Send=true;Batch Multi Send Number=2");
            try
            {
                await connection.OpenAsync(); await connection.GetComplexTypeAsync("TypeOwner", "Thing");
                using var batch = connection.CreateBatch();
                BatchItem(batch, "select value from first_table"); BatchItem(batch, "select value from second_table");
                var error = await Assert.ThrowsAsync<OceanBaseException>(async () =>
                {
                    using var reader = asynchronous ? await batch.ExecuteReaderAsync() : batch.ExecuteReader();
                });
                Assert.Same(batch.BatchCommands[0], error.BatchCommand); Assert.True(error.IsExecutionOutcomeUnknown);
                Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, "anonymous")] [InlineData(true, "anonymous")]
        [InlineData(false, "unknown")] [InlineData(true, "unknown")]
        [InlineData(false, "lob")] [InlineData(true, "lob")]
        public async Task BatchMultiSendRejectsUnsupportedPreparedComplexResults(bool asynchronous, string kind)
        {
            var server = Server(async channel =>
            {
                if (kind == "lob") await ComplexScalarType(channel, scalar: "CLOB", collection: false);
                await StatementCommand(channel, 22);
                await channel.WritePacketAsync(new byte[] { 0, 90, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
                await channel.WritePacketAsync(kind == "anonymous" ? ComplexColumn(name: "") : ComplexColumn());
                await channel.WritePacketAsync(Eof());
                Assert.Equal(90, (await StatementCommand(channel, 25))[1]);
                Assert.Equal(0, await channelStreamRead(channel));
            }, lobLocators: true);
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port) + ";Use Batch Multi Send=true");
            try
            {
                await connection.OpenAsync();
                if (kind == "lob") await connection.GetComplexTypeAsync("Owner", "Thing");
                using (var batch = connection.CreateBatch())
                {
                    BatchItem(batch, "select value from t");
                    if (asynchronous) await Assert.ThrowsAsync<NotSupportedException>(() => batch.ExecuteScalarAsync());
                    else Assert.Throws<NotSupportedException>(() => batch.ExecuteScalar());
                }
                Assert.Equal(ConnectionState.Open, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
